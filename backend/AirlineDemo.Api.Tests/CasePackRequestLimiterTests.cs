using AirlineDemo.Api.Admission;
using AirlineDemo.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AirlineDemo.Api.Tests;

public sealed class CasePackRequestLimiterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);

    private sealed class AdjustableTime : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private sealed class RacingStore : ICasePackRequestWindowStore
    {
        private readonly List<(string User, string Endpoint, DateTimeOffset At)> _attempts = [];
        private bool _raced;

        public int Count => _attempts.Count;

        public Task<bool> TryRecordAttemptAsync(
            string userId,
            string endpoint,
            DateTimeOffset attemptedAt,
            CancellationToken cancellationToken)
        {
            var cutoff = attemptedAt - CasePackRequestLimiter.RollingWindow;
            var activeCount = _attempts.Count(attempt => attempt.User == userId
                && attempt.Endpoint == endpoint && attempt.At > cutoff);
            if (!_raced && activeCount == CasePackRequestLimiter.MaxRequests - 1)
            {
                _attempts.Add((userId, endpoint, attemptedAt));
                _raced = true;
            }

            var currentCount = _attempts.Count(attempt => attempt.User == userId
                && attempt.Endpoint == endpoint && attempt.At > cutoff);
            if (activeCount >= CasePackRequestLimiter.MaxRequests
                || currentCount >= CasePackRequestLimiter.MaxRequests)
                return Task.FromResult(false);

            _attempts.Add((userId, endpoint, attemptedAt));
            return Task.FromResult(true);
        }
    }

    [Theory]
    [InlineData("case-list", 28, 0, "user-a", "case-list", "user-a", true)]
    [InlineData("case-list", 29, 0, "user-a", "case-list", "user-a", true)]
    [InlineData("case-list", 30, 0, "user-a", "case-list", "user-a", false)]
    [InlineData("scene", 28, 0, "user-a", "scene", "user-a", true)]
    [InlineData("scene", 29, 0, "user-a", "scene", "user-a", true)]
    [InlineData("scene", 30, 0, "user-a", "scene", "user-a", false)]
    [InlineData("case-list", 30, 59, "user-a", "case-list", "user-a", false)]
    [InlineData("scene", 30, 59, "user-a", "scene", "user-a", false)]
    [InlineData("case-list", 30, 60, "user-a", "case-list", "user-a", true)]
    [InlineData("scene", 30, 60, "user-a", "scene", "user-a", true)]
    [InlineData("case-list", 30, 0, "user-a", "case-list", "user-b", true)]
    [InlineData("case-list", 30, 0, "user-a", "scene", "user-a", true)]
    [InlineData("scene", 30, 0, "user-a", "case-list", "user-a", true)]
    public async Task AC029_ReadLimitsUsePerUserEndpointRollingWindows(
        string seededEndpoint,
        int activeAttempts,
        int oldestAgeSeconds,
        string seededUser,
        string requestEndpoint,
        string requestUser,
        bool expected)
    {
        var time = new AdjustableTime();
        var store = new InMemoryCasePackRequestWindowStore();
        var limiter = new CasePackRequestLimiter(store, time);
        var attemptTime = Now.AddSeconds(-oldestAgeSeconds);
        for (var attempt = 0; attempt < activeAttempts; attempt++)
        {
            Assert.True(await store.TryRecordAttemptAsync(seededUser, seededEndpoint, attemptTime, CancellationToken.None));
        }

        Assert.Equal(expected, await limiter.TryRecordAttemptAsync(requestUser, requestEndpoint));
    }

    [Fact]
    public async Task AC029_ConcurrentReadAdmissionIsAtomic()
    {
        var time = new AdjustableTime();
        var store = new InMemoryCasePackRequestWindowStore();
        var limiter = new CasePackRequestLimiter(store, time);
        for (var attempt = 0; attempt < CasePackRequestLimiter.MaxRequests - 1; attempt++)
            Assert.True(await limiter.TryRecordAttemptAsync("user-a", CasePackRequestLimiter.CaseListEndpoint));

        var ready = 0;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> ConcurrentAttempt()
        {
            if (Interlocked.Increment(ref ready) == 2) start.TrySetResult();
            await start.Task;
            return await limiter.TryRecordAttemptAsync("user-a", CasePackRequestLimiter.CaseListEndpoint);
        }
        var results = await Task.WhenAll(ConcurrentAttempt(), ConcurrentAttempt());
        Assert.Equal(1, results.Count(result => result));
        Assert.Equal(1, results.Count(result => !result));
        Assert.Equal(CasePackRequestLimiter.MaxRequests,
            store.ActiveCount("user-a", CasePackRequestLimiter.CaseListEndpoint, Now));

        var racingStore = new RacingStore();
        var racingLimiter = new CasePackRequestLimiter(racingStore, time);
        for (var attempt = 0; attempt < CasePackRequestLimiter.MaxRequests - 1; attempt++)
            Assert.True(await racingLimiter.TryRecordAttemptAsync("user-a", CasePackRequestLimiter.CaseListEndpoint));

        Assert.False(await racingLimiter.TryRecordAttemptAsync("user-a", CasePackRequestLimiter.CaseListEndpoint));
        Assert.Equal(CasePackRequestLimiter.MaxRequests, racingStore.Count);
    }

    [Fact]
    public async Task SqlStorePersistsAttemptsAndExcludesTheExactExpiryBoundary()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlite(connection).Options;
        await using (var db = new CaseStateDbContext(options))
            await db.Database.MigrateAsync();

        var store = new SqlCasePackRequestWindowStore(options);
        for (var attempt = 0; attempt < CasePackRequestLimiter.MaxRequests; attempt++)
        {
            Assert.True(await store.TryRecordAttemptAsync(
                "user-a", CasePackRequestLimiter.SceneEndpoint, Now, CancellationToken.None));
        }

        Assert.False(await store.TryRecordAttemptAsync(
            "user-a", CasePackRequestLimiter.SceneEndpoint, Now, CancellationToken.None));
        Assert.True(await store.TryRecordAttemptAsync(
            "user-a", CasePackRequestLimiter.SceneEndpoint,
            Now + CasePackRequestLimiter.RollingWindow, CancellationToken.None));
    }
}
