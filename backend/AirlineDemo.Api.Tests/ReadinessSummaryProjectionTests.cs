using System.Net;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Dashboard;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Requests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirlineDemo.Api.Tests;

public sealed class ReadinessSummaryProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task VersionedContractUsesFixtureDatesAndRejectsUnsupportedVersion()
    {
        var store = new CountingStore(FixtureState());
        var clock = new MutableClock(Now);
        using var factory = Factory(store, clock);
        using var client = factory.CreateClient();
        var cookie = Session(factory, "presenter", ["case-001"]);

        using var first = await Get(client, "?contractVersion=1", cookie);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, firstJson.RootElement.GetProperty("contractVersion").GetInt32());
        Assert.Equal("Return in 9 months", firstJson.RootElement.GetProperty("countdown").GetString());

        clock.Now = Now.AddYears(20); cookie = Session(factory, "presenter", ["case-001"]);
        using var second = await Get(client, "?contractVersion=1", cookie);
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("Return in 9 months", secondJson.RootElement.GetProperty("countdown").GetString());
        Assert.Equal(2, store.Reads);

        foreach (var version in new[] { 0, 2 })
        {
            using var unsupported = await Get(client, $"?contractVersion={version}", cookie);
            Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
            Assert.Equal("""{"error":"Unsupported contract version."}""", await unsupported.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        Assert.Equal(2, store.Reads);
    }

    [Fact]
    public async Task UsesQueueProjectionsAndFailsOnInconsistency()
    {
        var state = FixtureState();
        var store = new CountingStore(state, state with { WorkItems = [] });
        using var factory = Factory(store, new MutableClock(Now));
        using var client = factory.CreateClient();
        using var response = await Get(client, "?contractVersion=1", Session(factory, "presenter", ["case-001"]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new[] { 1, 1, 1, 2 }, new[]
        {
            json.RootElement.GetProperty("recordsGapsFound").GetInt32(),
            json.RootElement.GetProperty("routineRequestsSent").GetInt32(),
            json.RootElement.GetProperty("itemsHeldForInternalReview").GetInt32(),
            json.RootElement.GetProperty("documentsNotProcessed").GetInt32(),
        });
        Assert.Equal(1, store.Reads);

        var inconsistent = state with { RequestReceipts = [] };
        using var brokenFactory = Factory(new CountingStore(inconsistent), new MutableClock(Now));
        using var brokenClient = brokenFactory.CreateClient();
        using var unavailable = await Get(brokenClient, "?contractVersion=1", Session(brokenFactory, "presenter", ["case-001"]));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("""{"contractVersion":1,"status":"unavailable"}""", await unavailable.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExcludesAmbiguousAndUnprocessedItemsFromGaps()
    {
        var fixture = FixtureState();
        var ordinary = fixture with
        {
            WorkItems = fixture.WorkItems!.Where(item => item.ItemId != "held-1").ToArray(),
            Assessments = fixture.Assessments!.Where(item => item.WorkItemId is null).ToArray(),
        };
        var ambiguous = ordinary with
        {
            Assessments = ordinary.Assessments!.Append(Assessment("ambiguous", "Review", "ambiguous identity", workItemId: "ambiguous")).ToArray(),
            WorkItems = ordinary.WorkItems!.Append(Item("ambiguous", AssessmentExecutionService.ReviewCapability,
                CapabilityProjection.ReviewerRole)).ToArray(),
        };
        var cases = new[]
        {
            (State: ordinary, Packages: PackageStore(), Gaps: 1, Held: 0, Unprocessed: 2),
            (State: ambiguous, Packages: PackageStore(), Gaps: 1, Held: 1, Unprocessed: 2),
            (State: ordinary, Packages: PackageStore(3), Gaps: 1, Held: 0, Unprocessed: 3),
        };
        foreach (var item in cases)
        {
            var summary = Build(item.State, item.Packages)!;
            Assert.Equal(item.Gaps, summary.RecordsGapsFound);
            Assert.Equal(item.Held, summary.ItemsHeldForInternalReview);
            Assert.Equal(item.Unprocessed, summary.DocumentsNotProcessed);
        }
    }

    [Fact]
    public async Task DeniesUnauthorizedUnsupportedAndOutOfScopeRequestsBeforeProjectionRead()
    {
        var store = new CountingStore(FixtureState());
        using var factory = Factory(store, new MutableClock(Now));
        using var client = factory.CreateClient();

        using var unauthenticated = await client.GetAsync("/api/dashboard/readiness-summary?contractVersion=1", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode); Assert.Equal("""{"error":"Not signed in."}""", await unauthenticated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var forbidden = await Get(client, "?contractVersion=1", Session(factory, "finance-reviewer", ["case-001"]));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode); Assert.Equal("""{"error":"Role not permitted."}""", await forbidden.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var outOfScope = await Get(client, "?contractVersion=1", Session(factory, "presenter", ["case-002"]));
        Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);
        Assert.Equal(string.Empty, await outOfScope.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var unsupported = await Get(client, "?contractVersion=0", Session(factory, "presenter", ["case-001"]));
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
        using var forgedIds = await Get(client, "?contractVersion=1&caseId=case-002&runId=case-002",
            Session(factory, "presenter", ["case-001", "case-002"]));
        Assert.Equal(HttpStatusCode.NotFound, forgedIds.StatusCode);
        Assert.Equal(string.Empty, await forgedIds.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, store.Reads);

        using var ignoredRunId = await Get(client, "?contractVersion=1&runId=case-002",
            Session(factory, "presenter", ["case-001"]));
        Assert.Equal(HttpStatusCode.OK, ignoredRunId.StatusCode);
        Assert.Equal(1, store.Reads);
        Assert.Equal("case-001", store.LastScope);
    }

    private static CaseState FixtureState()
    {
        var request = new DeliveryReceipt("event-1", "request-1", "case-001", "report-1", "basis-1", 1, "1", "request");
        return new CaseState("case-001", "presenter", 1, [], [], [], new Dictionary<string, string>(),
            [Item("held-1", AssessmentExecutionService.ReviewCapability, CapabilityProjection.ReviewerRole)],
            [new InvestigationTrace("case-001", "pkg-1", "investigation-1", 1, "model", "api", "region", "filter", "escalate", "actor", "approved", [])],
            [Assessment("assessment-1", AssessmentExecutionService.Request, "missing history", "request-1"),
                Assessment("review-1", AssessmentExecutionService.Review, "ambiguous", workItemId: "held-1")], [request]);
    }

    private static ReadinessSummary? Build(CaseState state, EvidencePackageStore packages) =>
        ReadinessSummaryProjection.Build(state, (state.Traces ?? []).Select(trace => packages.Find(state.CaseId, trace.RunId)).ToArray());

    private static EvidencePackageStore PackageStore(int rejected = 2)
    {
        var packages = new EvidencePackageStore();
        var documents = new[] { new EvidenceDocumentResult("accepted.pdf", "accepted", "accepted", 1, "digest") }
            .Concat(Enumerable.Range(1, rejected).Select(i => new EvidenceDocumentResult($"rejected-{i}.pdf", "rejected-permanent", "parse failed", null, null)))
            .ToArray();
        packages.Submit("pkg-1", "case-001", () => new EvidencePackageOutcome(rejected == 0 ? "complete" : "partial", documents));
        return packages;
    }

    private static CaseAssessment Assessment(string id, string outcome, string reason, string? requestId = null, string? workItemId = null) =>
        new(id, "case-001", "basis-1", 1, outcome, reason, outcome == AssessmentExecutionService.Request
            ? [new AssessedClaim("MissingRequirement", "history", "engine LLP", "serial-1", [])] : [], requestId, workItemId);

    private static WorkItem Item(string id, string capability, string owner) =>
        new("case-001", id, capability, "open", owner, Now);

    private static WebApplicationFactory<Program> Factory(CountingStore store, TimeProvider clock) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICaseStateStore>(); services.AddSingleton<ICaseStateStore>(store);
            services.RemoveAll<EvidencePackageStore>(); services.AddSingleton(PackageStore());
            services.RemoveAll<TimeProvider>(); services.AddSingleton(clock);
        }));

    private static string Session(WebApplicationFactory<Program> factory, string role, IReadOnlyList<string> scopes) =>
        $"{DemoAuthEndpoints.CookieName}={factory.Services.GetRequiredService<DemoSessionStore>().Create(new DemoUser("test-user", "Test user", role, scopes)).Id}";

    private static Task<HttpResponseMessage> Get(HttpClient client, string query, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/readiness-summary" + query); request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }
    private sealed class CountingStore(CaseState first, CaseState? second = null) : ICaseStateStore
    {
        public int Reads { get; private set; }
        public string? LastScope { get; private set; }
        public string? OwnerOf(string caseId) => caseId == "case-001" ? "presenter" : null;
        public CaseScope AuthorizeCase(string caseId, string owner) =>
            OwnerOf(caseId) == owner ? new CaseScope(caseId) : throw new CaseAccessDeniedException();
        public CaseState Read(CaseScope scope)
        {
            Reads++;
            LastScope = scope.CaseId;
            return Reads == 1 ? first : second ?? first;
        }
        public CaseScope CreateCase(string caseId, string owner) => throw new NotSupportedException(); public BasisVersion GetBasisVersion(CaseScope scope, int version) => throw new NotSupportedException();
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => throw new NotSupportedException(); public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit) => throw new NotSupportedException();
        public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope) => throw new NotSupportedException(); public CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor) => throw new NotSupportedException();
        public CommitResult ChangeWorkItemState(CaseScope scope, long expectedRevision, string itemId, string state, string actor) => throw new NotSupportedException();         public void AppendTrace(CaseScope scope, InvestigationTrace trace) => throw new NotSupportedException();
        public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId) => throw new NotSupportedException();
    }
    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now;
    }
}
