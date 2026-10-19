using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Decisions;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AirlineDemo.Api.Tests;

public sealed class DecisionDossierTests : IDisposable
{
    private const string CaseId = "case-001";
    private const string Passcode = "dossier-test-passcode";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "decision-dossier-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task ReturnsExactDossierContractAndRejectsInvalidOrIncompleteInputs()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_dir, "complete.json");
        var store = Seed(path, includeEvidence: true);
        using var factory = Factory(path);
        using var client = factory.CreateClient();
        var cookie = await SignIn(client, "reviewer", ct);
        using var response = await Get(client, cookie, "/api/v1/cases/case-001/decisions/2/dossier", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(ct);
        using var actual = JsonDocument.Parse(text);
        var audit = store.ReadAudit(store.AuthorizeCase(CaseId, "presenter")).Single(item => item.Sequence == 2);
        using var detail = JsonDocument.Parse(audit.Detail);
        var body = actual.RootElement;
        var expected = new
        {
            contractVersion = 1,
            caseId = CaseId,
            decisionId = 2L,
            exportedAt = body.GetProperty("exportedAt").GetDateTimeOffset(),
            basis = new
            {
                basisId = "basis-v1", basisVersion = 1,
                documents = new[] { new { documentId = "SYN-DOC-01", documentVersion = "1" } },
                sourcePins = new[]
                {
                    new { sourceId = "records", recordId = "record-doc-1", recordVersion = 1 },
                    new { sourceId = "technical", recordId = "record-app-1", recordVersion = 1 },
                },
                requirements = new[] { new { id = "SYN-REQ-01", version = 1, regime = "EASA", responsibleParty = "lessor" } },
            },
            policyVersion = TechnicalReviewPolicy.Version,
            traceId = "trace-dossier-1",
            decision = new { actorId = "reviewer", role = "technical-reviewer", time = audit.At, reason = "Dossier test reason.", outcome = "approved" },
            auditEventIds = new[] { detail.RootElement.GetProperty("AuditEventId").GetString()! },
            contentHash = body.GetProperty("contentHash").GetString(),
            synthetic = true,
            limitations = new[]
            {
                "Not a certification.", "Not an approval.", "Not a legal conclusion.",
                "The SHA-256 contentHash is a checksum only and does not prove publisher identity or origin.",
            },
        };
        Assert.Equal(JsonSerializer.Serialize(expected, JsonOptions), text);
        Assert.NotEqual(audit.Sequence.ToString(), detail.RootElement.GetProperty("AuditEventId").GetString());
        foreach (var secret in new[] { "subjectName", "licenseNumber", "SYN-PERSON-NAME-01", "SYN-LICENSE-NUMBER-01",
                     "SYN-DOCUMENT-TEXT-01", "SYN-RAW-PROMPT-01", "SYN-MODEL-OUTPUT-01", "SYN-COOKIE-01", "SYN-PASSCODE-01", "SYN-AUTH-TOKEN-01" })
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        using var repeat = await Get(client, cookie, "/api/v1/cases/case-001/decisions/2/dossier", ct);
        var repeated = JsonNode.Parse(await repeat.Content.ReadAsStringAsync(ct))!;
        var first = JsonNode.Parse(text)!;
        first.AsObject().Remove("exportedAt");
        repeated!.AsObject().Remove("exportedAt");
        Assert.Equal(first.ToJsonString(JsonOptions), repeated.ToJsonString(JsonOptions));
        var hashInput = new
        {
            contractVersion = expected.contractVersion, caseId = expected.caseId, decisionId = expected.decisionId,
            basis = expected.basis, policyVersion = expected.policyVersion, traceId = expected.traceId,
            decision = expected.decision, auditEventIds = expected.auditEventIds, synthetic = expected.synthetic,
            limitations = expected.limitations,
        };
        Assert.Equal(Hex(JsonSerializer.SerializeToUtf8Bytes(hashInput, JsonOptions)), body.GetProperty("contentHash").GetString());
        using var version = await Get(client, cookie, "/api/v2/cases/case-001/decisions/2/dossier", ct);
        Assert.Equal(HttpStatusCode.BadRequest, version.StatusCode);
        Assert.Equal("""{"contractVersion":1,"error":{"code":"unsupported_contract_version"}}""", await version.Content.ReadAsStringAsync(ct));
        foreach (var id in new[] { "not-a-sequence", "999" })
        {
            using var missing = await Get(client, cookie, $"/api/v1/cases/case-001/decisions/{id}/dossier", ct);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal("""{"contractVersion":1,"error":{"code":"not_found","message":"Not found."}}""", await missing.Content.ReadAsStringAsync(ct));
        }
        foreach (var user in new[] { "fin-1", "legal-1", "presenter" })
        {
            using var denied = await Get(client, await SignIn(client, user, ct), "/api/v1/cases/case-001/decisions/2/dossier", ct);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.Equal("""{"contractVersion":1,"error":{"code":"not_found","message":"Not found."}}""", await denied.Content.ReadAsStringAsync(ct));
        }
        using var outOfScope = await Get(client, cookie, "/api/v1/cases/case-002/decisions/2/dossier", ct);
        Assert.Equal("""{"contractVersion":1,"error":{"code":"not_found","message":"Not found."}}""", await outOfScope.Content.ReadAsStringAsync(ct));
        var incompletePath = Path.Combine(_dir, "incomplete.json");
        Seed(incompletePath, includeEvidence: false);
        using var incompleteFactory = Factory(incompletePath);
        using var incompleteClient = incompleteFactory.CreateClient();
        var incompleteCookie = await SignIn(incompleteClient, "reviewer", ct);
        using var incomplete = await Get(incompleteClient, incompleteCookie, "/api/v1/cases/case-001/decisions/2/dossier", ct);
        Assert.Equal((HttpStatusCode)422, incomplete.StatusCode);
        Assert.Equal("""{"contractVersion":1,"error":{"code":"dossier_incomplete","message":"Dossier is incomplete."}}""",
            await incomplete.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public void HashesExactAuditEvidenceDeterministicallyAndMeetsFiftyPinLatency()
    {
        var path = Path.Combine(_dir, "snapshot.json");
        var store = Seed(path, includeEvidence: true, extraPins: 48);
        var scope = store.AuthorizeCase(CaseId, "presenter");
        var service = new DecisionDossierService(store, TimeProvider.System);
        var elapsed = new double[100]; string? originalHash = null;
        for (var i = 0; i < elapsed.Length; i++)
        {
            var start = Stopwatch.GetTimestamp();
            var result = service.Export(scope, CaseId, 2, "technical-reviewer");
            Assert.Equal(DossierResultKind.Success, result.Kind);
            Assert.Equal(50, result.Dossier!.Basis.SourcePins.Count);
            originalHash ??= result.Dossier.ContentHash;
            Assert.Equal(originalHash, result.Dossier.ContentHash);
            elapsed[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(elapsed);
        Assert.True(elapsed[(int)Math.Ceiling(.95 * elapsed.Length) - 1] <= 2000, $"p95 {elapsed[94]} ms.");
        var changed = TechnicalReviewPolicy.Decide(store, [CaseId], CaseEndpoints.ReviewerRole, "reviewer", CaseId,
            new TechnicalReviewCommand(2, 1, "accept", "Changed recorded reason."), "trace-dossier-1");
        Assert.NotEqual(originalHash, service.Export(scope, CaseId, changed.Decision!.Sequence, "technical-reviewer").Dossier!.ContentHash);
    }

    [Fact]
    public void UsesOneExactSnapshotAndNeverRefreshesSourcePins()
    {
        var store = Seed(Path.Combine(_dir, "pins.json"), includeEvidence: true, extraPins: 48);
        var dossier = new DecisionDossierService(store, TimeProvider.System)
            .Export(store.AuthorizeCase(CaseId, "presenter"), CaseId, 2, "technical-reviewer").Dossier!;
        Assert.Equal(50, dossier.Basis.SourcePins.Count);
        Assert.Contains(dossier.Basis.SourcePins, pin => pin == new DossierSourcePin("records", "record-doc-1", 1));
        Assert.Contains(dossier.Basis.SourcePins, pin => pin == new DossierSourcePin("maintenance", "record-extra-47", 1));
    }

    [Fact]
    public async Task RequiresMatchingRoleAndCaseScopeBeforeProtectedReads()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_dir, "auth.json");
        var store = Seed(path, includeEvidence: true);
        store.CreateCase("case-002", "presenter");
        using var factory = Factory(path);
        using var client = factory.CreateClient();
        foreach (var user in new[] { "fin-1", "legal-1", "presenter", "reviewer" })
        {
            var caseId = user == "reviewer" ? "case-002" : CaseId;
            using var denied = await Get(client, await SignIn(client, user, ct),
                $"/api/v1/cases/{caseId}/decisions/2/dossier", ct);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.Equal("""{"contractVersion":1,"error":{"code":"not_found","message":"Not found."}}""",
                await denied.Content.ReadAsStringAsync(ct));
        }
    }

    [Fact]
    public Task ExcludesRestrictedFieldsAndCarriesSimulationLimitations() =>
        ReturnsExactDossierContractAndRejectsInvalidOrIncompleteInputs();

    private CaseStateStore Seed(string path, bool includeEvidence, int extraPins = 0)
    {
        var store = new CaseStateStore(path);
        var scope = store.CreateCase(CaseId, "presenter");
        var pins = new List<SourceBasisPin>();
        if (includeEvidence)
        {
            pins.Add(Pin("technical", "record-app-1", "applicability", new { fields = new { requirementId = "SYN-REQ-01", regime = "EASA", responsibleParty = "lessor" } }));
            pins.Add(Pin("records", "record-doc-1", "document", new
            {
                documentId = "SYN-DOC-01", documentVersion = "1", containsPersonalData = true,
                subjectName = "SYN-PERSON-NAME-01", licenseNumber = "SYN-LICENSE-NUMBER-01",
                text = "SYN-DOCUMENT-TEXT-01", prompt = "SYN-RAW-PROMPT-01", output = "SYN-MODEL-OUTPUT-01",
                cookie = "SYN-COOKIE-01", passcode = "SYN-PASSCODE-01", token = "SYN-AUTH-TOKEN-01",
            }));
        }
        for (var i = 0; i < extraPins; i++)
            pins.Add(Pin("maintenance", $"record-extra-{i:D2}", "maintenance", new { index = i }));
        if (pins.Count == 0) pins.Add(Pin("maintenance", "record-extra", "maintenance", new { index = 1 }));
        var basis = EvidenceIntake.CreateSourceBasis(pins);
        store.Commit(scope, 0, new CaseCommit(CaseStateStore.CreateBasisVersion(1, basis.Content, string.Empty),
            null, "presenter", "submit-evidence", "basis 1"));
        var decided = TechnicalReviewPolicy.Decide(store, [CaseId], CaseEndpoints.ReviewerRole, "reviewer", CaseId,
            new TechnicalReviewCommand(1, 1, "accept", "Dossier test reason."), "trace-dossier-1");
        Assert.Equal(TechnicalReviewOutcome.Recorded, decided.Outcome);
        Assert.Equal(2, decided.Decision!.Sequence);
        return store;
    }

    private static SourceBasisPin Pin(string sourceId, string recordId, string kind, object payload)
    {
        var content = JsonSerializer.Serialize(new
        {
            sourceId, recordId, recordVersion = 1, asOf = "2026-10-01", caseId = CaseId, kind,
            stale = false, payload, synthetic = true,
        }, JsonOptions);
        return new SourceBasisPin(sourceId, recordId, 1, content, Hex(Encoding.UTF8.GetBytes(content)));
    }
    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static WebApplicationFactory<Program> Factory(string path) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Passcode"] = Passcode,
                ["CaseState:Path"] = path,
            })));

    private static async Task<string> SignIn(HttpClient client, string user, CancellationToken ct)
    {
        client.DefaultRequestHeaders.Remove("Cookie");
        using var response = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId = user, passcode = Passcode }, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return cookie;
    }
    private static Task<HttpResponseMessage> Get(HttpClient client, string cookie, string url, CancellationToken ct)
    {
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client.GetAsync(url, ct);
    }
}
