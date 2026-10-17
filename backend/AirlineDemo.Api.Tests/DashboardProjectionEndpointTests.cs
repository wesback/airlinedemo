using System.Net;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Dashboard;
using AirlineDemo.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirlineDemo.Api.Tests;

public sealed class DashboardProjectionEndpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AC021_ProjectionUsesOneSnapshotAndProjectsQueueAndActivity()
    {
        var store = NewStore();
        var now = Now;
        store.Set("case-a", State("case-a",
        [
            Item("open-old", "records-investigation", "open", "presenter", now.AddSeconds(-150.9)),
            Item("open-new", "records-investigation", "open", "presenter", now.AddSeconds(-20)),
            Item("open-tie-z", "utilisation-reserves", "open", "presenter", now.AddSeconds(-30)),
            Item("open-tie-a", "utilisation-reserves", "open", "presenter", now.AddSeconds(-30)),
            Item("open-contract", "contract-compliance", "open", "presenter", now.AddSeconds(-12)),
            Item("closed", "records-investigation", "closed", "presenter", now),
            Item("capitalized", "records-investigation", "Open", "presenter", now),
            Item("other-owner", "records-investigation", "open", "technical-reviewer", now),
            Item("future", "unlisted-capability", "open", "presenter", now.AddSeconds(10)),
        ],
        [
            Audit(3, "technical-review.decided", """{"basisVersion":2}"""),
            Audit(1, "reserve.decided", """{"decision":"approve"}"""),
            Audit(2, "obligation.legal-decided", """{"BasisVersion":4}"""),
            Audit(4, "mock-response.accepted", """{"basisVersion":5}"""),
            Audit(5, "create-work-item", "open"),
            Audit(6, "change-work-item-state", "closed"),
            Audit(7, "unlisted.action", "{}"),
        ]));
        using var factory = CreateFactory(store);
        using var client = factory.CreateClient();
        var cookie = AddSession(factory, "presenter", ["case-a"]);

        using var response = await GetAsync(client, "/api/cases/case-a/dashboard?role=technical-reviewer&revision=999", cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(1, store.Reads);
        var queue = root.GetProperty("queue").EnumerateArray().ToArray();
        Assert.Equal(new[] { "open-old", "open-new", "open-tie-z", "open-tie-a", "open-contract", "future" },
            queue.Select(item => item.GetProperty("itemId").GetString()));
        Assert.Equal(new long[] { 150, 20, 30, 30, 12, 0 }, queue.Select(item => item.GetProperty("ageSeconds").GetInt64()));
        Assert.Equal(new[] { "AI agent", "AI agent", "Deterministic workflow", "Deterministic workflow", "Deterministic workflow", "Unknown" },
            queue.Select(item => item.GetProperty("label").GetString()));
        var projectedQueue = JsonSerializer.Deserialize<DashboardQueueItem[]>(
            root.GetProperty("queue").GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("open-old", CapabilityProjection.NextDecisionItem(
            projectedQueue.Where(item => item.CapabilityId == "records-investigation"))?.ItemId);
        Assert.Equal("open-tie-a", CapabilityProjection.NextDecisionItem(
            projectedQueue.Where(item => item.CapabilityId == "utilisation-reserves"))?.ItemId);
        Assert.All(queue, item =>
        {
            Assert.Equal("open", item.GetProperty("state").GetString());
            Assert.Equal("presenter", item.GetProperty("owner").GetString());
        });

        var capabilities = root.GetProperty("capabilities").EnumerateArray().ToDictionary(
            item => item.GetProperty("id").GetString()!, item => item);
        Assert.Equal(2, capabilities["records-investigation"].GetProperty("openItems").GetInt32());
        Assert.Equal("presenter", capabilities["records-investigation"].GetProperty("nextDecisionOwner").GetString());
        Assert.Equal(2, capabilities["utilisation-reserves"].GetProperty("openItems").GetInt32());
        Assert.Equal("presenter", capabilities["utilisation-reserves"].GetProperty("nextDecisionOwner").GetString());
        Assert.Equal(1, capabilities["contract-compliance"].GetProperty("openItems").GetInt32());
        Assert.Equal("presenter", capabilities["contract-compliance"].GetProperty("nextDecisionOwner").GetString());
        Assert.Empty(root.GetProperty("allowedDecisions").EnumerateArray());
        var activity = root.GetProperty("activity").EnumerateArray().ToArray();
        Assert.Equal(Enumerable.Range(1, 7), activity.Select(item => item.GetProperty("sequence").GetInt32()));
        Assert.Equal(
            new[] { "Human decision", "Human decision", "Human decision", "Deterministic workflow", "Deterministic workflow", "Deterministic workflow", "Unknown" },
            activity.Select(item => item.GetProperty("label").GetString()));
        Assert.Equal(Enumerable.Range(1, 7).Select(sequence => "actor-" + sequence),
            activity.Select(item => item.GetProperty("actor").GetString()));
        Assert.Equal(new[] { "reserve.decided", "obligation.legal-decided", "technical-review.decided", "mock-response.accepted", "create-work-item", "change-work-item-state", "unlisted.action" },
            activity.Select(item => item.GetProperty("outcome").GetString()));
        Assert.Equal(new int?[] { null, 4, 2, 5, null, null, null },
            activity.Select(item => item.GetProperty("basisVersion").ValueKind == JsonValueKind.Null
                ? (int?)null : item.GetProperty("basisVersion").GetInt32()));
        Assert.DoesNotContain("fleet", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("portfolioTotal", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("currency", root.GetRawText(), StringComparison.OrdinalIgnoreCase);

        store.Reads = 0;
        using var empty = await GetAsync(client, "/api/cases/case-a/dashboard", AddSession(factory, "finance-reviewer", ["case-a"]));
        using var emptyJson = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
        Assert.Empty(emptyJson.RootElement.GetProperty("queue").EnumerateArray());
        Assert.All(emptyJson.RootElement.GetProperty("capabilities").EnumerateArray()
            .Where(item => item.GetProperty("designOnly").GetBoolean() == false),
            item => Assert.Equal(JsonValueKind.Null, item.GetProperty("nextDecisionOwner").ValueKind));
    }

    [Fact]
    public async Task AC020_LabelsTracesAndDesignOnlyCardsAreAuthoritative()
    {
        var trace = new InvestigationTrace("case-a", "run-7", "investigation-9", 3, "model", "api", "region",
            "filter", "accept", "opaque-actor", "completed", []);
        var store = NewStore();
        store.Set("case-a", State("case-a", [], traces: [trace]));
        using var factory = CreateFactory(store);
        using var client = factory.CreateClient();
        var cookie = AddSession(factory, "presenter", ["case-a"]);

        using var response = await GetAsync(client, "/api/cases/case-a/dashboard", cookie);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cards = json.RootElement.GetProperty("capabilities").EnumerateArray().ToArray();
        Assert.Empty(json.RootElement.GetProperty("activity").EnumerateArray());
        Assert.Equal(new[]
        {
            "records-investigation", "utilisation-reserves", "contract-compliance",
            "transition-planning", "credit-sanctions-monitoring", "portfolio-supervisor",
        }, cards.Select(card => card.GetProperty("id").GetString()));
        Assert.Equal(new[] { "AI agent", "Deterministic workflow", "Deterministic workflow", "Design only", "Design only", "Design only" },
            cards.Select(card => card.GetProperty("label").GetString()));
        Assert.Equal(new[]
        {
            "Records investigation (records digitisation)", "Utilisation and reserves", "Contract compliance",
            "Transition planning", "Credit and sanctions monitoring", "Portfolio supervisor",
        }, cards.Select(card => card.GetProperty("name").GetString()));
        Assert.Equal(new[]
        {
            "L4, autonomous", "L3, acts (invoices) with finance review", "L3, acts (notices) with legal sign-off",
            "L2, recommend", "L2, recommend", "not in the case-study table",
        }, cards.Select(card => card.GetProperty("caseStudyAutonomy").GetString()));
        Assert.Equal(new[]
        {
            "L3: acts only on routine evidence requests within policy to the mock inbox; a person decides ambiguous cases and technical acceptance",
            "L2: drafts a reserve calculation; a simulated finance review decides; no invoice",
            "L2: drafts an obligation status and notice; a simulated legal review decides; no notice sent",
            "design-only: not implemented in this demo", "design-only: not implemented in this demo", "design-only: not implemented in this demo",
        }, cards.Select(card => card.GetProperty("demoAutonomy").GetString()));

        var evidence = cards[0].GetProperty("evidence").EnumerateArray().Single();
        Assert.Equal("/api/cases/case-a/dashboard/traces/run-7/investigation-9", evidence.GetProperty("url").GetString());
        using var traceResponse = await GetAsync(client, evidence.GetProperty("url").GetString()!, cookie);
        using var traceJson = JsonDocument.Parse(await traceResponse.Content.ReadAsStringAsync());
        Assert.Equal("case-a", traceJson.RootElement.GetProperty("caseId").GetString());
        Assert.Equal("run-7", traceJson.RootElement.GetProperty("runId").GetString());
        Assert.Equal("investigation-9", traceJson.RootElement.GetProperty("investigationId").GetString());
        using var unauthenticatedTrace = await client.GetAsync(evidence.GetProperty("url").GetString()!);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedTrace.StatusCode);
        var otherCaseCookie = AddSession(factory, "presenter", ["case-b"]);
        using var outOfScopeTrace = await GetAsync(client, evidence.GetProperty("url").GetString()!, otherCaseCookie);
        var outOfScopeBody = await outOfScopeTrace.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, outOfScopeTrace.StatusCode);
        Assert.DoesNotContain("investigation-9", outOfScopeBody);
        using var missingTrace = await GetAsync(client, "/api/cases/case-a/dashboard/traces/run-7/missing", cookie);
        Assert.Equal(HttpStatusCode.NotFound, missingTrace.StatusCode);

        var design = cards.Skip(3).ToArray();
        Assert.All(design, card =>
        {
            Assert.Equal("Not implemented in this demo", card.GetProperty("design").GetProperty("notice").GetString());
            Assert.Empty(card.GetProperty("actions").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, card.GetProperty("openItems").ValueKind);
            Assert.Equal(JsonValueKind.Null, card.GetProperty("nextDecisionOwner").ValueKind);
        });
        Assert.All(design, card => Assert.False(string.IsNullOrWhiteSpace(card.GetProperty("design").GetProperty("purpose").GetString())));
        Assert.All(design, card => Assert.False(string.IsNullOrWhiteSpace(card.GetProperty("design").GetProperty("intendedAutonomy").GetString())));
        Assert.All(design, card => Assert.False(string.IsNullOrWhiteSpace(card.GetProperty("design").GetProperty("humanAuthority").GetString())));

        using var emptyFactory = CreateFactory(NewStore());
        using var emptyClient = emptyFactory.CreateClient();
        var emptyCookie = AddSession(emptyFactory, "presenter", ["case-a"]);
        using var emptyResponse = await GetAsync(emptyClient, "/api/cases/case-a/dashboard", emptyCookie);
        using var emptyJson = JsonDocument.Parse(await emptyResponse.Content.ReadAsStringAsync());
        Assert.Empty(emptyJson.RootElement.GetProperty("capabilities")[0].GetProperty("evidence").EnumerateArray());
    }

    [Fact]
    public async Task AC022_CapabilitiesCarryCaseStudyAndDemoAutonomy()
    {
        var store = NewStore();
        store.Set("case-a", State("case-a", []));
        using var factory = CreateFactory(store);
        using var client = factory.CreateClient();
        var cookie = AddSession(factory, "presenter", ["case-a"]);

        using var response = await GetAsync(client, "/api/cases/case-a/dashboard", cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var capabilities = json.RootElement.GetProperty("capabilities").EnumerateArray().ToArray();

        Assert.Equal(new[]
        {
            ("records-investigation", "Records investigation (records digitisation)",
                "L4, autonomous",
                "L3: acts only on routine evidence requests within policy to the mock inbox; a person decides ambiguous cases and technical acceptance"),
            ("utilisation-reserves", "Utilisation and reserves",
                "L3, acts (invoices) with finance review",
                "L2: drafts a reserve calculation; a simulated finance review decides; no invoice"),
            ("contract-compliance", "Contract compliance",
                "L3, acts (notices) with legal sign-off",
                "L2: drafts an obligation status and notice; a simulated legal review decides; no notice sent"),
            ("transition-planning", "Transition planning",
                "L2, recommend", "design-only: not implemented in this demo"),
            ("credit-sanctions-monitoring", "Credit and sanctions monitoring",
                "L2, recommend", "design-only: not implemented in this demo"),
            ("portfolio-supervisor", "Portfolio supervisor",
                "not in the case-study table", "design-only: not implemented in this demo"),
        }, capabilities.Select(capability =>
            (capability.GetProperty("id").GetString(),
                capability.GetProperty("name").GetString(),
                capability.GetProperty("caseStudyAutonomy").GetString(),
                capability.GetProperty("demoAutonomy").GetString())));
    }

    [Theory]
    [InlineData("technical-reviewer", "accept,reject")]
    [InlineData("presenter", "")]
    [InlineData("finance-reviewer", "")]
    [InlineData("legal-reviewer", "")]
    [InlineData("unknown", "")]
    [InlineData(null, "")]
    public void AC020_DecisionsMatchServerRole(string? role, string expected)
    {
        Assert.Equal(expected.Length == 0 ? Array.Empty<string>() : expected.Split(','),
            CapabilityProjection.PermittedDecisions(role).Select(decision => decision.Id));
    }

    [Fact]
    public async Task M02_DashboardProjectionUsesServerSessionAndCaseScope()
    {
        var store = NewStore();
        store.Set("case-a", State("case-a", [Item("SENTINEL-A", "records-investigation", "open", "presenter", Now)]));
        store.Set("case-b", State("case-b", [Item("SENTINEL-B", "records-investigation", "open", "presenter", Now, "case-b")]));
        using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        using var unauthenticated = await client.GetAsync("/api/cases/case-a/dashboard");
        var unauthenticatedBody = await unauthenticated.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.DoesNotContain("SENTINEL-A", unauthenticatedBody);
        Assert.DoesNotContain("SENTINEL-B", unauthenticatedBody);

        var cookie = AddSession(factory, "presenter", ["case-a"]);
        using var allowed = await GetAsync(client, "/api/cases/case-a/dashboard?role=technical-reviewer&revision=999", cookie);
        using var allowedJson = JsonDocument.Parse(await allowed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("SENTINEL-A", allowedJson.RootElement.GetProperty("queue")[0].GetProperty("itemId").GetString());
        Assert.Empty(allowedJson.RootElement.GetProperty("allowedDecisions").EnumerateArray());

        using var denied = await GetAsync(client, "/api/cases/case-b/dashboard", cookie);
        var deniedBody = await denied.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.DoesNotContain("SENTINEL-A", deniedBody);
        Assert.DoesNotContain("SENTINEL-B", deniedBody);
    }

    private static ProjectionStore NewStore() => new();

    private static WebApplicationFactory<Program> CreateFactory(ProjectionStore store) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CaseState:Path"] = Path.Combine(Path.GetTempPath(), $"dashboard-{Guid.NewGuid():N}.json"),
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICaseStateStore>();
                services.AddSingleton<ICaseStateStore>(store);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            });
        });

    private static string AddSession(WebApplicationFactory<Program> factory, string role, IReadOnlyList<string> scopes)
    {
        var session = factory.Services.GetRequiredService<DemoSessionStore>()
            .Create(new DemoUser("test-user", "Test user", role, scopes));
        return $"{DemoAuthEndpoints.CookieName}={session.Id}";
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    private static CaseState State(
        string caseId,
        IReadOnlyList<WorkItem> items,
        IReadOnlyList<AuditEvent>? audit = null,
        IReadOnlyList<InvestigationTrace>? traces = null) =>
        new(caseId, "owner", 0, [], [], audit ?? [], new Dictionary<string, string>(), items, traces);

    private static WorkItem Item(string id, string capability, string state, string owner, DateTimeOffset createdAt, string caseId = "case-a") =>
        new(caseId, id, capability, state, owner, createdAt);

    private static AuditEvent Audit(int sequence, string action, string detail) =>
        new(sequence, sequence, "actor-" + sequence, action, detail, Now);

    private sealed class ProjectionStore : ICaseStateStore
    {
        private readonly Dictionary<string, CaseState> _states = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal)
        {
            ["case-a"] = "owner",
            ["case-b"] = "owner",
        };

        public int Reads { get; set; }

        public ProjectionStore()
        {
            _states["case-a"] = State("case-a", []);
            _states["case-b"] = State("case-b", []);
        }

        public void Set(string caseId, CaseState state) => _states[caseId] = state;
        public string? OwnerOf(string caseId) => _owners.GetValueOrDefault(caseId);
        public CaseScope AuthorizeCase(string caseId, string owner) =>
            OwnerOf(caseId) == owner ? new CaseScope(caseId) : throw new CaseAccessDeniedException();
        public CaseState Read(CaseScope scope)
        {
            Reads++;
            return _states[scope.CaseId];
        }
        public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId) =>
            scope.CaseId == caseId
                ? _states[caseId].Traces?.SingleOrDefault(trace => trace.RunId == runId && trace.InvestigationId == investigationId)
                : null;

        public CaseScope CreateCase(string caseId, string owner) => throw new NotSupportedException();
        public BasisVersion GetBasisVersion(CaseScope scope, int version) => throw new NotSupportedException();
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => throw new NotSupportedException();
        public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit) => throw new NotSupportedException();
        public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope) => throw new NotSupportedException();
        public CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor) => throw new NotSupportedException();
        public CommitResult ChangeWorkItemState(CaseScope scope, long expectedRevision, string itemId, string state, string actor) => throw new NotSupportedException();
        public void AppendTrace(CaseScope scope, InvestigationTrace trace) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
