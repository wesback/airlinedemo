using System.Net;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
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
    private WebApplicationFactory<Program> CreateHost(CountingCaseStateStore store) =>
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
        using var client = host.CreateClient();
        var snapshot = host.Services.GetRequiredService<IAircraftCasePackSnapshotStore>().Read();
        Assert.NotNull(snapshot);

        using var allResponse = await SendAsync(client, HttpMethod.Get, "/api/v1/cases",
            CreateSession(host, "presenter", CaseIds));
        Assert.Equal(HttpStatusCode.OK, allResponse.StatusCode);
        using var allJson = await ReadJsonAsync(allResponse);
        AssertSummaryEnvelope(allJson.RootElement, snapshot.PackVersion, CaseIds);

        using var oneResponse = await SendAsync(client, HttpMethod.Get, "/api/v1/cases",
            CreateSession(host, "presenter", "cp-case-a"));
        Assert.Equal(HttpStatusCode.OK, oneResponse.StatusCode);
        using var oneJson = await ReadJsonAsync(oneResponse);
        AssertSummaryEnvelope(oneJson.RootElement, snapshot.PackVersion, ["cp-case-a"]);
        Assert.DoesNotContain("cp-case-b", oneJson.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("cp-case-c", oneJson.RootElement.GetRawText(), StringComparison.Ordinal);
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
