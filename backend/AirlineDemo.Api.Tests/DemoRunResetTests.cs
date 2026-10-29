using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Diagnostics;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Runs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AirlineDemo.Api.Tests;

public sealed class DemoRunResetTests : IDisposable
{
    private const string Owner = "presenter";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "demo-run-reset-" + Guid.NewGuid().ToString("N"));

    public DemoRunResetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string PathFor() => Path.Combine(_dir, "runs.json");

    private static void Seed(CaseStateStore store, string runId, string owner, string receipt)
    {
        var scope = store.CreateCase(runId, owner);
        store.Commit(scope, 0, new CaseCommit(null, receipt, owner, "open", "opened " + runId));
    }

    private static void Append(CaseStateStore store, string runId, string receipt, string actor, string action)
    {
        var scope = store.AuthorizeCase(runId, Owner);
        store.Commit(scope, store.Read(scope).Revision, new CaseCommit(null, receipt, actor, action, action + " " + runId));
    }

    [Theory]
    [InlineData("success", 200, "success")]
    [InlineData("stale", 409, "conflict")]
    [InlineData("outside-scope", 404, "denied")]
    [InlineData("anonymous", 401, "unauthorized")]
    [InlineData("wrong-role", 403, "denied")]
    [InlineData("csrf", 400, "anti_forgery_failed")]
    [InlineData("invalid-body", 400, "validation_failed")]
    public async Task Demo_reset_and_role_switch_events(string scenario, int status, string outcome)
    {
        var logs = new ResetEventLogger();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Passcode"] = "reset-test-passcode",
                ["CaseState:Path"] = PathFor(),
            }));
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(logs));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var store = factory.Services.GetRequiredService<ICaseStateStore>();
        var scope = store.CreateCase("cp-case-c", Owner);
        for (var revision = 0; revision < 7; revision++)
            store.Commit(scope, revision, new CaseCommit(null, null, Owner, "seed", "seed"));
        var before = JsonSerializer.Serialize(store.Read(scope));
        var role = scenario == "wrong-role" ? "technical-reviewer" : "presenter";
        var session = factory.Services.GetRequiredService<DemoSessionStore>().Create(
            new DemoUser("reset-test-presenter", "Presenter", role, scenario == "outside-scope" ? ["cp-case-a"] : ["cp-case-c"]));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases/cp-case-c/reset-blocked-variant")
        {
            Content = scenario == "invalid-body"
                ? JsonContent.Create(new { itemBody = "CANARY-ITEM-655", noticeDraft = "CANARY-DRAFT-655" })
                : JsonContent.Create(new { contractVersion = 1, commandId = "reset-event-1", expectedRevision = scenario == "stale" ? 6 : 7, variant = "blocked" }),
        };
        if (scenario != "anonymous")
        {
            request.Headers.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={session.Id}");
            request.Headers.Add("X-CSRF-Token", scenario == "csrf" ? "CANARY-CSRF-655" : session.CsrfToken);
        }
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(status, (int)response.StatusCode);
        if (scenario == "outside-scope")
            Assert.Equal("{\"contractVersion\":1,\"error\":{\"code\":\"not_found\",\"message\":\"Not found.\"}}", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        if (scenario == "stale")
            Assert.Equal("stale_revision", (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("error").GetProperty("code").GetString());
        if (scenario == "csrf")
            Assert.Equal("anti_forgery_failed", (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("error").GetProperty("code").GetString());
        if (scenario == "success")
        {
            Assert.Equal(8, store.Read(scope).Revision);
            Assert.Single(store.Read(scope).BlockedVariantResetReceipts!);
        }
        else Assert.Equal(before, JsonSerializer.Serialize(store.Read(scope)));

        var properties = Assert.Single(logs.Events.Where(p => p.GetValueOrDefault("event.name") == "security.case_reset"));
        Assert.Equal(TelemetryRedaction.SecurityEvents.CaseReset, properties["event.name"]);
        Assert.Equal("case-reset", properties["action"]);
        Assert.Equal(outcome, properties["event.outcome"]);
        Assert.Equal(scenario == "anonymous" ? "unknown" : role, properties["fixture.role"]);
        Assert.Matches("^trace-[0-9a-f]{16}$", properties["trace.id"]!);
        Assert.Matches("^actor-[0-9a-f]{16}$", properties["enduser.pseudo.id"]!);
        foreach (var canary in new[] { session.Id, session.CsrfToken, "reset-test-passcode", "CANARY-CSRF-655", "CANARY-ITEM-655", "CANARY-DRAFT-655" })
            Assert.DoesNotContain(canary, string.Join("|", properties.Select(p => $"{p.Key}={p.Value}")));
    }

    [Theory]
    [InlineData("owner-denied", 404, "denied")]
    [InlineData("owner-failed", 500, "failed")]
    [InlineData("owner-missing", 404, "denied")]
    [InlineData("authorize-denied", 404, "denied")]
    [InlineData("authorize-failed", 500, "failed")]
    public async Task Demo_reset_store_lookup_failures_emit_one_event(string scenario, int status, string outcome)
    {
        var logs = new ResetEventLogger();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Passcode"] = "reset-test-passcode",
                ["CaseState:Path"] = PathFor(),
            }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ILoggerProvider>(logs);
                var seeded = new CaseStateStore(PathFor());
                seeded.CreateCase("cp-case-lookup", Owner);
                var proxy = DispatchProxy.Create<ICaseStateStore, FaultingStore>();
                ((FaultingStore)(object)proxy).Configure(seeded, scenario);
                services.AddSingleton(proxy);
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var session = factory.Services.GetRequiredService<DemoSessionStore>().Create(
            new DemoUser("reset-test-presenter", "Presenter", "presenter", ["cp-case-lookup"]));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases/cp-case-lookup/reset-blocked-variant")
        {
            Content = JsonContent.Create(new { contractVersion = 1, commandId = "reset-lookup-1", expectedRevision = 1, variant = "blocked" }),
        };
        request.Headers.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={session.Id}");
        request.Headers.Add("X-CSRF-Token", session.CsrfToken);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(status, (int)response.StatusCode);
        var properties = Assert.Single(logs.Events.Where(p => p.GetValueOrDefault("event.name") == "security.case_reset"));
        Assert.Equal("case-reset", properties["action"]);
        Assert.Equal(outcome, properties["event.outcome"]);
        Assert.Equal("presenter", properties["fixture.role"]);
    }

    public class FaultingStore : DispatchProxy
    {
        private ICaseStateStore _inner = null!;
        private string _scenario = "";

        public void Configure(ICaseStateStore inner, string scenario) { _inner = inner; _scenario = scenario; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (_scenario, targetMethod!.Name)
            {
                case ("owner-denied", nameof(ICaseStateStore.OwnerOf)):
                case ("authorize-denied", nameof(ICaseStateStore.AuthorizeCase)):
                    throw new CaseAccessDeniedException();
                case ("owner-failed", nameof(ICaseStateStore.OwnerOf)):
                case ("authorize-failed", nameof(ICaseStateStore.AuthorizeCase)):
                    throw new InvalidOperationException("store unavailable");
                case ("owner-missing", nameof(ICaseStateStore.OwnerOf)):
                    return null;
            }
            try { return targetMethod.Invoke(_inner, args); }
            catch (TargetInvocationException exception) { ExceptionDispatchInfo.Capture(exception.InnerException!).Throw(); throw; }
        }
    }

    private sealed class ResetEventLogger : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<Dictionary<string, string?>> Events { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
                Events.Enqueue(properties.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value?.ToString()));
        }
    }

    [Fact]
    public void Reset_leaves_other_run_and_selected_run_history_unchanged()
    {
        var store = new CaseStateStore(PathFor());
        Seed(store, "run-a", Owner, "receipt-a");
        Seed(store, "run-b", Owner, "receipt-b");
        Append(store, "run-b", "finding-b", "reviewer", "finding");
        Append(store, "run-b", "request-b", Owner, "request");
        Append(store, "run-b", "decision-b", "reviewer", "decision");
        var runBBefore = store.Read(store.AuthorizeCase("run-b", Owner));
        var runABefore = store.Read(store.AuthorizeCase("run-a", Owner));

        var result = new DemoRunReset(store).Reset("run-a", Owner, "run-c", "aircraft-return-fixture-v1", "1.4.2");

        Assert.Equal("run-c", result.NewRunId);
        Assert.Equal("1.4.2", result.Fallback.ApplicationVersion);
        var runBAfter = store.Read(store.AuthorizeCase("run-b", Owner));
        Assert.Equal(runBBefore.Revision, runBAfter.Revision);
        Assert.Equal(runBBefore.ReceiptIds, runBAfter.ReceiptIds);
        Assert.Contains("finding-b", runBAfter.ReceiptIds);
        Assert.Contains("request-b", runBAfter.ReceiptIds);
        Assert.Contains("decision-b", runBAfter.ReceiptIds);
        Assert.Equal(runBBefore.Audit, runBAfter.Audit);
        Assert.Equal(runBBefore.ReceiptFingerprints, runBAfter.ReceiptFingerprints);

        var runAAfter = store.Read(store.AuthorizeCase("run-a", Owner));
        Assert.Equal(runABefore.Revision, runAAfter.Revision);
        Assert.Equal(runABefore.ReceiptIds, runAAfter.ReceiptIds);
        Assert.Equal(runABefore.Audit, runAAfter.Audit);
    }

    [Fact]
    public void Reset_starts_new_run_without_inherited_receipts_or_audit()
    {
        var store = new CaseStateStore(PathFor());
        Seed(store, "run-a", Owner, "receipt-a");

        var result = new DemoRunReset(store).Reset("run-a", Owner, "run-c", "aircraft-return-fixture-v1", "1.4.2");

        Assert.Empty(result.NewRun.ReceiptIds);
        Assert.Empty(result.NewRun.Audit);
        Assert.Equal(0, result.NewRun.Revision);
    }

    [Fact]
    public void Reset_by_non_owner_is_denied_without_mutating_store()
    {
        var store = new CaseStateStore(PathFor());
        Seed(store, "run-a", Owner, "receipt-a");
        var before = File.ReadAllBytes(PathFor());

        Assert.Throws<CaseAccessDeniedException>(() => new DemoRunReset(store).Reset("run-a", "reviewer", "run-c", "aircraft-return-fixture-v1", "1.4.2"));

        Assert.Equal(before, File.ReadAllBytes(PathFor()));
        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("run-c", Owner));
    }

    [Fact]
    public void Reset_of_unknown_run_is_denied_without_mutating_store()
    {
        var store = new CaseStateStore(PathFor());
        Seed(store, "run-a", Owner, "receipt-a");
        var before = File.ReadAllBytes(PathFor());

        Assert.Throws<CaseAccessDeniedException>(() => new DemoRunReset(store).Reset("missing", Owner, "run-c", "aircraft-return-fixture-v1", "1.4.2"));

        Assert.Equal(before, File.ReadAllBytes(PathFor()));
    }

    [Fact]
    public void Reset_onto_existing_run_id_is_rejected_without_mutating_store()
    {
        var store = new CaseStateStore(PathFor());
        Seed(store, "run-a", Owner, "receipt-a");
        Seed(store, "run-b", Owner, "receipt-b");
        var before = File.ReadAllBytes(PathFor());

        Assert.Throws<InvalidOperationException>(() => new DemoRunReset(store).Reset("run-a", Owner, "run-b", "aircraft-return-fixture-v1", "1.4.2"));
        Assert.Throws<DemoRunResetException>(() => new DemoRunReset(store).Reset("run-a", Owner, "run-a", "aircraft-return-fixture-v1", "1.4.2"));

        Assert.Equal(before, File.ReadAllBytes(PathFor()));
    }

    [Fact]
    public void Reset_does_not_ingest_evaluator_answers_into_any_run()
    {
        var store = new CaseStateStore(PathFor());
        Seed(store, "run-a", Owner, "receipt-a");

        var result = new DemoRunReset(store).Reset("run-a", Owner, "run-c", "aircraft-return-fixture-v1", "1.4.2");

        Assert.DoesNotContain(result.NewRun.Audit, e => e.Actor.Contains("evaluator", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(result.NewRun.ReceiptIds);
        Assert.Single(store.Read(store.AuthorizeCase("run-a", Owner)).ReceiptIds);
    }

    [Fact]
    public void Fallback_identifies_fixture_and_application_version()
    {
        var fallback = DemoRunReset.ResolveFallback("aircraft-return-fixture-v1", "1.4.2");

        Assert.Equal("aircraft-return-fixture-v1", fallback.FixtureId);
        Assert.Equal("1.4.2", fallback.ApplicationVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Fallback_is_blocked_when_application_version_is_missing(string? version)
    {
        Assert.Throws<DemoRunResetException>(() => DemoRunReset.ResolveFallback("aircraft-return-fixture-v1", version));
    }

    [Fact]
    public void Fallback_is_blocked_when_fixture_id_is_missing()
    {
        Assert.Throws<DemoRunResetException>(() => DemoRunReset.ResolveFallback(null, "1.4.2"));
    }
}

public sealed class DemoRunResetEndpointTests : IDisposable
{
    private const string Passcode = "test-passcode";
    private const string Fixture = "aircraft-return-fixture-v1";

    private sealed record Session(string Cookie, string Csrf);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "demo-run-reset-http-" + Guid.NewGuid().ToString("N"));

    private string PathFor => Path.Combine(_dir, "runs.json");

    public DemoRunResetEndpointTests()
    {
        Directory.CreateDirectory(_dir);
        var store = new CaseStateStore(PathFor);
        foreach (var caseId in new[] { "case-001", "case-002" })
        {
            var scope = store.CreateCase(caseId, "presenter");
            store.Commit(scope, 0, new CaseCommit(null, "receipt-" + caseId, "presenter", "open", "opened " + caseId));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Demo:Passcode"] = Passcode,
                    ["CaseState:Path"] = PathFor,
                })));

    private static async Task<Session> SignInAsync(HttpClient client, string userId)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId, passcode = Passcode }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return new Session(cookie, body.GetProperty("csrfToken").GetString()!);
    }

    private Task<HttpResponseMessage> ResetAsync(HttpClient client, string runId, Session? session, bool withCsrf, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/runs/{runId}/reset") { Content = JsonContent.Create(body) };
        if (session is not null) request.Headers.Add("Cookie", session.Cookie);
        if (session is not null && withCsrf) request.Headers.Add("X-CSRF-Token", session.Csrf);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Authorized_reset_creates_new_run_and_returns_fixture_and_version()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var session = await SignInAsync(client, "presenter");

        using var response = await ResetAsync(client, "case-001", session, true, new { newRunId = "run-new", fixtureId = Fixture, applicationVersion = "1.4.2" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("run-new", body.GetProperty("newRunId").GetString());
        Assert.Equal(Fixture, body.GetProperty("fallback").GetProperty("fixtureId").GetString());
        Assert.Equal("1.4.2", body.GetProperty("fallback").GetProperty("applicationVersion").GetString());
        Assert.Equal("receipt-case-001", new CaseStateStore(PathFor).Read(new CaseStateStore(PathFor).AuthorizeCase("case-001", "presenter")).ReceiptIds.Single());
    }

    [Fact]
    public async Task Reset_makes_new_run_available_to_the_same_session_only()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var session = await SignInAsync(client, "presenter");
        using var reset = await ResetAsync(client, "case-001", session, true, new { newRunId = "run-new", fixtureId = Fixture, applicationVersion = "1.4.2" });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var other = await SignInAsync(client, "reviewer");
        using var denied = await ResetAsync(client, "run-new", other, true, new { newRunId = "run-other", fixtureId = Fixture, applicationVersion = "1.4.2" });
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        using var next = await ResetAsync(client, "run-new", session, true, new { newRunId = "run-newer", fixtureId = Fixture, applicationVersion = "1.4.2" });
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task Reset_without_application_version_is_rejected_and_creates_no_run()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var session = await SignInAsync(client, "presenter");

        using var response = await ResetAsync(client, "case-001", session, true, new { newRunId = "run-new", fixtureId = Fixture, applicationVersion = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Throws<CaseAccessDeniedException>(() => new CaseStateStore(PathFor).AuthorizeCase("run-new", "presenter"));
    }

    [Fact]
    public async Task Reset_onto_existing_run_id_returns_conflict_without_mutating_store()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var session = await SignInAsync(client, "presenter");
        var before = File.ReadAllBytes(PathFor);

        using var response = await ResetAsync(client, "case-001", session, true, new { newRunId = "case-002", fixtureId = Fixture, applicationVersion = "1.4.2" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, File.ReadAllBytes(PathFor));
    }

    [Fact]
    public async Task Reset_of_another_users_run_is_not_found_and_mutates_nothing()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var reviewer = await SignInAsync(client, "reviewer");
        var before = File.ReadAllBytes(PathFor);

        using var response = await ResetAsync(client, "case-002", reviewer, true, new { newRunId = "run-new", fixtureId = Fixture, applicationVersion = "1.4.2" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, File.ReadAllBytes(PathFor));
    }

    [Fact]
    public async Task Reset_without_session_is_rejected_and_mutates_nothing()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var before = File.ReadAllBytes(PathFor);

        using var response = await ResetAsync(client, "case-001", null, false, new { newRunId = "run-new", fixtureId = Fixture, applicationVersion = "1.4.2" });

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, File.ReadAllBytes(PathFor));
    }

    [Fact]
    public async Task Reset_without_csrf_token_is_rejected_and_mutates_nothing()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var session = await SignInAsync(client, "presenter");
        var before = File.ReadAllBytes(PathFor);

        using var response = await ResetAsync(client, "case-001", session, false, new { newRunId = "run-new", fixtureId = Fixture, applicationVersion = "1.4.2" });

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, File.ReadAllBytes(PathFor));
    }
}
