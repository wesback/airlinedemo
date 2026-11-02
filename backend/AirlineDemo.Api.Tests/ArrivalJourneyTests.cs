using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.SourceSystems;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class ArrivalJourneyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arrival-journey-" + Guid.NewGuid().ToString("N"));
    private string CasePath => Path.Combine(_directory, "cases.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task FreshOptInCaseStartsWithoutSourcePins()
    {
        using var factory = CreateFactory(("Demo:ArrivalJourney", "true"), ("Demo:SeedJourney", "true"));
        using var client = factory.CreateClient();
        var state = factory.Services.GetRequiredService<ICaseStateStore>()
            .Read(factory.Services.GetRequiredService<ICaseStateStore>().AuthorizeCase("case-001", "presenter"));

        Assert.Equal(1, state.Revision);
        Assert.Empty(state.Basis);
        Assert.Equal("arrival.enrolled", Assert.Single(state.Audit).Action);

        var presenter = CreateSessionClient(factory, new DemoUser("presenter", "Presenter", "presenter", ["case-001"]));
        using var statusResponse = await presenter.GetAsync("/api/v1/cases/case-001/arrival", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "contractVersion", "caseId", "caseRevision", "state", "missingSources", "canComplete" },
            status.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("not_started", status.RootElement.GetProperty("state").GetString());
        Assert.Equal(1, status.RootElement.GetProperty("caseRevision").GetInt64());

        using var caseResponse = await presenter.GetAsync("/api/cases/case-001", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, caseResponse.StatusCode);
        using var caseBody = JsonDocument.Parse(await caseResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("not_started", caseBody.RootElement.GetProperty("arrival").GetProperty("state").GetString());
        Assert.Empty(caseBody.RootElement.GetProperty("basis").EnumerateArray());
    }

    [Fact]
    public async Task ExistingCasesAndDefaultSeedingRemainUnchanged()
    {
        Directory.CreateDirectory(_directory);
        var seeded = new CaseStateStore(CasePath);
        var scope = seeded.CreateCase("case-001", "presenter");
        seeded.Commit(scope, 0, new CaseCommit(CaseStateStore.CreateBasisVersion(1, "existing work", ""),
            "existing-receipt", "presenter", "existing-action", "Existing state must not be enrolled."));
        var before = JsonSerializer.Serialize(seeded.Read(scope));

        using var factory = CreateFactory(("Demo:ArrivalJourney", "true"));
        var store = factory.Services.GetRequiredService<ICaseStateStore>();
        var after = store.Read(store.AuthorizeCase("case-001", "presenter"));
        Assert.Equal(before, JsonSerializer.Serialize(after));
        using var legacyClient = CreateSessionClient(factory, new DemoUser("legacy-presenter", "Presenter", "presenter", ["case-001"]));
        using var legacyResponse = await legacyClient.GetAsync("/api/cases/case-001", TestContext.Current.CancellationToken);
        using var legacyBody = JsonDocument.Parse(await legacyResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(legacyBody.RootElement.TryGetProperty("arrival", out _));

        var defaultDirectory = Path.Combine(_directory, "default");
        using var defaultFactory = CreateFactory(("CaseState:Path", Path.Combine(defaultDirectory, "cases.json")));
        Assert.Null(defaultFactory.Services.GetRequiredService<ICaseStateStore>().OwnerOf("case-001"));
    }

    [Fact]
    public async Task StartEnforcesSessionRoleScopeAndCsrf()
    {
        using var factory = CreateFactory(("Demo:ArrivalJourney", "true"), ("Demo:SeedCases", "true"));
        using var presenter = CreateSessionClient(factory, new DemoUser("presenter", "Presenter", "presenter", ["case-001"]));
        using var noCsrf = CreateSessionClient(factory, new DemoUser("presenter-no-csrf", "Presenter", "presenter", ["case-001"]), includeCsrf: false);
        using var missingCsrf = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases/case-001/arrival/start")
        {
            Content = JsonContent.Create(new { commandId = "start-no-csrf", expectedRevision = 1 }),
        };
        using var csrfResponse = await noCsrf.SendAsync(missingCsrf, TestContext.Current.CancellationToken);
        await AssertErrorAsync(csrfResponse, HttpStatusCode.Forbidden, "anti_forgery_failed");

        using var reviewer = CreateSessionClient(factory,
            new DemoUser("reviewer", "Reviewer", "technical-reviewer", ["case-001"]));
        using var reviewerRequest = StartRequest("case-001", "reviewer-start", 1);
        using var reviewerResponse = await reviewer.SendAsync(reviewerRequest, TestContext.Current.CancellationToken);
        await AssertErrorAsync(reviewerResponse, HttpStatusCode.Forbidden, "forbidden");

        using var executive = CreateSessionClient(factory, new DemoUser("executive", "Executive", "executive", []));
        using var executiveRequest = StartRequest("case-001", "executive-start", 1);
        using var executiveResponse = await executive.SendAsync(executiveRequest, TestContext.Current.CancellationToken);
        await AssertErrorAsync(executiveResponse, HttpStatusCode.Forbidden, "forbidden");

        using var unknown = CreateSessionClient(factory, new DemoUser("unknown", "Unknown", "observer", ["case-001"]));
        using var unknownStatus = await unknown.GetAsync("/api/v1/cases/case-001/arrival", TestContext.Current.CancellationToken);
        await AssertErrorAsync(unknownStatus, HttpStatusCode.Forbidden, "forbidden");

        using var outOfScope = StartRequest("missing-case", "out-of-scope", 1);
        using var outOfScopeResponse = await presenter.SendAsync(outOfScope, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, outOfScopeResponse.StatusCode);
        Assert.Equal(0, (await outOfScopeResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length);

        using var notEnrolled = StartRequest("case-002", "not-enrolled", 0);
        using var notEnrolledResponse = await presenter.SendAsync(notEnrolled, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, notEnrolledResponse.StatusCode);
        Assert.Equal(0, (await notEnrolledResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length);
    }

    [Theory]
    [InlineData("{}", "/api/v1/cases/case-001/arrival/start")]
    [InlineData("{\"commandId\":\"\",\"expectedRevision\":1}", "/api/v1/cases/case-001/arrival/start")]
    [InlineData("{\"commandId\":\"start\",\"expectedRevision\":-1}", "/api/v1/cases/case-001/arrival/start")]
    [InlineData("{\"commandId\":\"start\"}", "/api/v1/cases/case-001/arrival/start")]
    [InlineData("{\"commandId\":\"start\",\"expectedRevision\":1,\"role\":\"presenter\"}", "/api/v1/cases/case-001/arrival/start")]
    [InlineData("{\"commandId\":\"12345678901234567890123456789012345678901234567890123456789012345\",\"expectedRevision\":1}", "/api/v1/cases/case-001/arrival/start")]
    [InlineData("{\"commandId\":\"start\",\"expectedRevision\":1}", "/api/v1/cases/case-001/arrival/start?role=presenter")]
    public async Task ArrivalContractCases(string body, string path)
    {
        using var factory = CreateFactory(("Demo:ArrivalJourney", "true"));
        using var client = CreateSessionClient(factory, new DemoUser("presenter", "Presenter", "presenter", ["case-001"]));
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");

        var store = factory.Services.GetRequiredService<ICaseStateStore>();
        var state = store.Read(store.AuthorizeCase("case-001", "presenter"));
        Assert.Equal(1, state.Revision);
        Assert.Single(state.Audit);
    }

    [Fact]
    public async Task StatusIsAvailableToEveryKnownInScopeRoleAndRejectsUnknownRoles()
    {
        using var factory = CreateFactory(("Demo:ArrivalJourney", "true"));
        foreach (var role in new[] { "presenter", "technical-reviewer", "finance-reviewer", "legal-reviewer" })
        {
            using var client = CreateSessionClient(factory, new DemoUser(role, role, role, ["case-001"]));
            using var response = await client.GetAsync("/api/v1/cases/case-001/arrival", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var unknown = CreateSessionClient(factory, new DemoUser("unknown", "Unknown", "observer", []));
        using var outOfScope = await unknown.GetAsync("/api/v1/cases/case-001/arrival", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);
        Assert.Equal(0, (await outOfScope.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length);

        using var noSession = factory.CreateClient();
        using var unauthenticated = await noSession.GetAsync("/api/v1/cases/case-001/arrival", TestContext.Current.CancellationToken);
        await AssertErrorAsync(unauthenticated, HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task StartReplayReturnsOriginalReceiptWithoutWrites()
    {
        using var factory = CreateFactory(("Demo:ArrivalJourney", "true"));
        using var client = CreateSessionClient(factory, new DemoUser("presenter", "Presenter", "presenter", ["case-001"]));
        using var firstResponse = await client.SendAsync(StartRequest("case-001", "start-1", 1),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var original = await firstResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var originalJson = JsonDocument.Parse(original);
        Assert.Equal(new[]
        {
            "contractVersion", "commandId", "caseId", "state", "priorRevision", "newRevision", "auditEventId",
        }, originalJson.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("collecting", originalJson.RootElement.GetProperty("state").GetString());
        Assert.Equal(1, originalJson.RootElement.GetProperty("priorRevision").GetInt64());
        Assert.Equal(2, originalJson.RootElement.GetProperty("newRevision").GetInt64());

        using var replayResponse = await client.SendAsync(StartRequest("case-001", "start-1", 1),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.Equal(original, await replayResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var store = factory.Services.GetRequiredService<ICaseStateStore>();
        var state = store.Read(store.AuthorizeCase("case-001", "presenter"));
        Assert.Equal(2, state.Revision);
        Assert.Equal(2, state.Audit.Count);
        Assert.Equal("arrival.started", state.Audit[^1].Action);

        using var changedReplay = await client.SendAsync(StartRequest("case-001", "start-1", 2),
            TestContext.Current.CancellationToken);
        await AssertErrorAsync(changedReplay, HttpStatusCode.Conflict, "command_id_conflict");
        Assert.Equal(2, store.Read(store.AuthorizeCase("case-001", "presenter")).Revision);
    }

    [Fact]
    public async Task StartRejectsStaleRevisionWithoutWriting()
    {
        using var factory = CreateFactory(("Demo:ArrivalJourney", "true"));
        var store = factory.Services.GetRequiredService<ICaseStateStore>();
        var scope = store.AuthorizeCase("case-001", "presenter");
        store.Commit(scope, 1, new CaseCommit(null, null, "presenter", "intervening-action", "Advance case revision."));

        using var client = CreateSessionClient(factory, new DemoUser("presenter", "Presenter", "presenter", ["case-001"]));
        using var response = await client.SendAsync(StartRequest("case-001", "stale-start", 1), TestContext.Current.CancellationToken);
        await AssertErrorAsync(response, HttpStatusCode.Conflict, "stale_revision");

        var state = store.Read(scope);
        Assert.Equal(2, state.Revision);
        Assert.DoesNotContain(state.Audit, item => item.Action == "arrival.started");
    }

    [Fact]
    public void StartRaceRejectsInterveningRevisionWithoutWriting()
    {
        Directory.CreateDirectory(_directory);
        var inner = new CaseStateStore(CasePath);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Demo:ArrivalJourney"] = "true" }).Build();
        DemoCaseSeeder.Seed(configuration, inner);
        ArrivalJourneyService.EnrollOptInCase(configuration, inner);
        var racing = new InterveningCommitStore(inner);
        var service = new ArrivalJourneyService(racing, TimeProvider.System, new SourceModeStore(racing, TimeProvider.System));

        var result = service.Start(inner.AuthorizeCase("case-001", "presenter"), "presenter",
            new ArrivalCommand("race-start", 1));

        Assert.Equal(ArrivalStartOutcome.StaleRevision, result.Outcome);
        var state = inner.Read(inner.AuthorizeCase("case-001", "presenter"));
        Assert.Equal(2, state.Revision);
        Assert.Collection(state.Audit,
            item => Assert.Equal("arrival.enrolled", item.Action),
            item => Assert.Equal("intervening-action", item.Action));
    }

    private WebApplicationFactory<Program> CreateFactory(params (string Key, string Value)[] settings)
    {
        Directory.CreateDirectory(_directory);
        var values = settings.ToDictionary(pair => pair.Key, pair => (string?)pair.Value);
        values["Persistence:CaseStateProvider"] = "File";
        values["CaseState:Path"] = values.GetValueOrDefault("CaseState:Path") ?? CasePath;
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values)));
    }

    private static HttpClient CreateSessionClient(WebApplicationFactory<Program> factory, DemoUser user, bool includeCsrf = true)
    {
        var session = factory.Services.GetRequiredService<DemoSessionStore>().Create(user);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={session.Id}");
        if (includeCsrf) client.DefaultRequestHeaders.Add(DemoAuthEndpoints.CsrfHeaderName, session.CsrfToken);
        return client;
    }

    private static HttpRequestMessage StartRequest(string caseId, string commandId, long revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/cases/{caseId}/arrival/start")
        {
            Content = JsonContent.Create(new { commandId, expectedRevision = revision }),
        };
        return request;
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "contractVersion", "error" }, json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(1, json.RootElement.GetProperty("contractVersion").GetInt32());
        Assert.Equal(code, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(new[] { "code" }, json.RootElement.GetProperty("error").EnumerateObject().Select(property => property.Name).ToArray());
    }

    private sealed class InterveningCommitStore(ICaseStateStore inner) : ICaseStateStore
    {
        private bool _intervened;

        public CaseScope AuthorizeCase(string caseId, string owner) => inner.AuthorizeCase(caseId, owner);
        public string? OwnerOf(string caseId) => inner.OwnerOf(caseId);
        public CaseScope CreateCase(string caseId, string owner) => inner.CreateCase(caseId, owner);
        public CaseState Read(CaseScope scope) => inner.Read(scope);
        public BasisVersion GetBasisVersion(CaseScope scope, int version) => inner.GetBasisVersion(scope, version);
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => inner.ReadAudit(scope);
        public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit)
        {
            if (!_intervened && commit.Action == ArrivalJourneyService.StartAction)
            {
                _intervened = true;
                inner.Commit(scope, expectedRevision, new CaseCommit(null, null, "presenter",
                    "intervening-action", "A competing commit wins."));
            }
            return inner.Commit(scope, expectedRevision, commit);
        }
        public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope) => inner.ListWorkItems(scope);
        public CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor) =>
            inner.CreateWorkItem(scope, expectedRevision, draft, actor);
        public CommitResult ChangeWorkItemState(CaseScope scope, long expectedRevision, string itemId, string state, string actor) =>
            inner.ChangeWorkItemState(scope, expectedRevision, itemId, state, actor);
        public void AppendTrace(CaseScope scope, InvestigationTrace trace) => inner.AppendTrace(scope, trace);
        public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId) =>
            inner.FindTrace(scope, caseId, runId, investigationId);
    }
}
