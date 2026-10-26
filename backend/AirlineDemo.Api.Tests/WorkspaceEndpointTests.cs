using System.Net;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirlineDemo.Api.Tests;

public sealed class WorkspaceEndpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Worklist_returns_scoped_role_owned_items_in_contract_order()
    {
        var store = new WorkspaceStore();
        store.Set("case-a", State("case-a", [Item("old", "records-investigation", "technical-reviewer", -100, "case-a") , Item("other", "records-investigation", "finance-reviewer", -200, "case-a") , Item("z", "utilisation-reserves", "technical-reviewer", -50, "case-a")]));
        store.Set("case-b", State("case-b", [Item("b-item", "contract-compliance", "technical-reviewer", -50, "case-b") , Item("a-item", "contract-compliance", "technical-reviewer", -50, "case-b") ]));
        store.Set("case-out", State("case-out", [Item("out", "records-investigation", "technical-reviewer", -500, "case-out") ]));
        using var factory = CreateFactory(store);
        using var client = factory.CreateClient();
        var cookie = AddSession(factory, "technical-reviewer", ["case-a", "case-b", "case-missing"]);

        using var response = await GetAsync(client, "/api/v1/workspace/worklist", cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal(new[] { "old", "z", "a-item", "b-item" }, root.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("itemId").GetString()));
        Assert.Equal(1, root.GetProperty("contractVersion").GetInt32());
        Assert.Equal(new[] { "case-missing" }, root.GetProperty("unavailableCaseIds").EnumerateArray().Select(x => x.GetString()));
        Assert.False(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Worklist_contract_shape_is_exact()
    {
        var store = new WorkspaceStore();
        store.Set("case-a", State("case-a", [Item("item-a", "records-investigation", "presenter", -10, "case-a") ]));
        using var factory = CreateFactory(store);
        using var client = factory.CreateClient();
        var cookie = AddSession(factory, "presenter", ["case-a"]);
        using var response = await GetAsync(client, "/api/v1/workspace/worklist", cookie);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.True(root.EnumerateObject().Select(x => x.Name).ToHashSet().SetEquals(new[] { "contractVersion", "items", "unavailableCaseIds", "truncated" }));
        var item = root.GetProperty("items")[0];
        Assert.True(item.EnumerateObject().Select(x => x.Name).ToHashSet().SetEquals(new[] { "caseId", "itemId", "capabilityId", "state", "owner", "ageSeconds", "label" }));
    }

    [Fact]
    public async Task Worklist_limits_filtered_items_after_scope()
    {
        var store = new WorkspaceStore();
        var items201 = Enumerable.Range(1, 201).Select(i => Item($"item-{i:000}", "records-investigation", "technical-reviewer", -i, "case-a")).ToArray();
        store.Set("case-a", State("case-a", items201));
        using var factory = CreateFactory(store);
        using var client = factory.CreateClient();
        var cookie = AddSession(factory, "technical-reviewer", ["case-a", "case-missing"]);
        using var response = await GetAsync(client, "/api/v1/workspace/worklist", cookie);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(200, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(new[] { "case-missing" }, json.RootElement.GetProperty("unavailableCaseIds").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Worklist_rejects_forbidden_versions_and_roles()
    {
        var store = new WorkspaceStore();
        store.Set("case-a", State("case-a", []));
        using var factory = CreateFactory(store);
        using var client = factory.CreateClient();
        using var unauthenticated = await client.GetAsync("/api/v1/workspace/worklist");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        var executive = AddSession(factory, "executive", ["case-a"]);
        using var forbidden = await GetAsync(client, "/api/v1/workspace/worklist", executive);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("{\"error\":\"Role not permitted.\"}", await forbidden.Content.ReadAsStringAsync());
        using var v2 = await GetAsync(client, "/api/v2/workspace/worklist", executive);
        Assert.Equal(HttpStatusCode.NotFound, v2.StatusCode);
    }

    [Fact]
    public async Task Worklist_five_case_capacity_stays_within_budget()
    {
        var store = new WorkspaceStore();
        for (var c = 1; c <= 5; c++)
        {
            var caseId = $"case-{c}";
            store.Set(caseId, State(caseId, Enumerable.Range(1, 200).Select(i =>
                Item($"item-{c}-{i}", "records-investigation", "technical-reviewer", -i, caseId)).ToArray()));
        }
        using var factory = CreateFactory(store);
        using var client = factory.CreateClient();
        var cookie = AddSession(factory, "technical-reviewer", Enumerable.Range(1, 5).Select(c => $"case-{c}").ToArray());
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await GetAsync(client, "/api/v1/workspace/worklist", cookie);
        stopwatch.Stop();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(stopwatch.ElapsedMilliseconds < 500);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(200, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
    }

    private static WebApplicationFactory<Program> CreateFactory(WorkspaceStore store) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICaseStateStore>();
            services.AddSingleton<ICaseStateStore>(store);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        }));

    private static string AddSession(WebApplicationFactory<Program> factory, string role, IReadOnlyList<string> scopes)
    {
        var session = factory.Services.GetRequiredService<DemoSessionStore>().Create(new DemoUser("test-user", "Test user", role, scopes));
        return $"{DemoAuthEndpoints.CookieName}={session.Id}";
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    private static CaseState State(string caseId, IReadOnlyList<WorkItem> items) => new(caseId, "owner", 0, [], [], [], new Dictionary<string, string>(), items);
    private static WorkItem Item(string id, string capability, string owner, int ageSeconds, string caseId) =>
        new(caseId, id, capability, "open", owner, Now.AddSeconds(ageSeconds));

    private sealed class WorkspaceStore : ICaseStateStore
    {
        private readonly Dictionary<string, CaseState> _states = new(StringComparer.Ordinal);
        public int Reads { get; private set; }
        public void Set(string caseId, CaseState state) => _states[caseId] = state;
        public string? OwnerOf(string caseId) => _states.TryGetValue(caseId, out var state) ? state.Owner : null;
        public CaseScope AuthorizeCase(string caseId, string owner) => OwnerOf(caseId) == owner ? new CaseScope(caseId) : throw new CaseAccessDeniedException();
        public CaseState Read(CaseScope scope) { Reads++; return _states[scope.CaseId]; }
        public CaseScope CreateCase(string caseId, string owner) => throw new NotSupportedException();
        public BasisVersion GetBasisVersion(CaseScope scope, int version) => throw new NotSupportedException();
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => throw new NotSupportedException();
        public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit) => throw new NotSupportedException();
        public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope) => _states[scope.CaseId].WorkItems ?? [];
        public CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor) => throw new NotSupportedException();
        public CommitResult ChangeWorkItemState(CaseScope scope, long expectedRevision, string itemId, string state, string actor) => throw new NotSupportedException();
        public void AppendTrace(CaseScope scope, InvestigationTrace trace) => throw new NotSupportedException();
        public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId) => null;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
