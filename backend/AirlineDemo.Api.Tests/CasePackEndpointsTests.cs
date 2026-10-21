using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AirlineDemo.Api.Admission;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Finance;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Projection;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
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
    private sealed class CountingCaseStateStore(CaseStateStore inner) : ICaseStateStore
    {
        public CaseStateStore Inner { get; } = inner;
        public int ReadCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public bool ThrowOnRead { get; set; }

        public CaseScope AuthorizeCase(string caseId, string owner) => Inner.AuthorizeCase(caseId, owner);
        public string? OwnerOf(string caseId) => Inner.OwnerOf(caseId);
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
        { WriteCalls++; return Inner.Commit(scope, expectedRevision, commit); }
        public CasePackResetCommitResult CommitBlockedVariantReset(
            CaseScope scope, long expectedRevision, string actor, string commandId, string variant)
        { WriteCalls++; return Inner.CommitBlockedVariantReset(scope, expectedRevision, actor, commandId, variant); }
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
        public void ResetWriteCount() => WriteCalls = 0;
    }
    private WebApplicationFactory<Program> CreateHost(
        CountingCaseStateStore store,
        ICasePackRequestWindowStore? windowStore = null,
        TimeProvider? time = null) =>
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
        var value = host.Services.GetRequiredService<DemoSessionStore>()
            .Create(new DemoUser("case-pack-reader", "Case Pack Reader", role, scopes));
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
        var current = store.Read(scope);
        store.Commit(scope, current.Revision, new CaseCommit(
            null, null, "case-pack-test-owner", "scene.seed", "Seed current scene label",
            At: at, JourneyEvent: new CaseJourneyEventDraft(
                "scene-event-" + caseId, "scene-run-" + caseId, caseId,
                "evidence-reviewed", "basis-v1", 1, "presenter", "Current scene", "AI agent", [])));
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
    [Fact]
    public async Task AC013_SceneHidesUnknownAndOutOfScopeCases()
    {
        var store = CreateStore();
        using var host = CreateHost(store);
        SeedCases(store);
        SeedCurrentArrProjection(store.Inner, "cp-case-a");
        using var client = host.CreateClient();
        var inScope = CreateSession(host, "presenter", "cp-case-a");
        var outOfScope = CreateSession(host, "presenter", "cp-case-a");
        var baseline = store.CaptureState();

        using (var unauthenticated = await SendAsync(client, HttpMethod.Get,
                   "/api/v1/cases/cp-case-a/scene"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            using var json = await ReadJsonAsync(unauthenticated);
            Assert.Equal(1, json.RootElement.GetProperty("contractVersion").GetInt32());
            Assert.False(json.RootElement.TryGetProperty("case", out _));
        }

        using var success = await SendAsync(client, HttpMethod.Get,
            "/api/v1/cases/cp-case-a/scene", inScope);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        using var successJson = await ReadJsonAsync(success);
        Assert.Equal("cp-case-a", successJson.RootElement.GetProperty("case").GetProperty("caseId").GetString());

        const string notFound = "{\"contractVersion\":1,\"error\":{\"code\":\"not_found\",\"message\":\"Not found.\"}}";
        foreach (var value in new[] { "cp-case-b", "cp-case-z", "cp-case-a\"", "*", "path/like/id", "cp-case-path-like" })
        {
            var session = value == "cp-case-b" ? outOfScope : inScope;
            using var response = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/cases/{value}/scene", session);
            var responseBodyForStatus = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{value}: {response.StatusCode}: {responseBodyForStatus}");
            var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(responseBody == notFound, $"{value}: {responseBody}");
            using var json = await ReadJsonAsync(response);
            Assert.False(json.RootElement.TryGetProperty("case", out _));
            Assert.False(json.RootElement.TryGetProperty("objects", out _));
            Assert.False(json.RootElement.TryGetProperty("history", out _));
            Assert.False(json.RootElement.TryGetProperty("documentRefs", out _));
            Assert.False(json.RootElement.TryGetProperty("evidence", out _));
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

        using var unsupported = await SendAsync(client, HttpMethod.Get,
            "/api/v2/cases/cp-case-a/scene", session);
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
        Assert.Equal("unsupported_contract_version",
            (await ReadJsonAsync(unsupported)).RootElement.GetProperty("error").GetProperty("code").GetString());

        using var nonGet = await SendAsync(client, HttpMethod.Post,
            "/api/v1/cases/cp-case-a/scene", session);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, nonGet.StatusCode);
        using var methodJson = await ReadJsonAsync(nonGet);
        Assert.False(methodJson.RootElement.TryGetProperty("case", out _));
        Assert.Equal(1, methodJson.RootElement.GetProperty("contractVersion").GetInt32());

        store.ThrowOnRead = true;
        using var failed = await SendAsync(client, HttpMethod.Get,
            "/api/v1/cases/cp-case-a/scene", session);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(
            "{\"contractVersion\":1,\"error\":{\"code\":\"internal_error\",\"message\":\"The request could not be completed.\"}}",
            await failed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(0, store.WriteCalls);
        Assert.Equal(baseline, store.CaptureState());
    }

    [Theory]
    [InlineData("presenter", "cp-case-c", true, true)]
    [InlineData("presenter", "cp-case-a", true, false)]
    [InlineData("presenter", "cp-case-c", false, false)]
    [InlineData("technical-reviewer", "cp-case-c", true, false)]
    public async Task AC024_SceneResetPermissionMatchesRoleScopeAndVariantTable(
        string role, string caseId, bool blockedVariant, bool expected)
    {
        var store = CreateStore();
        using var host = CreateHost(store);
        SeedCases(store);
        SeedCurrentArrProjection(store.Inner, caseId);
        if (blockedVariant)
        {
            var packages = host.Services.GetRequiredService<EvidencePackageStore>();
            packages.Submit("blocked-package", caseId, () => new EvidencePackageOutcome(
                "blocked", [new EvidenceDocumentResult("blocked-document.txt", "rejected-retryable",
                    "blocked", null, null)]));
        }
        using var client = host.CreateClient();
        var session = CreateSession(host, role, caseId);

        using var response = await SendAsync(client, HttpMethod.Get,
            $"/api/v1/cases/{caseId}/scene", session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await ReadJsonAsync(response);
        Assert.Equal(expected, json.RootElement.GetProperty("canResetBlockedVariant").GetBoolean());
        var actions = json.RootElement.GetProperty("allowedActions").EnumerateArray()
            .Select(item => item.GetString()).ToArray();
        Assert.Equal(expected ? ["resetBlockedVariant"] : [], actions);
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
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
