using AirlineDemo.Api.Admission;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text.Json;

namespace AirlineDemo.Api.Tests;

public sealed class CasePackRequestLimiterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);

    private sealed class AdjustableTime : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private static WebApplicationFactory<Program> ReadFactory(
        InMemoryCasePackRequestWindowStore store,
        AdjustableTime time) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(time);
                services.RemoveAll<ICasePackRequestWindowStore>();
                services.AddSingleton<ICasePackRequestWindowStore>(store);
            }));

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
    public async Task UnsupportedEndpointIsRejected()
    {
        var limiter = new CasePackRequestLimiter(new InMemoryCasePackRequestWindowStore(), new AdjustableTime());

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => limiter.TryRecordAttemptAsync("user-a", "unknown-endpoint"));

        Assert.Equal("endpoint", error.ParamName);
    }

    [Fact]
    public void RegistrationUsesTheConfiguredCaseStateProvider()
    {
        var fileConfiguration = new ConfigurationBuilder().Build();
        var fileServices = new ServiceCollection();
        fileServices.AddSingleton<TimeProvider>(new AdjustableTime());
        fileServices.AddCasePackRequestLimiter(fileConfiguration);
        using (var fileProvider = fileServices.BuildServiceProvider())
        {
            Assert.IsType<InMemoryCasePackRequestWindowStore>(
                fileProvider.GetRequiredService<ICasePackRequestWindowStore>());
            Assert.IsType<CasePackRequestLimiter>(fileProvider.GetRequiredService<CasePackRequestLimiter>());
        }

        var missingConnection = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Persistence:CaseStateProvider"] = "Sql" })
            .Build();
        Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddCasePackRequestLimiter(missingConnection));

        const string connectionString =
            "Server=tcp:case-state.database.windows.net,1433;Database=casestate;Encrypt=True;Authentication=Active Directory Managed Identity;";
        var sqlConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:CaseStateProvider"] = "Sql",
                ["ConnectionStrings:CaseState"] = connectionString,
            })
            .Build();
        var sqlServices = new ServiceCollection();
        sqlServices.AddCasePackRequestLimiter(sqlConfiguration);
        using var sqlProvider = sqlServices.BuildServiceProvider();

        Assert.IsType<SqlCasePackRequestWindowStore>(
            sqlProvider.GetRequiredService<ICasePackRequestWindowStore>());
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

    [Theory]
    [InlineData("/api/v1/cases", "case-list")]
    [InlineData("/api/v1/cases/cp-case-a/scene", "scene")]
    public async Task AC029_ReadRoutesReturn429WithoutCaseDataWhenWindowIsFull(string path, string endpoint)
    {
        var time = new AdjustableTime();
        var store = new InMemoryCasePackRequestWindowStore();
        for (var attempt = 0; attempt < CasePackRequestLimiter.MaxRequests; attempt++)
            Assert.True(await store.TryRecordAttemptAsync("user-a", endpoint, Now, CancellationToken.None));

        using var factory = ReadFactory(store, time);
        using var client = factory.CreateClient();
        var sessions = factory.Services.GetRequiredService<DemoSessionStore>();
        var session = sessions.Create(new DemoUser("user-a", "Reader", "presenter", ["cp-case-a"]));
        client.DefaultRequestHeaders.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={session.Id}");

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        using var json = JsonDocument.Parse(responseBody);
        var root = json.RootElement;
        Assert.Equal(2, root.EnumerateObject().Count());
        Assert.Equal(1, root.GetProperty("contractVersion").GetInt32());
        var error = root.GetProperty("error");
        Assert.Equal(2, error.EnumerateObject().Count());
        Assert.Equal("rate_limit_exceeded", error.GetProperty("code").GetString());
        Assert.Equal("rate_limit_exceeded", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AC029_CaseListReturnsOnlyCasesInAuthenticatedSessionScope()
    {
        var time = new AdjustableTime();
        using var factory = ReadFactory(new InMemoryCasePackRequestWindowStore(), time);
        using var client = factory.CreateClient();
        var snapshot = factory.Services.GetRequiredService<IAircraftCasePackSnapshotStore>().Read();
        Assert.NotNull(snapshot);
        Assert.True(snapshot.Cases.Count > 1);
        var visibleCase = snapshot.Cases[0];
        var session = factory.Services.GetRequiredService<DemoSessionStore>()
            .Create(new DemoUser("user-a", "Reader", "presenter", [visibleCase.CaseId]));
        client.DefaultRequestHeaders.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={session.Id}");

        using var response = await client.GetAsync("/api/v1/cases", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(3, root.EnumerateObject().Count());
        Assert.Equal(1, root.GetProperty("contractVersion").GetInt32());
        Assert.Equal(snapshot.PackVersion, root.GetProperty("packVersion").GetString());
        var cases = root.GetProperty("cases");
        Assert.Equal(1, cases.GetArrayLength());
        var resultCase = cases[0];
        Assert.Equal(new[]
        {
            "activityId", "aircraftCategory", "capabilityLabel", "caseId", "caseRevision",
            "currentRunId", "name", "nextAction", "owner", "status", "synthetic",
        }, resultCase.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(visibleCase.CaseId, resultCase.GetProperty("caseId").GetString());
        Assert.True(resultCase.GetProperty("synthetic").GetBoolean());
        Assert.Equal(0, resultCase.GetProperty("caseRevision").GetInt64());
        Assert.Equal(6, resultCase.GetProperty("status").EnumerateObject().Count());
    }

    [Fact]
    public async Task AC029_SceneReturnsThePublishedSceneForAnInScopeCase()
    {
        var time = new AdjustableTime();
        using var factory = ReadFactory(new InMemoryCasePackRequestWindowStore(), time);
        using var client = factory.CreateClient();
        var snapshot = factory.Services.GetRequiredService<IAircraftCasePackSnapshotStore>().Read();
        Assert.NotNull(snapshot);
        var sceneCase = snapshot.Cases.First(scene =>
            scene.Components.Count > 0 && scene.Documents.Count > 0);
        var session = factory.Services.GetRequiredService<DemoSessionStore>()
            .Create(new DemoUser("user-a", "Reader", "presenter", [sceneCase.CaseId]));
        client.DefaultRequestHeaders.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={session.Id}");

        using var response = await client.GetAsync(
            $"/api/v1/cases/{sceneCase.CaseId}/scene", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(3, root.EnumerateObject().Count());
        Assert.Equal(1, root.GetProperty("contractVersion").GetInt32());
        Assert.Equal(snapshot.PackVersion, root.GetProperty("packVersion").GetString());
        var resultCase = root.GetProperty("case");
        Assert.Equal(6, resultCase.EnumerateObject().Count());
        Assert.Equal(sceneCase.CaseId, resultCase.GetProperty("caseId").GetString());
        Assert.Equal(sceneCase.Synthetic, resultCase.GetProperty("synthetic").GetBoolean());
        Assert.Equal(sceneCase.AircraftCount, resultCase.GetProperty("aircraftCount").GetInt32());
        Assert.Equal(sceneCase.EngineCount, resultCase.GetProperty("engineCount").GetInt32());

        var components = resultCase.GetProperty("components");
        Assert.Equal(sceneCase.Components.Count, components.GetArrayLength());
        for (var index = 0; index < sceneCase.Components.Count; index++)
        {
            var expected = sceneCase.Components[index];
            var actual = components[index];
            Assert.Equal(2, actual.EnumerateObject().Count());
            Assert.Equal(expected.ComponentId, actual.GetProperty("componentId").GetString());
            Assert.Equal(expected.EvidenceDocumentPaths,
                actual.GetProperty("evidenceDocumentPaths").EnumerateArray()
                    .Select(path => path.GetString()).ToArray());
        }

        var documents = resultCase.GetProperty("documents");
        Assert.Equal(sceneCase.Documents.Count, documents.GetArrayLength());
        for (var index = 0; index < sceneCase.Documents.Count; index++)
        {
            var expected = sceneCase.Documents[index];
            var actual = documents[index];
            Assert.Equal(2, actual.EnumerateObject().Count());
            Assert.Equal(expected.Path, actual.GetProperty("path").GetString());
            Assert.Equal(expected.Synthetic, actual.GetProperty("synthetic").GetBoolean());
        }
    }

    [Fact]
    public async Task AC029_SceneDoesNotReturnMissingOrOutOfScopeCases()
    {
        var time = new AdjustableTime();
        using var factory = ReadFactory(new InMemoryCasePackRequestWindowStore(), time);
        using var client = factory.CreateClient();
        var snapshot = factory.Services.GetRequiredService<IAircraftCasePackSnapshotStore>().Read();
        Assert.NotNull(snapshot);
        var existingCaseId = snapshot.Cases[0].CaseId;
        var session = factory.Services.GetRequiredService<DemoSessionStore>()
            .Create(new DemoUser("user-a", "Reader", "presenter", []));
        client.DefaultRequestHeaders.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={session.Id}");

        foreach (var caseId in new[] { existingCaseId, "not-published" })
        {
            using var response = await client.GetAsync(
                $"/api/v1/cases/{caseId}/scene", TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(string.Empty, body);
        }
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

    [Fact]
    public async Task SqliteMigrationCanDropTheRequestWindowTable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlite(connection).Options;
        await using var db = new CaseStateDbContext(options);
        await db.Database.MigrateAsync();

        await db.GetService<IMigrator>().MigrateAsync("20261019120000_AddCaseHistory");

        var tableCount = await db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'case_pack_request_windows'")
            .SingleAsync();
        Assert.Equal(0, tableCount);
    }
}
