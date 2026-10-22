using System.Data;
using System.Security.Cryptography;
using System.Text;
using AirlineDemo.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using AirlineDemo.Api.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
    public const string ResetEndpoint = "reset-blocked-variant";
    public const string ResetBlockedVariantEndpoint = ResetEndpoint;
    public const int MaxRequests = 30;
    public const int MaxResetRequests = 3;
    public const int MaxResetAttempts = MaxResetRequests;
    public static readonly TimeSpan RollingWindow = TimeSpan.FromSeconds(60);

    public Task<bool> TryRecordAttemptAsync(
        string authenticatedUserId,
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedUserId);
        if (endpoint is not (CaseListEndpoint or SceneEndpoint or ResetEndpoint))
            throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Unsupported case-pack endpoint.");

        return store.TryRecordAttemptAsync(authenticatedUserId, endpoint, timeProvider.GetUtcNow(), cancellationToken);
    }

    public static int MaximumFor(string endpoint) => endpoint switch
    {
        ResetEndpoint => MaxResetRequests,
        CaseListEndpoint or SceneEndpoint => MaxRequests,
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Unsupported case-pack endpoint."),
    };
}

public sealed class SqlCasePackRequestWindowStore(DbContextOptions<CaseStateDbContext> options) : ICasePackRequestWindowStore
{
    private static readonly SemaphoreSlim SqliteGate = new(1, 1);

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
        var sqlite = string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal);
        if (sqlite)
            await SqliteGate.WaitAsync(cancellationToken);

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            if (!sqlite)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT [AttemptId] FROM [dbo].[case_pack_request_windows] WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE [AttemptId] = {Guid.Empty}",
                    cancellationToken);
            }

            if (sqlite)
            {
                var expired = await db.CasePackRequestWindows
                    .Where(row => row.AttemptId != Guid.Empty && row.RequestedAtTicks <= cutoff)
                    .ToListAsync(cancellationToken);
                db.CasePackRequestWindows.RemoveRange(expired);
                await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                await db.CasePackRequestWindows
                    .Where(row => row.AttemptId != Guid.Empty && row.RequestedAtTicks <= cutoff)
                    .ExecuteDeleteAsync(cancellationToken);
            }
            var attempts = db.CasePackRequestWindows.Where(row =>
                row.UserHash == userHash && row.Endpoint == endpoint && row.RequestedAtTicks > cutoff);
            var maximum = CasePackRequestLimiter.MaximumFor(endpoint);
            if (await attempts.CountAsync(cancellationToken) >= maximum)
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
        finally
        {
            if (sqlite)
                SqliteGate.Release();
        }
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
            var maximum = CasePackRequestLimiter.MaximumFor(endpoint);
            if (_attempts.Count(attempt => attempt.UserHash == userHash
                    && attempt.Endpoint == endpoint && attempt.RequestedAtTicks > cutoff)
                >= maximum)
                return Task.FromResult(false);

            _attempts.Add(new Attempt(userHash, endpoint, now));
            return Task.FromResult(true);
        }
    }

    internal int TotalCount(string endpoint)
    {
        lock (_gate)
            return _attempts.Count(attempt => attempt.Endpoint == endpoint);
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
        services.AddSingleton<IStartupFilter, CasePackResetRequestRateLimitStartupFilter>();
        return services;
    }
}

public sealed class CasePackResetRequestRateLimitStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<CasePackResetRequestRateLimitMiddleware>();
        next(app);
    };
}

public sealed class CasePackResetRequestRateLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext http,
        DemoSessionStore sessions,
        CasePackRequestLimiter limiter)
    {
        if (!IsResetRequest(http.Request))
        {
            await next(http);
            return;
        }

        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null)
        {
            await next(http);
            return;
        }

        if (!await limiter.TryRecordAttemptAsync(session.User.Id, CasePackRequestLimiter.ResetEndpoint, http.RequestAborted))
        {
            http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await http.Response.WriteAsJsonAsync(new
            {
                contractVersion = 1,
                error = new { code = "rate_limit_exceeded", message = "rate_limit_exceeded" },
            }, http.RequestAborted);
            return;
        }

        if (!string.Equals(session.User.Role, "presenter", StringComparison.Ordinal)
            && TryGetResetRunId(http.Request, out var runId)
            && session.User.CaseScopes.Contains(runId))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new { error = "forbidden" }, http.RequestAborted);
            return;
        }

        await next(http);
    }

    private static bool IsResetRequest(HttpRequest request)
    {
        return TryGetResetRunId(request, out _) && HttpMethods.IsPost(request.Method);
    }

    private static bool TryGetResetRunId(HttpRequest request, out string runId)
    {
        var segments = request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is { Length: 4 }
            && string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[1], "runs", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[3], "reset", StringComparison.OrdinalIgnoreCase))
        {
            runId = segments[2];
            return true;
        }

        runId = string.Empty;
        return false;
    }
}
