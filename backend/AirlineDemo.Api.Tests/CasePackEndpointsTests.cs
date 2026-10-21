using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AirlineDemo.Api.Admission;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Finance;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Projection;
using AirlineDemo.Api.Requests;
using AirlineDemo.Api.Reviews;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace AirlineDemo.Api.Tests;

public sealed class CasePackEndpointsTests : IDisposable
{
    private const string Passcode = "case-pack-list-test-passcode";
    private static readonly string[] CaseIds = ["cp-case-a", "cp-case-b", "cp-case-c"];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"case-pack-list-{Guid.NewGuid():N}");

    private sealed record Session(string Cookie, DemoSession Value);
    private sealed class AdjustableTime : TimeProvider
    {
        public DateTimeOffset Current { get; set; }
        public override DateTimeOffset GetUtcNow() => Current;
    }
    private sealed record CaseStateSnapshot(
        string CaseId, long Revision, string[] RunIds, string[] JourneyRunIds, AuditEvent[] Audit);
    private sealed record ArrSelector(string CaseId, string RunId, string BasisId);
    private sealed class CountingArrHistorySource : IArrCaseHistorySource
    {
        public List<ArrSelector> Selectors { get; } = [];
        public string CaseId { get; set; } = "cp-case-a";
        public string RunId { get; set; } = "current-run";
        public string BasisId { get; set; } = "basis-v1";
        public IReadOnlyList<CaseJourneyEvent> Events { get; set; } = [];
        public int ReadCalls { get; private set; }
        public bool OlderHistoryAvailable { get; set; } = true;
        public ArrCaseHistorySelection? Read(string caseId, string runId, string basisId)
        {
            ReadCalls++;
            Selectors.Add(new ArrSelector(caseId, runId, basisId));
            if (!StringComparer.Ordinal.Equals(caseId, CaseId)
                || !StringComparer.Ordinal.Equals(runId, RunId)
                || !StringComparer.Ordinal.Equals(basisId, BasisId)) return null;
            return new ArrCaseHistorySelection(caseId, runId, basisId, Events, OlderHistoryAvailable);
        }
    }
    private sealed class CountingCaseStateStore(CaseStateStore inner) : ICaseStateStore
    {
        public CaseStateStore Inner { get; } = inner;
        public int ReadCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public int OwnerCalls { get; private set; }
        public int AuthorizeCalls { get; private set; }
        public int ResetCommitCalls { get; private set; }
        public bool ThrowOnRead { get; set; }

        public CaseScope AuthorizeCase(string caseId, string owner)
        { AuthorizeCalls++; return Inner.AuthorizeCase(caseId, owner); }
        public string? OwnerOf(string caseId)
        { OwnerCalls++; return Inner.OwnerOf(caseId); }
        public CaseScope CreateCase(string caseId, string owner) { WriteCalls++; return Inner.CreateCase(caseId, owner); }
        public CaseState Read(CaseScope scope)
        {
            ReadCalls++;
            if (ThrowOnRead) throw new InvalidDataException("private case-store detail");
            return Inner.Read(scope);
        }
        public BasisVersion GetBasisVersion(CaseScope scope, int version) => Inner.GetBasisVersion(scope, version);
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => Inner.ReadAudit(scope);
        public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit)
        {
            var result = Inner.Commit(scope, expectedRevision, commit);
            WriteCalls++;
            return result;
        }
        public void ResetReadCount() => ReadCalls = 0;
        public CasePackResetCommitResult CommitBlockedVariantReset(
            CaseScope scope, long expectedRevision, string actor, string commandId, string variant,
            DateTimeOffset? auditAt = null)
        {
            ResetCommitCalls++;
            var result = Inner.CommitBlockedVariantReset(scope, expectedRevision, actor, commandId, variant, auditAt);
            if (!result.Replayed) WriteCalls++;
            return result;
        }
        public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope) => Inner.ListWorkItems(scope);
        public CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor)
        { WriteCalls++; return Inner.CreateWorkItem(scope, expectedRevision, draft, actor); }
        public CommitResult ChangeWorkItemState(CaseScope scope, long expectedRevision, string itemId, string state, string actor)
        { WriteCalls++; return Inner.ChangeWorkItemState(scope, expectedRevision, itemId, state, actor); }
        public void AppendTrace(CaseScope scope, InvestigationTrace trace) { WriteCalls++; Inner.AppendTrace(scope, trace); }
        public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId) =>
            Inner.FindTrace(scope, caseId, runId, investigationId);
        public string CaptureState()
        {
            var cases = CaseIds.Select(caseId =>
            {
                var owner = Inner.OwnerOf(caseId)!;
                var state = Inner.Read(Inner.AuthorizeCase(caseId, owner));
                return new CaseStateSnapshot(
                    caseId,
                    state.Revision,
                    (state.Traces ?? []).Select(item => item.RunId).ToArray(),
                    (state.JourneyEvents ?? []).Select(item => item.RunId).ToArray(),
                    state.Audit.ToArray());
            });
            return JsonSerializer.Serialize(cases);
        }
        public void ResetCheckCounts()
        {
            ReadCalls = 0; WriteCalls = 0; OwnerCalls = 0; AuthorizeCalls = 0; ResetCommitCalls = 0;
        }
        public void ResetWriteCount() => WriteCalls = 0;
    }
    private WebApplicationFactory<Program> CreateHost(
        CountingCaseStateStore store,
        ICasePackRequestWindowStore? windowStore = null,
        TimeProvider? time = null,
        IArrCaseHistorySource? history = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Demo:Passcode"] = Passcode,
                    ["Persistence:EvidenceProvider"] = "Memory",
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICaseStateStore>();
                services.AddSingleton(store);
                services.AddSingleton<ICaseStateStore>(store);
                if (time is not null)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(time);
                }
                if (windowStore is not null)
                {
                    services.RemoveAll<ICasePackRequestWindowStore>();
                    services.AddSingleton(windowStore);
                }
                if (history is not null)
                {
                    services.RemoveAll<IArrCaseHistorySource>();
                    services.AddSingleton(history);
                }
            });
        });

    private CountingCaseStateStore CreateStore()
    {
        Directory.CreateDirectory(_directory);
        return new CountingCaseStateStore(new CaseStateStore(Path.Combine(_directory, "case-state.json")));
    }
    private static void SeedCases(CountingCaseStateStore store)
    {
        foreach (var caseId in CaseIds) store.Inner.CreateCase(caseId, "case-pack-test-owner");
        store.ResetWriteCount();
    }
    private static Session CreateSession(WebApplicationFactory<Program> host, string role, params string[] scopes)
    {
        var userId = "case-pack-reader-" + Guid.NewGuid().ToString("N");
        var value = host.Services.GetRequiredService<DemoSessionStore>()
            .Create(new DemoUser(userId, "Case Pack Reader", role, scopes));
        return new Session($"{DemoAuthEndpoints.CookieName}={value.Id}", value);
    }
    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, Session? session = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (session is not null) request.Headers.Add("Cookie", session.Cookie);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private static async Task<HttpResponseMessage> SendResetAsync(
        HttpClient client, string caseId, Session session, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/cases/{caseId}/reset-blocked-variant");
        request.Headers.Add("Cookie", session.Cookie);
        request.Headers.Add(DemoAuthEndpoints.CsrfHeaderName, session.Value.CsrfToken);
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string[] Keys(JsonElement value) =>
        value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task AC007_CaseListReturnsExactScopedContract()
    {
        var store = CreateStore();
        using var host = CreateHost(store);
        SeedCases(store);
        SeedCurrentArrProjection(store.Inner, "cp-case-a");
        using var client = host.CreateClient();
        var snapshot = host.Services.GetRequiredService<IAircraftCasePackSnapshotStore>().Read();
        Assert.NotNull(snapshot);

        using var allResponse = await SendAsync(client, HttpMethod.Get, "/api/v1/cases",
            CreateSession(host, "presenter", CaseIds));
        Assert.Equal(HttpStatusCode.OK, allResponse.StatusCode);
        using var allJson = await ReadJsonAsync(allResponse);
        AssertSummaryEnvelope(allJson.RootElement, snapshot.PackVersion, CaseIds);
        var allCases = allJson.RootElement.GetProperty("cases");
        AssertStatus(allCases, "cp-case-a", "assessment-ready", "open", "delivered", "approve", "signed-off", "accept");
        AssertStatus(allCases, "cp-case-b", null, null, null, null, null, null);
        AssertStatus(allCases, "cp-case-c", null, null, null, null, null, null);

        using var oneResponse = await SendAsync(client, HttpMethod.Get, "/api/v1/cases",
            CreateSession(host, "presenter", "cp-case-a"));
        Assert.Equal(HttpStatusCode.OK, oneResponse.StatusCode);
        using var oneJson = await ReadJsonAsync(oneResponse);
        AssertSummaryEnvelope(oneJson.RootElement, snapshot.PackVersion, ["cp-case-a"]);
        Assert.DoesNotContain("cp-case-b", oneJson.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("cp-case-c", oneJson.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    private static void SeedCurrentArrProjection(CaseStateStore store, string caseId)
    {
        var scope = store.AuthorizeCase(caseId, "case-pack-test-owner");
        var at = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
        var basis = CaseStateStore.CreateBasisVersion(1, "current basis", string.Empty);
        var receipt = new DeliveryReceipt(
            "request-event-a", "request-a", caseId, "report-a", "basis-v1", 1, "1", "synthetic request");
        var assessment = new CaseAssessment(
            "assessment-a", caseId, "basis-v1", 1, "assessment-ready", "current assessment",
            Array.Empty<AssessedClaim>(), receipt.RequestId, null);
        store.Commit(scope, 0, new CaseCommit(
            basis, receipt.EventId, "assessment-service", "assessment-delivered", "delivered",
            At: at, Assessment: assessment, RequestReceipt: receipt));
        store.CreateWorkItem(
            scope, 1,
            new WorkItemDraft("review-a", AssessmentExecutionService.ReviewCapability, "open", "technical-reviewer"),
            "assessment-service");
        store.Commit(scope, 2, new CaseCommit(
            null, null, "finance-reviewer", FinanceEndpoints.ReserveDecisionAction,
            """{"decision":"approve"}""", At: at));
        store.Commit(scope, 3, new CaseCommit(
            null, null, "legal-reviewer", FinanceEndpoints.LegalDecisionAction,
            """{"decision":"signed-off"}""", At: at));

        var review = TechnicalReviewPolicy.Decide(
            store, [caseId], "technical-reviewer", "technical-reviewer", caseId,
            new TechnicalReviewCommand(4, 1, "accept", "Current basis approved."), "trace-a");
        Assert.Equal(TechnicalReviewOutcome.Recorded, review.Outcome);
    }

    private static void AssertStatus(
        JsonElement cases,
        string caseId,
        string? assessment,
        string? review,
        string? request,
        string? finance,
        string? legal,
        string? acceptance)
    {
        var summary = Assert.Single(cases.EnumerateArray(), item =>
            item.GetProperty("caseId").GetString() == caseId);
        var status = summary.GetProperty("status");
        Assert.Equal(assessment, status.GetProperty("assessment").GetString());
        Assert.Equal(review, status.GetProperty("review").GetString());
        Assert.Equal(request, status.GetProperty("request").GetString());
        Assert.Equal(finance, status.GetProperty("finance").GetString());
        Assert.Equal(legal, status.GetProperty("legal").GetString());
        Assert.Equal(acceptance, status.GetProperty("acceptance").GetString());
    }

    [Fact]
    public async Task AC008_CaseListIsReadOnlyAndFailsClosed()
    {
        var store = CreateStore();
        using var host = CreateHost(store);
        SeedCases(store);
        using var client = host.CreateClient();
        var baseline = store.CaptureState();

        using (var unauthenticated = await SendAsync(client, HttpMethod.Get, "/api/v1/cases"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            using var json = await ReadJsonAsync(unauthenticated);
            Assert.Equal(1, json.RootElement.GetProperty("contractVersion").GetInt32());
            Assert.False(json.RootElement.TryGetProperty("cases", out _));
        }
        Assert.Equal(0, store.ReadCalls);

        var session = CreateSession(host, "presenter", CaseIds);
        using (var post = await SendAsync(client, HttpMethod.Post, "/api/v1/cases", session))
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);

        using (var response = await SendAsync(client, HttpMethod.Get, "/api/v1/cases", session))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = await ReadJsonAsync(response);
            var returnedIds = json.RootElement.GetProperty("cases").EnumerateArray()
                .Select(item => item.GetProperty("caseId").GetString()).ToArray();
            Assert.Equal(CaseIds, returnedIds);
            var evaluationIds = Enumerable.Range(1, 40).Select(number => $"EVAL-V2-{number:00}");
            Assert.Empty(returnedIds.Intersect(evaluationIds, StringComparer.Ordinal));
        }
        using (var denied = await SendAsync(client, HttpMethod.Get, "/api/v1/cases",
                   CreateSession(host, "unrecognized-role", CaseIds)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var json = await ReadJsonAsync(denied);
            Assert.False(json.RootElement.TryGetProperty("cases", out _));
        }
        store.ThrowOnRead = true;
        using (var failed = await SendAsync(client, HttpMethod.Get, "/api/v1/cases", session))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal(
                """{"contractVersion":1,"error":{"code":"internal_error","message":"The request could not be completed."}}""",
                await failed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        Assert.Equal(0, store.WriteCalls);
        Assert.Equal(baseline, store.CaptureState());
    }
    private static void SeedSceneHistory(CaseStateStore store, string caseId, string runId = "current-run", int count = 1, int summaryLength = 0, string? eventPrefix = null)
    {
        var scope = store.AuthorizeCase(caseId, "case-pack-test-owner");
        var state = store.Read(scope);
        var basis = state.Basis.MaxBy(item => item.Version);
        if (basis is null)
        {
            var created = CaseStateStore.CreateBasisVersion(1, "current basis", string.Empty);
            store.Commit(scope, state.Revision, new CaseCommit(created, null, "presenter", "basis", "current basis"));
            state = store.Read(scope);
        }
        for (var index = 1; index <= count; index++)
        {
            state = store.Read(scope);
            var summary = summaryLength == 0 ? $"Current run event {index}" : new string('x', summaryLength);
            var prefix = eventPrefix ?? string.Empty;
            var witness = caseId switch
            {
                "cp-case-a" => new CaseJourneyWitness($"{prefix}witness-{index}", "case-a/aircraft-summary.txt", 1, 1, "basis-v1", 1),
                "cp-case-b" => new CaseJourneyWitness($"{prefix}witness-{index}", "case-b/aircraft-summary.txt", 1, 1, "basis-v1", 1),
                "cp-case-c" => new CaseJourneyWitness($"{prefix}witness-{index}", "case-c/aircraft-summary.txt", 1, 1, "basis-v1", 1),
                _ => null,
            };
            store.Commit(scope, state.Revision, new CaseCommit(null, null, "presenter", "journey", "scene history",
                JourneyEvent: new CaseJourneyEventDraft($"{prefix}event-{index}", runId, caseId, "evidence-reviewed", "basis-v1", 1,
                    "presenter", summary, "AI agent", witness is null ? [] : [witness])));
        }
    }

    [Fact]
    public async Task AC027_SceneUsesAuthorizedHistoryAndExactWitnessTuples()
    {
        var store = CreateStore();
        SeedCases(store);
        SeedSceneHistory(store.Inner, "cp-case-a", "current-run", 25);
        var historyEvents = store.Inner.Read(store.Inner.AuthorizeCase("cp-case-a", "case-pack-test-owner"))
            .JourneyEvents!;
        var reader = new CountingArrHistorySource
        {
            CaseId = "cp-case-a", RunId = "current-run", BasisId = "basis-v1",
            Events = historyEvents.Where(item => item.RunId == "current-run" && item.BasisId == "basis-v1").ToArray(),
        };
        using var host = CreateHost(store, history: reader);
        var packages = host.Services.GetRequiredService<EvidencePackageStore>();
        packages.Submit("accepted-a", "cp-case-a", () => new EvidencePackageOutcome("complete",
            [new EvidenceDocumentResult("aircraft-summary.txt", "accepted", "accepted", 1, null)]));
        using var client = host.CreateClient();
        var session = CreateSession(host, "presenter", "cp-case-a");

        using var sceneResponse = await SendAsync(client, HttpMethod.Get, "/api/v1/cases/cp-case-a/scene", session);
        Assert.Equal(HttpStatusCode.OK, sceneResponse.StatusCode);
        var bytes = await sceneResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.True(bytes.Length <= 65_536);
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        Assert.Equal("cp-case-a", root.GetProperty("case").GetProperty("caseId").GetString());
        Assert.Equal("current-run", root.GetProperty("case").GetProperty("currentRunId").GetString());
        Assert.Equal(20, root.GetProperty("history").GetArrayLength());
        Assert.Equal(
            Enumerable.Range(6, 20).Select(index => $"event-{index}").ToArray(),
            root.GetProperty("history").EnumerateArray().Select(item => item.GetProperty("eventId").GetString()).ToArray());
        Assert.True(root.GetProperty("olderHistoryAvailable").GetBoolean());
        Assert.Equal(20, root.GetProperty("evidence").GetArrayLength());
        Assert.Equal(
            Enumerable.Range(6, 20).Select(index => $"witness-{index}").OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            root.GetProperty("evidence").EnumerateArray().Select(item => item.GetProperty("witnessId").GetString()).ToArray());
        Assert.All(root.GetProperty("evidence").EnumerateArray(), witness =>
        {
            Assert.Equal("case-a/aircraft-summary.txt", witness.GetProperty("documentId").GetString());
            Assert.Equal("basis-v1", witness.GetProperty("basisId").GetString());
            Assert.Equal(1, witness.GetProperty("documentVersion").GetInt32());
            Assert.Equal(1, witness.GetProperty("basisVersion").GetInt32());
            Assert.Equal(1, witness.GetProperty("page").GetInt32());
        });
        Assert.Equal([new ArrSelector("cp-case-a", "current-run", "basis-v1")], reader.Selectors);

        reader.CaseId = "cp-case-b";
        using var crossCase = await SendAsync(client, HttpMethod.Get, "/api/v1/cases/cp-case-a/scene", session);
        Assert.Equal(HttpStatusCode.NotFound, crossCase.StatusCode);
        using var crossJson = await ReadJsonAsync(crossCase);
        Assert.False(crossJson.RootElement.TryGetProperty("case", out _));
        Assert.False(crossJson.RootElement.TryGetProperty("evidence", out _));
        Assert.Equal(
            [new ArrSelector("cp-case-a", "current-run", "basis-v1"),
             new ArrSelector("cp-case-a", "current-run", "basis-v1")], reader.Selectors);

        reader.CaseId = "cp-case-a";
        reader.BasisId = "basis-v2";
        using var crossBasis = await SendAsync(client, HttpMethod.Get, "/api/v1/cases/cp-case-a/scene", session);
        Assert.Equal(HttpStatusCode.NotFound, crossBasis.StatusCode);
        using var crossBasisJson = await ReadJsonAsync(crossBasis);
        Assert.False(crossBasisJson.RootElement.TryGetProperty("case", out _));
        Assert.False(crossBasisJson.RootElement.TryGetProperty("evidence", out _));
        Assert.Equal(
            [new ArrSelector("cp-case-a", "current-run", "basis-v1"),
             new ArrSelector("cp-case-a", "current-run", "basis-v1"),
             new ArrSelector("cp-case-a", "current-run", "basis-v1")], reader.Selectors);
    }

    [Fact]
    public async Task AC020_SceneMeetsLatencyAndPayloadBudgets()
    {
        var store = CreateStore();
        var clock = new AdjustableTime { Current = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero) };
        using var host = CreateHost(store, new InMemoryCasePackRequestWindowStore(), clock);
        SeedCases(store);
        SeedSceneHistory(store.Inner, "cp-case-a", "current-run", 25);
        using var client = host.CreateClient();
        var session = CreateSession(host, "presenter", "cp-case-a");
        using (var warmup = await SendAsync(client, HttpMethod.Get, "/api/v1/cases/cp-case-a/scene", session))
        {
            Assert.Equal(HttpStatusCode.OK, warmup.StatusCode);
            Assert.True((await warmup.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length <= 65_536);
        }
        clock.Current = clock.Current.Add(CasePackRequestLimiter.RollingWindow);

        var samples = new double[100];
        for (var index = 0; index < 100; index++)
        {
            if (index > 0 && index % 30 == 0) clock.Current = clock.Current.Add(CasePackRequestLimiter.RollingWindow);
            var started = Stopwatch.GetTimestamp();
            using var response = await SendAsync(client, HttpMethod.Get, "/api/v1/cases/cp-case-a/scene", session);
            var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(body.Length <= 65_536, $"Scene payload was {body.Length} UTF-8 bytes.");
        }
        Array.Sort(samples);
        Assert.True(samples[94] <= 1000, $"Scene p95 was {samples[94]:F3} ms.");

        SeedSceneHistory(store.Inner, "cp-case-a", "oversized-run", 1, 70_000, "oversized-");
        store.ResetWriteCount();
        var baseline = store.CaptureState();
        using var oversized = await SendAsync(client, HttpMethod.Get, "/api/v1/cases/cp-case-a/scene", session);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, oversized.StatusCode);
        using var oversizedJson = await ReadJsonAsync(oversized);
        Assert.Equal("scene_payload_limit", oversizedJson.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(oversizedJson.RootElement.TryGetProperty("case", out _));
        Assert.False(oversizedJson.RootElement.TryGetProperty("evidence", out _));
        Assert.Equal(0, store.WriteCalls);
        Assert.Equal(baseline, store.CaptureState());
    }

    [Fact]
    public async Task AC029_SceneEnforcesPerUserReadLimit()
    {
        var store = CreateStore();
        var windowStore = new InMemoryCasePackRequestWindowStore();
        var clock = new AdjustableTime { Current = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero) };
        var history = new CountingArrHistorySource();
        using var host = CreateHost(store, windowStore, clock, history);
        SeedCases(store);
        SeedSceneHistory(store.Inner, "cp-case-a", "current-run", 25);
        var readerEvents = store.Inner.Read(store.Inner.AuthorizeCase("cp-case-a", "case-pack-test-owner")).JourneyEvents!;
        history.Events = readerEvents.Where(item => item.RunId == "current-run" && item.BasisId == "basis-v1").ToArray();
        using var client = host.CreateClient();
        var sessions = host.Services.GetRequiredService<DemoSessionStore>();
        var userOne = sessions.Create(new DemoUser("scene-user-one", "Scene User One", "presenter", ["cp-case-a"]));
        var userTwo = sessions.Create(new DemoUser("scene-user-two", "Scene User Two", "presenter", ["cp-case-a"]));
        var one = new Session($"{DemoAuthEndpoints.CookieName}={userOne.Id}", userOne);
        var two = new Session($"{DemoAuthEndpoints.CookieName}={userTwo.Id}", userTwo);

        for (var attempt = 1; attempt <= 30; attempt++)
        {
            using var response = await SendAsync(client, HttpMethod.Get, "/api/v1/cases/cp-case-a/scene", one);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            _ = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        }
        using var limited = await SendAsync(client, HttpMethod.Get, "/api/v1/cases/cp-case-a/scene", one);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        using var limitedJson = await ReadJsonAsync(limited);
        Assert.Equal("rate_limit_exceeded", limitedJson.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(limitedJson.RootElement.TryGetProperty("case", out _));

        using var second = await SendAsync(client, HttpMethod.Get, "/api/v1/cases/cp-case-a/scene", two);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        _ = await second.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        using var caseList = await SendAsync(client, HttpMethod.Get, "/api/v1/cases", one);
        Assert.Equal(HttpStatusCode.OK, caseList.StatusCode);
        _ = await caseList.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        clock.Current = clock.Current.AddSeconds(60);
        using var expired = await SendAsync(client, HttpMethod.Get, "/api/v1/cases/cp-case-a/scene", one);
        Assert.Equal(HttpStatusCode.OK, expired.StatusCode);
        Assert.Equal(0, store.WriteCalls);
    }

    [Fact]
    public async Task AC020_CaseListP95MeetsOneSecondBudget()
    {
        var store = CreateStore();
        var clock = new AdjustableTime
        {
            Current = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero),
        };
        using var host = CreateHost(store, new InMemoryCasePackRequestWindowStore(), clock);
        SeedCases(store);
        using var client = host.CreateClient();
        var session = CreateSession(host, "presenter", CaseIds);

        using (var warmup = await SendAsync(client, HttpMethod.Get, "/api/v1/cases", session))
        {
            Assert.Equal(HttpStatusCode.OK, warmup.StatusCode);
            _ = await warmup.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        }
        clock.Current = clock.Current.Add(CasePackRequestLimiter.RollingWindow);

        var samples = new double[100];
        for (var index = 0; index < samples.Length; index++)
        {
            if (index > 0 && index % 30 == 0)
                clock.Current = clock.Current.Add(CasePackRequestLimiter.RollingWindow);
            var started = Stopwatch.GetTimestamp();
            using var response = await SendAsync(client, HttpMethod.Get, "/api/v1/cases", session);
            _ = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Array.Sort(samples);
        Assert.True(samples[94] <= 1000, $"Case-list p95 was {samples[94]:F3} ms.");
        Assert.Equal(0, store.WriteCalls);
    }

    [Fact]
    public async Task AC029_CaseListEnforcesPerUserReadLimit()
    {
        var store = CreateStore();
        var windowStore = new InMemoryCasePackRequestWindowStore();
        var clock = new AdjustableTime
        {
            Current = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero),
        };
        using var host = CreateHost(store, windowStore, clock);
        SeedCases(store);
        using var client = host.CreateClient();
        var sessions = host.Services.GetRequiredService<DemoSessionStore>();
        var userOne = sessions.Create(new DemoUser("user-one", "User One", "presenter", CaseIds));
        var userTwo = sessions.Create(new DemoUser("user-two", "User Two", "presenter", CaseIds));
        var userOneSession = new Session($"{DemoAuthEndpoints.CookieName}={userOne.Id}", userOne);
        var userTwoSession = new Session($"{DemoAuthEndpoints.CookieName}={userTwo.Id}", userTwo);
        var baseline = store.CaptureState();

        for (var attempt = 1; attempt <= 29; attempt++)
        {
            using var response = await SendAsync(client, HttpMethod.Get, "/api/v1/cases", userOneSession);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            _ = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        }

        using (var thirtieth = await SendAsync(client, HttpMethod.Get, "/api/v1/cases", userOneSession))
        {
            Assert.Equal(HttpStatusCode.OK, thirtieth.StatusCode);
            _ = await thirtieth.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        }

        using (var thirtyFirst = await SendAsync(client, HttpMethod.Get, "/api/v1/cases", userOneSession))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, thirtyFirst.StatusCode);
            using var json = await ReadJsonAsync(thirtyFirst);
            Assert.Equal(new[] { "contractVersion", "error" }, Keys(json.RootElement));
            Assert.Equal(1, json.RootElement.GetProperty("contractVersion").GetInt32());
            Assert.False(json.RootElement.TryGetProperty("cases", out _));
            var error = json.RootElement.GetProperty("error");
            Assert.Equal(new[] { "code", "message" }, Keys(error));
            Assert.Equal("rate_limit_exceeded", error.GetProperty("code").GetString());
            Assert.Equal("rate_limit_exceeded", error.GetProperty("message").GetString());
        }
        Assert.Equal(0, store.WriteCalls);
        Assert.Equal(baseline, store.CaptureState());

        using (var secondUser = await SendAsync(client, HttpMethod.Get, "/api/v1/cases", userTwoSession))
        {
            Assert.Equal(HttpStatusCode.OK, secondUser.StatusCode);
            _ = await secondUser.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        }

        clock.Current = clock.Current.AddSeconds(59);
        using (var agedRequest = await SendAsync(client, HttpMethod.Get, "/api/v1/cases", userOneSession))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, agedRequest.StatusCode);
            using var json = await ReadJsonAsync(agedRequest);
            Assert.Equal("rate_limit_exceeded", json.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.False(json.RootElement.TryGetProperty("cases", out _));
        }

        clock.Current = clock.Current.AddSeconds(1);
        using (var expiredWindow = await SendAsync(client, HttpMethod.Get, "/api/v1/cases", userOneSession))
        {
            Assert.Equal(HttpStatusCode.OK, expiredWindow.StatusCode);
            _ = await expiredWindow.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, store.WriteCalls);
        Assert.Equal(baseline, store.CaptureState());
    }

    [Fact]
    public async Task AC025_CaseListRejectsUnsupportedVersion()
    {
        var store = CreateStore();
        using var host = CreateHost(store);
        SeedCases(store);
        using var client = host.CreateClient();
        using var response = await SendAsync(client, HttpMethod.Get, "/api/v2/cases",
            CreateSession(host, "presenter", CaseIds));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = await ReadJsonAsync(response);
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("contractVersion").GetInt32());
        Assert.Equal("unsupported_contract_version", root.GetProperty("error").GetProperty("code").GetString());
        Assert.False(root.TryGetProperty("cases", out _));
    }
    private static void AssertSummaryEnvelope(JsonElement root, string packVersion, string[] expectedIds)
    {
        Assert.Equal(new[] { "cases", "contractVersion", "packVersion" }, Keys(root));
        Assert.Equal(1, root.GetProperty("contractVersion").GetInt32());
        Assert.Equal(packVersion, root.GetProperty("packVersion").GetString());
        var cases = root.GetProperty("cases");
        Assert.Equal(expectedIds, cases.EnumerateArray()
            .Select(item => item.GetProperty("caseId").GetString()).ToArray());

        var summaryKeys = new[]
        {
            "activityId", "aircraftCategory", "capabilityLabel", "caseId", "caseRevision",
            "currentRunId", "name", "nextAction", "owner", "status", "synthetic",
        };
        var statusKeys = new[] { "acceptance", "assessment", "finance", "legal", "request", "review" };
        var categories = new[] { "narrow-body", "regional", "wide-body" };
        var labels = new[] { "AI agent", "Rules-based", "Simulated", "Design-only", "Unknown" };
        foreach (var summary in cases.EnumerateArray())
        {
            Assert.Equal(summaryKeys, Keys(summary));
            Assert.True(categories.Contains(summary.GetProperty("aircraftCategory").GetString(), StringComparer.Ordinal));
            Assert.True(summary.GetProperty("caseRevision").GetInt64() >= 0);
            Assert.Equal(JsonValueKind.String, summary.GetProperty("name").ValueKind);
            Assert.Equal(JsonValueKind.String, summary.GetProperty("currentRunId").ValueKind);
            Assert.Equal(JsonValueKind.True, summary.GetProperty("synthetic").ValueKind);
            Assert.True(labels.Contains(summary.GetProperty("capabilityLabel").GetString(), StringComparer.Ordinal));
            Assert.True(summary.GetProperty("owner").ValueKind is JsonValueKind.String or JsonValueKind.Null);
            Assert.True(summary.GetProperty("activityId").ValueKind is JsonValueKind.String or JsonValueKind.Null);

            var status = summary.GetProperty("status");
            Assert.Equal(statusKeys, Keys(status));
            foreach (var property in status.EnumerateObject())
                Assert.True(property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null);

            var nextAction = summary.GetProperty("nextAction");
            if (nextAction.ValueKind == JsonValueKind.Object)
            {
                Assert.Equal(new[] { "actionId", "text" }, Keys(nextAction));
                Assert.Equal(JsonValueKind.String, nextAction.GetProperty("actionId").ValueKind);
                Assert.Equal(JsonValueKind.String, nextAction.GetProperty("text").ValueKind);
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, nextAction.ValueKind);
            }
        }
    }
    [Fact]
    public async Task AC013_SceneHidesUnknownAndOutOfScopeCases()
    {
        var store = CreateStore();
        using var host = CreateHost(store);
        SeedCases(store);
        SeedCurrentArrProjection(store.Inner, "cp-case-a");
        using var client = host.CreateClient();
        var baseline = store.CaptureState();

        using (var unauthenticated = await SendAsync(client, HttpMethod.Get, "/api/v1/cases/cp-case-a/scene"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            using var json = await ReadJsonAsync(unauthenticated);
            Assert.Equal(1, json.RootElement.GetProperty("contractVersion").GetInt32());
            Assert.False(json.RootElement.TryGetProperty("case", out _));
        }

        var session = CreateSession(host, "presenter", "cp-case-a");
        using (var inScope = await SendAsync(client, HttpMethod.Get, "/api/v1/cases/cp-case-a/scene", session))
        {
            Assert.Equal(HttpStatusCode.OK, inScope.StatusCode);
            using var json = await ReadJsonAsync(inScope);
            Assert.Equal("cp-case-a", json.RootElement.GetProperty("case").GetProperty("caseId").GetString());
        }

        foreach (var caseId in new[] { "cp-case-b", "cp-case-z", "quote\"id", "*", "slash/id", "path/like/identifier" })
        {
            var escaped = Uri.EscapeDataString(caseId);
            using var response = await SendAsync(client, HttpMethod.Get, $"/api/v1/cases/{escaped}/scene", session);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(
                "{\"contractVersion\":1,\"error\":{\"code\":\"not_found\",\"message\":\"Not found.\"}}",
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal(0, store.WriteCalls);
        Assert.Equal(baseline, store.CaptureState());
    }

    [Fact]
    public async Task AC011_SceneRejectsUnsupportedVersionMethodAndStoreFailure()
    {
        var store = CreateStore();
        using var host = CreateHost(store);
        SeedCases(store);
        SeedCurrentArrProjection(store.Inner, "cp-case-a");
        using var client = host.CreateClient();
        var session = CreateSession(host, "presenter", CaseIds);
        var baseline = store.CaptureState();

        using (var unsupported = await SendAsync(client, HttpMethod.Get, "/api/v2/cases/cp-case-a/scene", session))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
            using var json = await ReadJsonAsync(unsupported);
            Assert.Equal(1, json.RootElement.GetProperty("contractVersion").GetInt32());
            Assert.Equal("unsupported_contract_version", json.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.False(json.RootElement.TryGetProperty("case", out _));
        }

        using (var method = await SendAsync(client, HttpMethod.Post, "/api/v1/cases/cp-case-a/scene", session))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, method.StatusCode);
            using var json = await ReadJsonAsync(method);
            Assert.Equal(1, json.RootElement.GetProperty("contractVersion").GetInt32());
            Assert.False(json.RootElement.TryGetProperty("objects", out _));
        }

        store.ThrowOnRead = true;
        using (var failed = await SendAsync(client, HttpMethod.Get, "/api/v1/cases/cp-case-a/scene", session))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal(
                "{\"contractVersion\":1,\"error\":{\"code\":\"internal_error\",\"message\":\"The request could not be completed.\"}}",
                await failed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.DoesNotContain("private case-store detail", await failed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Equal(0, store.WriteCalls);
        store.ThrowOnRead = false;
        Assert.Equal(baseline, store.CaptureState());
    }

    [Fact]
    public async Task AC014_ResetEndpointRequiresPresenterAndCommits()
    {
        var store = CreateStore();
        var clock = new AdjustableTime { Current = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero) };
        using var host = CreateHost(store, time: clock);
        SeedCases(store);
        using var client = host.CreateClient();
        var presenter = CreateSession(host, "presenter", "cp-case-c");
        var before = store.CaptureState();

        using var accepted = await SendResetAsync(client, "cp-case-c", presenter,
            new { contractVersion = 1, commandId = "reset-014", expectedRevision = 0L, variant = "blocked",
                actor = "caller-actor", reasonCode = "caller_reason", at = "2000-01-01T00:00:00Z" });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var receiptJson = await ReadJsonAsync(accepted);
        Assert.Equal(["auditEventId", "caseId", "commandId", "contractVersion", "newRevision", "newRunId", "priorRevision"],
            Keys(receiptJson.RootElement));
        Assert.Equal(0, receiptJson.RootElement.GetProperty("priorRevision").GetInt64());
        Assert.Equal(1, receiptJson.RootElement.GetProperty("newRevision").GetInt64());
        Assert.Equal(1, store.WriteCalls);
        Assert.NotEqual(before, store.CaptureState());
        var scope = store.Inner.AuthorizeCase("cp-case-c", "case-pack-test-owner");
        var state = store.Inner.Read(scope);
        var audit = Assert.Single(state.Audit);
        Assert.Equal(presenter.Value.User.Id, audit.Actor);
        Assert.Equal("presenter_reset_blocked_variant", audit.Action);
        Assert.Contains("reasonCode=presenter_reset_blocked_variant", audit.Detail, StringComparison.Ordinal);
        Assert.Equal(clock.Current, audit.At);
    }

    [Fact]
    public async Task AC015_ResetRejectsRolesScopeAndAntiForgeryWithoutMutation()
    {
        var store = CreateStore();
        var clock = new AdjustableTime { Current = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero) };
        var windowStore = new InMemoryCasePackRequestWindowStore();
        using var host = CreateHost(store, windowStore, clock);
        SeedCases(store);
        using var client = host.CreateClient();
        foreach (var role in new[] { "technical-reviewer", "finance-reviewer", "legal-reviewer", "executive" })
        {
            var before = store.CaptureState();
            var session = CreateSession(host, role, "cp-case-c");
            using var response = await SendResetAsync(client, "cp-case-c", session,
                new { contractVersion = 1, commandId = $"role-{role}", expectedRevision = 0L, variant = "blocked" });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(before, store.CaptureState());
        }

        var presenter = CreateSession(host, "presenter", "cp-case-c");

        var unauthenticated = new HttpRequestMessage(HttpMethod.Post,
            "/api/v1/cases/cp-case-c/reset-blocked-variant") { Content = JsonContent.Create(new { contractVersion = 1, commandId = "unauthenticated-reset", expectedRevision = 0L, variant = "blocked" }) };
        unauthenticated.Headers.Add(DemoAuthEndpoints.CsrfHeaderName, "not-required-when-unauthenticated");
        var beforeUnauthenticated = store.CaptureState();
        using var unauthenticatedResponse = await client.SendAsync(unauthenticated, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedResponse.StatusCode);
        Assert.Equal(beforeUnauthenticated, store.CaptureState());
        Assert.Equal(0, store.WriteCalls);

        var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/v1/cases/cp-case-c/reset-blocked-variant");
        request.Headers.Add("Cookie", presenter.Cookie);
        request.Headers.Add(DemoAuthEndpoints.CsrfHeaderName, "invalid-token");
        request.Content = JsonContent.Create(new { contractVersion = 1, commandId = "bad-csrf", expectedRevision = 0L, variant = "blocked" });
        var beforeCsrf = store.CaptureState();
        using var csrf = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        Assert.Equal(beforeCsrf, store.CaptureState());

        var missingRequest = new HttpRequestMessage(HttpMethod.Post,
            "/api/v1/cases/cp-case-c/reset-blocked-variant") { Content = JsonContent.Create(new { contractVersion = 1, commandId = "missing-csrf", expectedRevision = 0L, variant = "blocked" }) };
        missingRequest.Headers.Add("Cookie", presenter.Cookie);
        var beforeMissingCsrf = store.CaptureState();
        using var missingCsrf = await client.SendAsync(missingRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        Assert.Equal(beforeMissingCsrf, store.CaptureState());

        var outOfScope = CreateSession(host, "presenter", "cp-case-a");
        var beforeScope = store.CaptureState();
        using var scopeResponse = await SendResetAsync(client, "cp-case-c", outOfScope,
            new { contractVersion = 1, commandId = "bad-scope", expectedRevision = 0L, variant = "blocked" });
        Assert.Equal(HttpStatusCode.NotFound, scopeResponse.StatusCode);
        Assert.Equal(beforeScope, store.CaptureState());

        var quotaPresenter = CreateSession(host, "presenter", "cp-case-c");
        for (var attempt = 0; attempt < CasePackRequestLimiter.MaxResetRequests; attempt++)
        {
            using var accepted = await SendResetAsync(client, "cp-case-c", quotaPresenter,
                new { contractVersion = 1, commandId = $"quota-{attempt}", expectedRevision = (long)attempt, variant = "blocked" });
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
        store.ResetWriteCount();
        var beforeQuota = store.CaptureState();
        using var exhausted = await SendResetAsync(client, "cp-case-c", quotaPresenter,
            new { contractVersion = 1, commandId = "quota-exhausted", expectedRevision = 3L, variant = "blocked" });
        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted.StatusCode);
        Assert.Equal(beforeQuota, store.CaptureState());
        Assert.Equal(0, store.WriteCalls);
    }

    [Fact]
    public async Task AC016_And_AC023_ResetReplayRevisionAndConflictResponsesAreExact()
    {
        var store = CreateStore();
        var clock = new AdjustableTime { Current = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero) };
        var windowStore = new InMemoryCasePackRequestWindowStore();
        using var host = CreateHost(store, windowStore, clock);
        SeedCases(store);
        using var client = host.CreateClient();
        var presenter = CreateSession(host, "presenter", "cp-case-c");
        var body = new { contractVersion = 1, commandId = "reset-replay", expectedRevision = 0L, variant = "blocked" };

        using var accepted = await SendResetAsync(client, "cp-case-c", presenter, body);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var replayJson = await ReadJsonAsync(accepted);
        using var replay = await SendResetAsync(client, "cp-case-c", presenter, body);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayedJson = await ReadJsonAsync(replay);
        Assert.Equal(replayJson.RootElement.GetRawText(), replayedJson.RootElement.GetRawText());

        var cScope = store.Inner.AuthorizeCase("cp-case-c", "case-pack-test-owner");
        Assert.Equal(1, store.Inner.Read(cScope).Revision);
        store.Inner.Commit(cScope, store.Inner.Read(cScope).Revision,
            new CaseCommit(null, null, "presenter", "advance", "test advancement"));
        store.ResetWriteCount();
        using var stale = await SendResetAsync(client, "cp-case-c", presenter,
            new { contractVersion = 1, commandId = "reset-stale", expectedRevision = 0L, variant = "blocked" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("stale_revision", (await ReadJsonAsync(stale)).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, store.WriteCalls);

        clock.Current = clock.Current.Add(CasePackRequestLimiter.RollingWindow);
        var beforeUnsupported = store.CaptureState();
        using var unsupported = await SendResetAsync(client, "cp-case-c", presenter,
            new { contractVersion = 1, commandId = "reset-replay", expectedRevision = 0L, variant = "other" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unsupported.StatusCode);
        Assert.Equal(beforeUnsupported, store.CaptureState());

        var beforeConflict = store.CaptureState();
        using var conflict = await SendResetAsync(client, "cp-case-c", presenter,
            new { contractVersion = 1, commandId = "reset-replay", expectedRevision = 99L, variant = "blocked" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var conflictJson = await ReadJsonAsync(conflict);
        Assert.Equal("command_id_conflict", conflictJson.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(beforeConflict, store.CaptureState());

        clock.Current = clock.Current.Add(CasePackRequestLimiter.RollingWindow);
        foreach (var unsupportedCase in new[] { "cp-case-a", "cp-case-b" })
        {
            var unsupportedBefore = store.CaptureState();
            var unsupportedSession = CreateSession(host, "presenter", unsupportedCase);
            using var response = await SendResetAsync(client, unsupportedCase, unsupportedSession,
                new { contractVersion = 1, commandId = $"unsupported-{unsupportedCase}", expectedRevision = 0L, variant = "blocked" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(unsupportedBefore, store.CaptureState());
        }

        clock.Current = clock.Current.Add(CasePackRequestLimiter.RollingWindow);
        var differentActor = CreateSession(host, "presenter", "cp-case-c");
        var replayWithOtherActor = await SendResetAsync(client, "cp-case-c", differentActor, body);
        Assert.Equal(HttpStatusCode.Conflict, replayWithOtherActor.StatusCode);
        using var actorConflictJson = await ReadJsonAsync(replayWithOtherActor);
        Assert.Equal("command_id_conflict", actorConflictJson.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task M03_ResetAuthorizationChecksRunInRequiredOrder()
    {
        var store = CreateStore();
        var clock = new AdjustableTime { Current = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero) };
        var windowStore = new InMemoryCasePackRequestWindowStore();
        using var host = CreateHost(store, windowStore, clock);
        SeedCases(store);
        using var client = host.CreateClient();
        var blockedBody = new { contractVersion = 1, commandId = "m03", expectedRevision = 0L, variant = "blocked" };

        var cases = new (string Stage, Session Session, string CaseId, object Body, HttpStatusCode Expected)[
        ]
        {
            ("role", CreateSession(host, "technical-reviewer", "cp-case-c"), "cp-case-c", blockedBody, HttpStatusCode.Forbidden),
            ("scope", CreateSession(host, "presenter", "cp-case-a"), "cp-case-c", blockedBody, HttpStatusCode.NotFound),
            ("antiForgery", CreateSession(host, "presenter", "cp-case-c"), "cp-case-c", blockedBody, HttpStatusCode.BadRequest),
            ("variant", CreateSession(host, "presenter", "cp-case-c"), "cp-case-c",
                new { contractVersion = 1, commandId = "m03-other", expectedRevision = 0L, variant = "other" }, HttpStatusCode.UnprocessableEntity),
            ("unsupportedCase", CreateSession(host, "presenter", "cp-case-a"), "cp-case-a", blockedBody, HttpStatusCode.UnprocessableEntity),
        };

        foreach (var test in cases)
        {
            store.ResetCheckCounts();
            var before = store.CaptureState();
            HttpResponseMessage response;
            if (test.Stage == "antiForgery")
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/cases/{test.CaseId}/reset-blocked-variant")
                { Content = JsonContent.Create(test.Body) };
                request.Headers.Add("Cookie", test.Session.Cookie);
                request.Headers.Add(DemoAuthEndpoints.CsrfHeaderName, "bad");
                response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            }
            else
            {
                response = await SendResetAsync(client, test.CaseId, test.Session, test.Body);
            }
            using (response)
            {
                Assert.Equal(test.Expected, response.StatusCode);
                Assert.Equal(before, store.CaptureState());
                if (test.Stage is "role" or "scope" or "antiForgery" or "variant" or "unsupportedCase")
                    Assert.Equal(0, store.ReadCalls);
                Assert.Equal(0, store.WriteCalls);
            }
        }

        var presenter = CreateSession(host, "presenter", "cp-case-c");
        using var accepted = await SendResetAsync(client, "cp-case-c", presenter, blockedBody);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        store.ResetCheckCounts();
        var replayBefore = store.CaptureState();
        using var replay = await SendResetAsync(client, "cp-case-c", presenter, blockedBody);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(replayBefore, store.CaptureState());
        Assert.Equal(1, store.ResetCommitCalls);
        Assert.Equal(0, store.WriteCalls);

        clock.Current = clock.Current.Add(CasePackRequestLimiter.RollingWindow);
        store.ResetCheckCounts();
        var staleBefore = store.CaptureState();
        using var stale = await SendResetAsync(client, "cp-case-c", presenter,
            new { contractVersion = 1, commandId = "m03-stale", expectedRevision = 99L, variant = "blocked" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("stale_revision", (await ReadJsonAsync(stale)).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(staleBefore, store.CaptureState());
        Assert.Equal(1, store.ResetCommitCalls);
        Assert.Equal(0, store.WriteCalls);
    }

    [Fact]
    public async Task AC024_SceneResetPermissionMatchesRoleScopeAndVariantTable()
    {
        var store = CreateStore();
        using var host = CreateHost(store);
        SeedCases(store);
        SeedCurrentArrProjection(store.Inner, "cp-case-c");
        var cScope = store.Inner.AuthorizeCase("cp-case-c", "case-pack-test-owner");
        var cState = store.Inner.Read(cScope);
        store.Inner.Commit(cScope, cState.Revision, new CaseCommit(
            null, null, "presenter", "scene-ready", "scenario ready",
            JourneyEvent: new CaseJourneyEventDraft(
                "scene-c", "scene-run-c", "cp-case-c", "scene.ready", "basis-v1", 1,
                "presenter", "Case C scene ready", "AI agent", [])));
        var packages = host.Services.GetRequiredService<EvidencePackageStore>();
        packages.Submit("blocked-c", "cp-case-c", () => new EvidencePackageOutcome("blocked",
            [new EvidenceDocumentResult("blocked.pdf", "rejected-retryable", "blocked", null, null)]));
        using var client = host.CreateClient();

        foreach (var test in new[]
        {
            (Role: "presenter", Scope: "cp-case-c", CaseId: "cp-case-c", Blocked: true, Expected: true),
            (Role: "technical-reviewer", Scope: "cp-case-c", CaseId: "cp-case-c", Blocked: true, Expected: false),
            (Role: "presenter", Scope: "cp-case-a", CaseId: "cp-case-c", Blocked: true, Expected: false),
            (Role: "presenter", Scope: "cp-case-c", CaseId: "cp-case-a", Blocked: true, Expected: false),
            (Role: "presenter", Scope: "cp-case-c", CaseId: "cp-case-c", Blocked: false, Expected: false),
        })
        {
            if (!test.Blocked && test.CaseId == "cp-case-c")
            {
                packages.Submit("complete-c", "cp-case-c", () => new EvidencePackageOutcome("complete", []));
            }

            var session = CreateSession(host, test.Role, test.Scope);
            using var response = await SendAsync(client, HttpMethod.Get, $"/api/v1/cases/{test.CaseId}/scene", session);
            if (test.Scope != test.CaseId)
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                continue;
            }
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = await ReadJsonAsync(response);
            var root = json.RootElement;
            Assert.Equal(test.Expected, root.GetProperty("canResetBlockedVariant").GetBoolean());
            var allowed = root.GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()).ToArray();
            if (test.Expected)
            {
                Assert.Equal(["resetBlockedVariant"], allowed);
            }
            else
            {
                Assert.DoesNotContain("resetBlockedVariant", allowed);
            }
        }

        Assert.Equal(0, store.WriteCalls);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
