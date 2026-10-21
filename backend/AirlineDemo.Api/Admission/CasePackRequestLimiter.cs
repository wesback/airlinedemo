using System.Data;
using System.Security.Cryptography;
using System.Text;
using AirlineDemo.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Admission;

public interface ICasePackRequestWindowStore
{
    Task<bool> TryRecordAttemptAsync(
        string userId,
        string endpoint,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken);
}

public sealed class CasePackRequestWindowRow
{
    public Guid AttemptId { get; set; }
    public byte[] UserHash { get; set; } = [];
    public string Endpoint { get; set; } = string.Empty;
    public long RequestedAtTicks { get; set; }
}

public sealed class CasePackRequestLimiter(ICasePackRequestWindowStore store, TimeProvider timeProvider)
{
    public const string CaseListEndpoint = "case-list";
    public const string SceneEndpoint = "scene";
    public const int MaxRequests = 30;
    public static readonly TimeSpan RollingWindow = TimeSpan.FromSeconds(60);

    public Task<bool> TryRecordAttemptAsync(
        string authenticatedUserId,
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedUserId);
        if (endpoint is not (CaseListEndpoint or SceneEndpoint))
            throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Unsupported case-pack read endpoint.");

        return store.TryRecordAttemptAsync(authenticatedUserId, endpoint, timeProvider.GetUtcNow(), cancellationToken);
    }
}

public sealed class SqlCasePackRequestWindowStore(DbContextOptions<CaseStateDbContext> options) : ICasePackRequestWindowStore
{
    public async Task<bool> TryRecordAttemptAsync(
        string userId,
        string endpoint,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken)
    {
        var now = attemptedAt.UtcDateTime.Ticks;
        var cutoff = now - CasePackRequestLimiter.RollingWindow.Ticks;
        var userHash = Hash(userId);
        await using var db = new CaseStateDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        if (!string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT [AttemptId] FROM [dbo].[case_pack_request_windows] WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE [AttemptId] = {Guid.Empty}",
                cancellationToken);
        }

        await db.CasePackRequestWindows
            .Where(row => row.AttemptId != Guid.Empty && row.RequestedAtTicks <= cutoff)
            .ExecuteDeleteAsync(cancellationToken);
        var attempts = db.CasePackRequestWindows.Where(row =>
            row.UserHash == userHash && row.Endpoint == endpoint && row.RequestedAtTicks > cutoff);
        if (await attempts.CountAsync(cancellationToken) >= CasePackRequestLimiter.MaxRequests)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        db.CasePackRequestWindows.Add(new CasePackRequestWindowRow
        {
            AttemptId = Guid.NewGuid(),
            UserHash = userHash,
            Endpoint = endpoint,
            RequestedAtTicks = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static byte[] Hash(string userId) => SHA256.HashData(Encoding.UTF8.GetBytes(userId));
}

public sealed class InMemoryCasePackRequestWindowStore : ICasePackRequestWindowStore
{
    private sealed record Attempt(string UserHash, string Endpoint, long RequestedAtTicks);

    private readonly object _gate = new();
    private readonly List<Attempt> _attempts = [];

    public Task<bool> TryRecordAttemptAsync(
        string userId,
        string endpoint,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = attemptedAt.UtcDateTime.Ticks;
        var cutoff = now - CasePackRequestLimiter.RollingWindow.Ticks;
        var userHash = Convert.ToHexString(Hash(userId));
        lock (_gate)
        {
            _attempts.RemoveAll(attempt => attempt.RequestedAtTicks <= cutoff);
            if (_attempts.Count(attempt => attempt.UserHash == userHash
                    && attempt.Endpoint == endpoint && attempt.RequestedAtTicks > cutoff)
                >= CasePackRequestLimiter.MaxRequests)
                return Task.FromResult(false);

            _attempts.Add(new Attempt(userHash, endpoint, now));
            return Task.FromResult(true);
        }
    }

    internal int ActiveCount(string userId, string endpoint, DateTimeOffset now)
    {
        var userHash = Convert.ToHexString(Hash(userId));
        var cutoff = now.UtcDateTime.Ticks - CasePackRequestLimiter.RollingWindow.Ticks;
        lock (_gate)
            return _attempts.Count(attempt => attempt.UserHash == userHash
                && attempt.Endpoint == endpoint && attempt.RequestedAtTicks > cutoff);
    }

    private static byte[] Hash(string userId) => SHA256.HashData(Encoding.UTF8.GetBytes(userId));
}

public static class CasePackRequestLimiterRegistration
{
    public static IServiceCollection AddCasePackRequestLimiter(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (string.Equals(configuration["Persistence:CaseStateProvider"], "Sql", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = configuration.GetConnectionString(CaseStateStoreRegistration.ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("ConnectionStrings:CaseState is required when Persistence:CaseStateProvider is Sql.");
            CaseStateStoreRegistration.ValidateManagedIdentityConnectionString(connectionString);
            var options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlServer(connectionString).Options;
            services.AddSingleton<ICasePackRequestWindowStore>(new SqlCasePackRequestWindowStore(options));
        }
        else
        {
            services.AddSingleton<ICasePackRequestWindowStore, InMemoryCasePackRequestWindowStore>();
        }

        services.AddSingleton<CasePackRequestLimiter>();
        return services;
    }
}
