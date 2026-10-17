using System.Data;
using System.Security.Cryptography;
using System.Text;
using AirlineDemo.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.SourceSystems;
public interface ISourceReadAdmissionStore
{
    Task<Guid?> TryAcquireAsync(string sessionId, string caseId, DateTimeOffset requestedAt, CancellationToken cancellationToken);
    Task<bool> ReleaseAsync(Guid leaseId, DateTimeOffset releasedAt, CancellationToken cancellationToken);
}
public sealed class SourceReadWindowRow
{
    public Guid LeaseId { get; set; }
    public byte[] CaseHash { get; set; } = [];
    public byte[] SessionHash { get; set; } = [];
    public long RequestedAtTicks { get; set; }
    public long ExpiresAtTicks { get; set; }
    public long? ReleasedAtTicks { get; set; }
}
public sealed class SqlSourceReadAdmissionStore(DbContextOptions<CaseStateDbContext> options) : ISourceReadAdmissionStore
{
    public async Task<Guid?> TryAcquireAsync(string sessionId, string caseId, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        var now = requestedAt.UtcDateTime.Ticks;
        await using var db = new CaseStateDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT [LeaseId] FROM [dbo].[source_read_windows] WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE [LeaseId] = {Guid.Empty}",
            cancellationToken);
        var caseHash = Hash(caseId);
        var sessionHash = Hash(sessionId);
        var rows = db.SourceReadWindows.Where(row => row.CaseHash == caseHash);
        if (await rows.CountAsync(row => row.SessionHash == sessionHash
                && row.RequestedAtTicks > now - SourceReadBudget.RollingWindow.Ticks, cancellationToken) >= SourceReadBudget.MaxRequests
            || await rows.CountAsync(row => row.ReleasedAtTicks == null && row.ExpiresAtTicks > now, cancellationToken)
                >= SourceReadBudget.MaxInFlightPerCase)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        var leaseId = Guid.NewGuid();
        var reusableLeaseId = await db.SourceReadWindows
            .Where(row => row.LeaseId != Guid.Empty && row.RequestedAtTicks <= now - SourceReadBudget.RollingWindow.Ticks
                && (row.ReleasedAtTicks != null || row.ExpiresAtTicks <= now))
            .OrderBy(row => row.RequestedAtTicks)
            .Select(row => row.LeaseId)
            .FirstOrDefaultAsync(cancellationToken);
        if (reusableLeaseId != Guid.Empty)
        {
            await db.SourceReadWindows.Where(row => row.LeaseId == reusableLeaseId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(row => row.LeaseId, leaseId)
                    .SetProperty(row => row.CaseHash, caseHash)
                    .SetProperty(row => row.SessionHash, sessionHash)
                    .SetProperty(row => row.RequestedAtTicks, now)
                    .SetProperty(row => row.ExpiresAtTicks, now + SourceReadBudget.ReadDeadline.Ticks)
                    .SetProperty(row => row.ReleasedAtTicks, (long?)null), cancellationToken);
        }
        else
        {
            db.SourceReadWindows.Add(new SourceReadWindowRow { LeaseId = leaseId, CaseHash = caseHash, SessionHash = sessionHash,
                RequestedAtTicks = now, ExpiresAtTicks = now + SourceReadBudget.ReadDeadline.Ticks });
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return leaseId;
    }
    public async Task<bool> ReleaseAsync(Guid leaseId, DateTimeOffset releasedAt, CancellationToken cancellationToken)
    {
        await using var db = new CaseStateDbContext(options);
        return await db.SourceReadWindows
            .Where(row => row.LeaseId == leaseId && row.ReleasedAtTicks == null)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.ReleasedAtTicks, releasedAt.UtcDateTime.Ticks), cancellationToken) == 1;
    }
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
public sealed class InMemorySourceReadAdmissionStore : ISourceReadAdmissionStore
{
    private sealed record Window(Guid LeaseId, string CaseHash, string SessionHash, long RequestedAtTicks,
        long ExpiresAtTicks, long? ReleasedAtTicks);

    private readonly Lock _gate = new();
    private readonly List<Window> _windows = [];
    internal int ActiveCount(string caseId, DateTimeOffset now)
    {
        var caseHash = Convert.ToHexString(Hash(caseId));
        lock (_gate)
            return _windows.Count(window => window.CaseHash == caseHash && window.ReleasedAtTicks is null
                && window.ExpiresAtTicks > now.UtcDateTime.Ticks);
    }
    public Task<Guid?> TryAcquireAsync(string sessionId, string caseId, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = requestedAt.UtcDateTime.Ticks;
        var caseHash = Convert.ToHexString(Hash(caseId));
        var sessionHash = Convert.ToHexString(Hash(sessionId));
        lock (_gate)
        {
            _windows.RemoveAll(window => window.RequestedAtTicks <= now - SourceReadBudget.RollingWindow.Ticks
                && (window.ReleasedAtTicks is not null || window.ExpiresAtTicks <= now));
            if (_windows.Count(window => window.CaseHash == caseHash && window.SessionHash == sessionHash
                    && window.RequestedAtTicks > now - SourceReadBudget.RollingWindow.Ticks) >= SourceReadBudget.MaxRequests
                || _windows.Count(window => window.CaseHash == caseHash && window.ReleasedAtTicks is null
                    && window.ExpiresAtTicks > now) >= SourceReadBudget.MaxInFlightPerCase)
                return Task.FromResult<Guid?>(null);

            var id = Guid.NewGuid();
            _windows.Add(new Window(id, caseHash, sessionHash, now, now + SourceReadBudget.ReadDeadline.Ticks, null));
            return Task.FromResult<Guid?>(id);
        }
    }
    public Task<bool> ReleaseAsync(Guid leaseId, DateTimeOffset releasedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var index = _windows.FindIndex(window => window.LeaseId == leaseId && window.ReleasedAtTicks is null);
            if (index < 0) return Task.FromResult(false);
            _windows[index] = _windows[index] with { ReleasedAtTicks = releasedAt.UtcDateTime.Ticks };
            return Task.FromResult(true);
        }
    }
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
public static class SourceReadAdmissionRegistration
{
    public static IServiceCollection AddSourceReadBudget(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Persistence:CaseStateProvider"];
        if (string.Equals(provider, "Sql", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = configuration.GetConnectionString("CaseState");
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("ConnectionStrings:CaseState is required when Persistence:CaseStateProvider is Sql.");
            CaseStateStoreRegistration.ValidateManagedIdentityConnectionString(connectionString);
            var options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlServer(connectionString).Options;
            services.AddSingleton<ISourceReadAdmissionStore>(new SqlSourceReadAdmissionStore(options));
        }
        else
        {
            services.AddSingleton<ISourceReadAdmissionStore, InMemorySourceReadAdmissionStore>();
        }

        services.AddSingleton<SourceReadBudget>();
        return services;
    }
}
