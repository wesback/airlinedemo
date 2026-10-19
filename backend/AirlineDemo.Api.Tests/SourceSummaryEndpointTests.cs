using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.SourceSystems;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirlineDemo.Api.Tests;

public sealed class SourceSummaryEndpointTests
{
    private sealed record ExtraPayload;
    private sealed class FakeSource<T>(IReadOnlyList<SourceRecord<T>> records) : ISourceSystem<T> where T : class
    {
        public Task<SourceReadResult<IReadOnlyList<SourceRecord<T>>>> ListAsync(string caseId, CancellationToken cancellationToken = default, int? maxRecords = null) =>
            Task.FromResult<SourceReadResult<IReadOnlyList<SourceRecord<T>>>>(new SourceReadSucceeded<IReadOnlyList<SourceRecord<T>>>(records));
        public Task<SourceReadResult<SourceRecord<T>>> GetAsync(string caseId, string recordId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static readonly string[] Sources = ["technical", "maintenance", "finance", "records", "contracts"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static WebApplicationFactory<Program> Host(string path, ISourceSystem<TechnicalPayload>? technical = null, ISourceSystem<RecordsPayload>? records = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            { ["CaseState:Path"] = path, ["Demo:Passcode"] = "test-passcode", ["Persistence:EvidenceProvider"] = "Memory" }));
            builder.ConfigureTestServices(s =>
            {
                if (technical is not null) { s.RemoveAll<ISourceSystem<TechnicalPayload>>(); s.AddSingleton(technical); }
                if (records is not null) { s.RemoveAll<ISourceSystem<RecordsPayload>>(); s.AddSingleton(records); }
                s.AddSingleton<ISourceSystem<ExtraPayload>>(new FakeSource<ExtraPayload>([]));
            });
        });

    private static SourceRecord<TechnicalPayload> TechnicalRecord(string caseId) => new()
    {
        SourceId = "technical", RecordId = "technical-record", RecordVersion = 1, AsOf = new(2026, 10, 1), CaseId = caseId,
        Kind = "test", Payload = new TechnicalPayload(new Dictionary<string, string>()),
    };

    private static SourceRecord<RecordsPayload> PersonalRecord(string caseId) => new()
    {
        SourceId = "records", RecordId = "records-record", RecordVersion = 1, AsOf = new(2026, 10, 1), CaseId = caseId,
        Kind = "document", Payload = new RecordsPayload(new Dictionary<string, string>
        {
            ["documentId"] = "SYN-DOC-01", ["documentVersion"] = "1", ["containsPersonalData"] = "true",
            ["subjectName"] = "SYN-PERSON-NAME-01", ["licenseNumber"] = "SYN-LICENSE-NUMBER-01", ["freeText"] = "SYN-FREE-FORM-TEXT-01",
        }),
    };

    private static void EnsureCases(WebApplicationFactory<Program> host, params string[] ids)
    {
        var cases = host.Services.GetRequiredService<ICaseStateStore>();
        foreach (var id in ids) if (cases.OwnerOf(id) is null) cases.CreateCase(id, "test-owner");
    }

    private static DemoSession Session(WebApplicationFactory<Program> host, string role, params string[] cases) =>
        host.Services.GetRequiredService<DemoSessionStore>().Create(new DemoUser("summary-reader", "Test", role, cases));

    private static async Task<(HttpStatusCode Status, byte[] Body)> Get(HttpClient client, string caseId, DemoSession? session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/cases/{caseId}/sources");
        if (session is not null) request.Headers.Add("Cookie", $"__Host-demo-session={session.Id}");
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }

    private static async Task<JsonNode> Summary(HttpClient client, string caseId, DemoSession session)
    {
        var response = await Get(client, caseId, session);
        Assert.Equal(HttpStatusCode.OK, response.Status);
        return JsonNode.Parse(response.Body)!;
    }

    private static string[] Keys(JsonNode value) => value.AsObject().Select(item => item.Key).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task SummaryReturnsExactVersionedContractForFiveSources()
    {
        const string id = "case-001";
        await using var host = Host(Path.Combine(Path.GetTempPath(), $"summary-{Guid.NewGuid():N}.json"));
        EnsureCases(host, id);
        using var client = host.CreateClient();
        var response = await Summary(client, id, Session(host, "technical-reviewer", id));
        Assert.Equal(new[] { "caseId", "caseRevision", "contractVersion", "fixtureVersion", "sources" }, Keys(response));
        Assert.Equal((1, id, 0L, SourceFixtureManifest.SupportedSourceFixtureVersion),
            ((int)response["contractVersion"]!, (string)response["caseId"]!, (long)response["caseRevision"]!, (string)response["fixtureVersion"]!));
        var statuses = response["sources"]!.AsArray();
        Assert.Equal(Sources, statuses.Select(item => (string)item!["sourceId"]!).ToArray());
        foreach (var status in statuses)
        {
            var detailed = new[] { "technical", "maintenance", "records" }.Contains((string)status!["sourceId"]!);
            Assert.Equal(detailed
                ? new[] { "capabilityLabel", "coverage", "mode", "pinnedInBasis", "recordCount", "recordsInUse", "sourceId" }
                : new[] { "capabilityLabel", "coverage", "mode", "sourceId" }, Keys(status!));
            Assert.Equal("Simulated", (string?)status["capabilityLabel"]);
            Assert.True((bool)status["coverage"]!);
        }
        Assert.DoesNotContain("extra", statuses.Select(item => (string)item!["sourceId"]!));
    }

    [Fact]
    public async Task CoverageUsesExactModeAndCaseTruthTable()
    {
        var modes = SourceModes.Modes;
        var recordCases = modes.SelectMany(mode => new[] { $"{mode}-record", $"{mode}-other-only" }).ToArray();
        var allCases = recordCases.Concat(modes.Select(mode => $"{mode}-empty")).ToArray();
        var fake = new FakeSource<TechnicalPayload>(recordCases
            .Where(id => !id.EndsWith("other-only", StringComparison.Ordinal)).Select(TechnicalRecord)
            .Append(TechnicalRecord("outside-request")).ToArray());
        await using var host = Host(Path.Combine(Path.GetTempPath(), $"coverage-{Guid.NewGuid():N}.json"), fake);
        EnsureCases(host, allCases);
        using var client = host.CreateClient();
        var store = host.Services.GetRequiredService<ICaseStateStore>();
        var modeStore = host.Services.GetRequiredService<SourceModeStore>();
        foreach (var mode in modes)
        foreach (var (suffix, count) in new[] { ("record", true), ("other-only", false), ("empty", false) })
        {
            var id = $"{mode}-{suffix}";
            if (mode != "available")
            {
                var scope = store.AuthorizeCase(id, "test-owner");
                Assert.Equal(SourceModeOutcome.Recorded, modeStore.Set(scope, "test", $"mode-{id}",
                    store.Read(scope).Revision, "technical", mode).Outcome);
            }
            var status = (await Summary(client, id, Session(host, "finance-reviewer", allCases)))["sources"]!.AsArray()
                .Single(item => (string)item!["sourceId"]! == "technical")!;
            Assert.Equal(mode == "available" && count, (bool)status["coverage"]!);
            Assert.Equal(mode == "conflicting" && count ? 2 : mode == "unavailable" || !count ? 0 : 1,
                (int)status["recordCount"]!);
        }
    }

    [Fact]
    public async Task SummaryEnforcesRoleAndCaseVisibilityMatrix()
    {
        const string id = "case-001", other = "case-a";
        await using var host = Host(Path.Combine(Path.GetTempPath(), $"roles-{Guid.NewGuid():N}.json"));
        EnsureCases(host, id, other);
        using var client = host.CreateClient();
        var roles = new (string Role, string[] Detailed)[]
        {
            ("technical-reviewer", ["technical", "maintenance", "records"]),
            ("finance-reviewer", ["technical", "finance"]), ("legal-reviewer", ["contracts", "records"]),
            ("presenter", []), ("executive", []),
        };
        foreach (var (role, detailed) in roles)
        {
            var response = await Summary(client, id, Session(host, role, id));
            foreach (var status in response["sources"]!.AsArray())
            {
                var hasDetails = detailed.Contains((string)status!["sourceId"]!);
                Assert.Equal(hasDetails
                    ? new[] { "capabilityLabel", "coverage", "mode", "pinnedInBasis", "recordCount", "recordsInUse", "sourceId" }
                    : new[] { "capabilityLabel", "coverage", "mode", "sourceId" }, Keys(status!));
                Assert.DoesNotContain("payload", status!.ToJsonString(), StringComparison.Ordinal);
            }
        }
        var denied = await Get(client, id, Session(host, "unknown", id));
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("""{"contractVersion":1,"error":{"code":"forbidden"}}""", Encoding.UTF8.GetString(denied.Body));
        var session = Session(host, "technical-reviewer", id);
        var missing = await Get(client, "missing", session);
        var outside = await Get(client, other, session);
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
        Assert.Empty(missing.Body);
        Assert.Equal(missing.Status, outside.Status);
        Assert.Equal(missing.Body, outside.Body);
    }

    [Fact]
    public async Task SummaryEnforcesPayloadAndLatencyLimitsWithoutDisclosure()
    {
        string[] cases = ["case-001", "case-a", "case-b", "case-c"];
        await using var host = Host(Path.Combine(Path.GetTempPath(), $"payload-{Guid.NewGuid():N}.json"),
            records: new FakeSource<RecordsPayload>(cases.Select(PersonalRecord).ToArray()));
        EnsureCases(host, cases);
        using var client = host.CreateClient();
        var session = Session(host, "technical-reviewer", cases);
        var docs = host.Services.GetRequiredService<IEvidenceBlobStore>();
        var document = Encoding.UTF8.GetBytes("""{"subjectName":"SYN-PERSON-NAME-01","licenseNumber":"SYN-LICENSE-NUMBER-01"}""");
        var stored = await docs.StoreAsync("SYN-DOC-01", document);
        Assert.Equal(document, await docs.ReadAsync(stored.Name, stored.Sha256));
        var times = new List<TimeSpan>();
        for (var i = 0; i < 100; i++)
        {
            var start = Stopwatch.GetTimestamp();
            var body = await Get(client, cases[i % cases.Length], session);
            times.Add(Stopwatch.GetElapsedTime(start));
            Assert.Equal(HttpStatusCode.OK, body.Status);
            foreach (var text in new[] { "subjectName", "licenseNumber", "SYN-PERSON-NAME-01", "SYN-LICENSE-NUMBER-01", "SYN-FREE-FORM-TEXT-01" })
                Assert.DoesNotContain(text, Encoding.UTF8.GetString(body.Body), StringComparison.Ordinal);
        }
        times.Sort();
        Assert.True(times[94] <= TimeSpan.FromSeconds(1));
        Pin(host, cases[0], "x");
        var baseline = await Get(client, cases[0], session);
        var id = new string('x', 1 + 65_536 - baseline.Body.Length);
        Pin(host, cases[0], id);
        var atLimit = await Get(client, cases[0], session);
        Assert.Equal((HttpStatusCode.OK, 65_536), (atLimit.Status, atLimit.Body.Length));
        var status = JsonNode.Parse(atLimit.Body)!["sources"]!.AsArray().Single(item => (string)item!["sourceId"]! == "technical")!;
        Assert.True((bool)status["pinnedInBasis"]!);
        var pin = Assert.Single(status["recordsInUse"]!.AsArray())!;
        Assert.Equal(new[] { "recordId", "recordVersion", "sourceId" }, Keys(pin));
        Assert.Equal((id, 1), ((string)pin["recordId"]!, (int)pin["recordVersion"]!));
        Pin(host, cases[0], id + "x");
        var over = await Get(client, cases[0], session);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.Status);
        Assert.Equal("""{"contractVersion":1,"error":{"code":"source_payload_limit"}}""", Encoding.UTF8.GetString(over.Body));
    }

    private static void Pin(WebApplicationFactory<Program> host, string caseId, string recordId)
    {
        var store = host.Services.GetRequiredService<ICaseStateStore>();
        var scope = store.AuthorizeCase(caseId, "test-owner");
        var state = store.Read(scope);
        var content = JsonSerializer.Serialize(new
        {
            sourceId = "technical", recordId, recordVersion = 1, asOf = "2026-10-01", caseId,
            kind = "test", stale = false, payload = new { fields = new { value = "synthetic" } }, synthetic = true,
        }, Json);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        var gathered = EvidenceIntake.CreateSourceBasis([new SourceBasisPin("technical", recordId, 1, content, digest)]);
        var prior = state.Basis.Count == 0 ? string.Empty : state.Basis[^1].BasisDigest;
        var basis = CaseStateStore.CreateBasisVersion(state.Basis.Count + 1, gathered.Content, prior);
        store.Commit(scope, state.Revision, new CaseCommit(basis, null, "summary-test", "source-basis.gathered", "test pin"));
    }
}
