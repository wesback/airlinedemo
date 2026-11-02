using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Finance;
using AirlineDemo.Api.Obligations;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.SourceSystems;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public class CountingCaseStore : DispatchProxy
{
    public ICaseStateStore Inner { get; set; } = null!;
    public int Reads;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == nameof(ICaseStateStore.Read)) Interlocked.Increment(ref Reads);
        try { return targetMethod.Invoke(Inner, args); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null) { throw exception.InnerException; }
    }
}

public sealed class CopilotProjectionTests : IAsyncDisposable
{
    private static readonly string[] Roles = ["technical-reviewer", "finance-reviewer", "legal-reviewer", "presenter", "executive"];
    private static readonly string[] CpCases = ["cp-case-a", "cp-case-b", "cp-case-c"];
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"copilot-{Guid.NewGuid():N}.json");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private CountingCaseStore? _counter;

    public CopilotProjectionTests()
    {
        var path = _path;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Passcode"] = "test-passcode",
                ["CaseState:Path"] = path,
                ["Demo:SeedCases"] = "true",
                ["Demo:SeedJourney"] = "true",
            }));
            builder.ConfigureTestServices(services =>
            {
                var descriptor = services.Last(item => item.ServiceType == typeof(ICaseStateStore));
                services.Remove(descriptor);
                services.AddSingleton<ICaseStateStore>(provider =>
                {
                    var inner = (ICaseStateStore)(descriptor.ImplementationInstance
                        ?? descriptor.ImplementationFactory?.Invoke(provider)
                        ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!));
                    var proxy = DispatchProxy.Create<ICaseStateStore, CountingCaseStore>();
                    _counter = (CountingCaseStore)(object)proxy;
                    _counter.Inner = inner;
                    return proxy;
                });
            });
        });
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        File.Delete(_path);
    }

    private DemoSession Session(string role, params string[] cases) =>
        _factory.Services.GetRequiredService<DemoSessionStore>().Create(new DemoUser($"copilot-{role}", "Test", role, cases));

    private async Task<(HttpStatusCode Status, string Body)> Get(string path, DemoSession? session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (session is not null) request.Headers.Add("Cookie", $"__Host-demo-session={session.Id}");
        using var response = await _client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonNode> Ok(string path, DemoSession session)
    {
        var (status, body) = await Get(path, session);
        Assert.True(status == HttpStatusCode.OK, $"{path}: {(int)status} {body}");
        return JsonNode.Parse(body)!;
    }

    private static string[] Keys(JsonNode node) => [.. node.AsObject().Select(item => item.Key).Order(StringComparer.Ordinal)];

    private (ICaseStateStore Store, CaseScope Scope) Scope(string caseId)
    {
        var store = _factory.Services.GetRequiredService<ICaseStateStore>();
        return (store, store.AuthorizeCase(caseId, store.OwnerOf(caseId)!));
    }

    private void Commit(string caseId, string action, string detail, string? basisContent = null)
    {
        var (store, scope) = Scope(caseId);
        var state = store.Read(scope);
        var basis = basisContent is null ? null : CaseStateStore.CreateBasisVersion(state.Basis.Count + 1, basisContent, state.Basis[^1].BasisDigest);
        store.Commit(scope, state.Revision, new CaseCommit(basis, null, "presenter", action, detail));
    }

    private static JsonNode Expected(string caseId, long revision, string domain, string state, string role, params (string Pt, string Id)[] points) =>
        JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            contractVersion = 1, caseId, caseRevision = revision, domain,
            points = points.Select(point => new { pointId = point.Id, domain, pointType = point.Pt, state, ownerRole = role, sourceRefs = Array.Empty<object>() }),
        }))!;

    [Fact]
    public async Task ProductionHost_CaseSummary_CanonicalProjection()
    {
        string[] summaryKeys = ["aircraftId", "capabilityLabel", "caseId", "caseRevision", "contractVersion", "nextActionId", "ownerRole",
            "releaseBlockers", "released", "simulated", "status", "synthetic"];
        foreach (var role in Roles)
        {
            var session = Session(role, CpCases);
            var listed = (await Ok("/api/v1/cases", session))["cases"]!.AsArray();
            foreach (var caseId in CpCases)
            {
                var summary = await Ok($"/api/v1/cases/{caseId}/copilot/summary", session);
                var canonical = listed.Single(item => (string?)item!["caseId"] == caseId)!;
                Assert.Equal(summaryKeys, Keys(summary));
                Assert.Equal(1, (int)summary["contractVersion"]!);
                Assert.Equal(caseId, (string?)summary["caseId"]);
                Assert.Equal($"SYN-{caseId.Replace("case-", "", StringComparison.Ordinal).ToUpperInvariant()}-AIR-001", (string?)summary["aircraftId"]);
                Assert.Equal((long)canonical["caseRevision"]!, (long)summary["caseRevision"]!);
                Assert.True(JsonNode.DeepEquals(canonical["status"], summary["status"]), $"{role} {caseId} status");
                Assert.Equal((string?)canonical["owner"], (string?)summary["ownerRole"]);
                Assert.Equal((string?)canonical["nextAction"]?["actionId"], (string?)summary["nextActionId"]);
                Assert.Equal((string?)canonical["capabilityLabel"], (string?)summary["capabilityLabel"]);
                Assert.True((bool)summary["simulated"]! && (bool)summary["synthetic"]!);
            }
        }

        var reviewer = Session("technical-reviewer", CpCases);
        Assert.Equal("review-cp-case-c", (string?)(await Ok("/api/v1/cases/cp-case-c/copilot/summary", reviewer))["nextActionId"]);
        var caseB = await Ok("/api/v1/cases/cp-case-b/copilot/summary", reviewer);
        Assert.False((bool)caseB["released"]!);
        Assert.Equal(["reserve_not_approved", "obligations_not_signed_off"], caseB["releaseBlockers"]!.AsArray().Select(item => (string)item!));

        Commit("cp-case-b", FinanceEndpoints.ReserveDecisionAction, """{"decision":"bogus","reason":"test"}""");
        foreach (var path in new[] { "summary", "open-points" })
        {
            var (status, body) = await Get($"/api/v1/cases/cp-case-b/copilot/{path}", reviewer);
            Assert.Equal(HttpStatusCode.BadGateway, status);
            Assert.Equal("""{"contractVersion":1,"error":{"code":"copilot_context_invalid"}}""", body);
        }
    }

    [Fact]
    public async Task ProductionHost_OpenPoints_RoleDomainAndCurrentBasis()
    {
        async Task<JsonNode> Points(string role, string caseId) => await Ok($"/api/v1/cases/{caseId}/copilot/open-points", Session(role, CpCases));
        long Revision(string caseId) { var (store, scope) = Scope(caseId); return store.Read(scope).Revision; }

        // cp-case-c: nothing decided, every domain has its own open point.
        var c = Revision("cp-case-c");
        Assert.True(JsonNode.DeepEquals(Expected("cp-case-c", c, "technical", "open", "technical-reviewer", ("technical_requirement", "REQ-1")), await Points("technical-reviewer", "cp-case-c")));
        Assert.True(JsonNode.DeepEquals(Expected("cp-case-c", c, "finance", "open", "finance-reviewer", ("reserve_review", "RSV-cp-case-c")), await Points("finance-reviewer", "cp-case-c")));
        Assert.True(JsonNode.DeepEquals(Expected("cp-case-c", c, "legal", "open", "legal-reviewer", ("obligation_review", "OBL-1")), await Points("legal-reviewer", "cp-case-c")));

        // cp-case-a: technical accepted and reserve approved on the current basis, legal still pending.
        var a = Revision("cp-case-a");
        Assert.True(JsonNode.DeepEquals(Expected("cp-case-a", a, "technical", "open", "technical-reviewer"), await Points("technical-reviewer", "cp-case-a")));
        Assert.True(JsonNode.DeepEquals(Expected("cp-case-a", a, "finance", "open", "finance-reviewer"), await Points("finance-reviewer", "cp-case-a")));
        Assert.True(JsonNode.DeepEquals(Expected("cp-case-a", a, "legal", "open", "legal-reviewer", ("obligation_review", "OBL-1")), await Points("legal-reviewer", "cp-case-a")));

        // A new basis makes the earlier decisions stale: points reopen and carry only the role's entitled source records.
        Commit("cp-case-a", "test.basis", "{}", """
            {"format":"source-record-pins-v1","pins":[
            {"sourceId":"technical","recordId":"T-1","recordVersion":2},{"sourceId":"technical","recordId":"T-1","recordVersion":2},
            {"sourceId":"maintenance","recordId":"M-1","recordVersion":1},{"sourceId":"finance","recordId":"F-1","recordVersion":3},
            {"sourceId":"records","recordId":"R-1","recordVersion":1},{"sourceId":"contracts","recordId":"C-1","recordVersion":1}]}
            """);
        var technical = await Points("technical-reviewer", "cp-case-a");
        Assert.Equal("REQ-1", (string?)technical["points"]![0]!["pointId"]);
        Assert.Equal("""[{"sourceId":"maintenance","recordId":"M-1","recordVersion":1},{"sourceId":"records","recordId":"R-1","recordVersion":1},{"sourceId":"technical","recordId":"T-1","recordVersion":2}]""",
            technical["points"]![0]!["sourceRefs"]!.ToJsonString());
        var finance = await Points("finance-reviewer", "cp-case-a");
        Assert.Equal("RSV-cp-case-a", (string?)finance["points"]![0]!["pointId"]);
        Assert.Equal("""[{"sourceId":"finance","recordId":"F-1","recordVersion":3},{"sourceId":"technical","recordId":"T-1","recordVersion":2}]""",
            finance["points"]![0]!["sourceRefs"]!.ToJsonString());
        Assert.Equal("""[{"sourceId":"contracts","recordId":"C-1","recordVersion":1},{"sourceId":"records","recordId":"R-1","recordVersion":1}]""",
            (await Points("legal-reviewer", "cp-case-a"))["points"]![0]!["sourceRefs"]!.ToJsonString());

        var (store, scope) = Scope("cp-case-a");
        Assert.Equal(SourceModeOutcome.Recorded, _factory.Services.GetRequiredService<SourceModeStore>()
            .Set(scope, "presenter", "copilot-mode-1", store.Read(scope).Revision, "technical", "unavailable").Outcome);
        Assert.Equal("blocked", (string?)(await Points("technical-reviewer", "cp-case-a"))["points"]![0]!["state"]);
        Assert.Equal("open", (string?)(await Points("legal-reviewer", "cp-case-a"))["points"]![0]!["state"]);
    }

    [Fact]
    public async Task ProductionHost_OpenPoints_DenialNoPorts()
    {
        var reads = _counter!.Reads;
        foreach (var path in new[] { "summary", "open-points" })
        {
            var anonymous = await Get($"/api/v1/cases/cp-case-a/copilot/{path}", null);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
            var unknown = await Get($"/api/v1/cases/cp-case-a/copilot/{path}", Session("auditor", CpCases));
            Assert.Equal(HttpStatusCode.Forbidden, unknown.Status);
            Assert.Equal("""{"contractVersion":1,"error":{"code":"forbidden"}}""", unknown.Body);
            foreach (var (caseId, scopes) in new[] { ("cp-case-a", new[] { "cp-case-b" }), ("cp-case-z", new[] { "cp-case-z" }), ("case-001", new[] { "case-001" }) })
            {
                var missing = await Get($"/api/v1/cases/{caseId}/copilot/{path}", Session("technical-reviewer", scopes));
                Assert.Equal(HttpStatusCode.NotFound, missing.Status);
                Assert.Equal("", missing.Body);
            }
        }
        Assert.Equal(reads, _counter.Reads);

        foreach (var role in new[] { "presenter", "executive" })
        {
            var envelope = await Ok("/api/v1/cases/cp-case-c/copilot/open-points", Session(role, CpCases));
            Assert.Equal(["caseId", "caseRevision", "contractVersion", "status"], Keys(envelope));
        }
    }

    [Fact]
    public async Task ProductionHost_OpenPoints_DenialNoPorts_PointLimit()
    {
        var inputs = _factory.Services.GetRequiredService<FinanceInputStore>();
        var original = inputs.Find("cp-case-c")!;
        var session = Session("technical-reviewer", CpCases);
        void Requirements(int count) => inputs.Set("cp-case-c", original with
        {
            Obligation = original.Obligation! with
            {
                Requirements = [.. Enumerable.Range(0, count).Select(index => new LinkedRequirement($"REQ-{index:D3}", RequirementEvidence.Satisfied, 1))],
            },
        });

        Requirements(100);
        var points = (await Ok("/api/v1/cases/cp-case-c/copilot/open-points", session))["points"]!.AsArray();
        Assert.Equal(100, points.Count);
        Assert.Equal("REQ-000", (string?)points[0]!["pointId"]);
        Assert.Equal("REQ-099", (string?)points[99]!["pointId"]);

        Requirements(101);
        var (status, body) = await Get("/api/v1/cases/cp-case-c/copilot/open-points", session);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("""{"contractVersion":1,"error":{"code":"copilot_point_limit"}}""", body);
    }
}
