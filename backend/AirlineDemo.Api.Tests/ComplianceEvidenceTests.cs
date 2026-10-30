using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Policy;
using AirlineDemo.Api.Reviews;
using AirlineDemo.Api.SourceSystems;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirlineDemo.Api.Tests;

public sealed class ComplianceEvidenceTests
{
    private const string Url = "/api/v1/cases/case-001/compliance-evidence";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ProjectsExactRegimeEvidenceAndAuditMetadataWithoutPersonalDataOrClaims()
    {
        using var h = new Harness();
        using var response = await h.Get();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        Assert.Equal(["EASA", "EU sanctions", "FAA-91", "GDPR", "UK sanctions"],
            root.GetProperty("regimes").EnumerateArray().Select(r => r.GetProperty("regime").GetString()).ToArray());
        var requirement = Requirement(root, "EASA", "REQ-SHARED");
        Assert.Equal("met_by_evidence", requirement.GetProperty("status").GetString());
        Assert.Equal("lessor", requirement.GetProperty("responsibleParty").GetString());
        Assert.Equal("""[{"sourceId":"records","recordId":"doc-record","recordVersion":1,"access":"not_permitted"}]""",
            requirement.GetProperty("evidence").GetRawText());
        var audit = h.Store.Read(h.Scope).Audit.Single(a => a.Sequence == h.DecisionSequence);
        using var detail = JsonDocument.Parse(audit.Detail);
        Assert.Equal(JsonSerializer.Serialize(new
        {
            sequence = audit.Sequence, auditEventId = detail.RootElement.GetProperty("AuditEventId").GetString(),
            actorId = audit.Actor, role = "technical-reviewer", at = audit.At, decision = "accept",
        }, Json), requirement.GetProperty("decision").GetRawText());
        Assert.Equal("not_assessed", Requirement(root, "GDPR", "REQ-GDPR").GetProperty("status").GetString());
        Assert.Equal("needs_review", Requirement(root, "EU sanctions", "REQ-SANCTIONS").GetProperty("status").GetString());
        Assert.Equal("not_assessed", Requirement(root, "UK sanctions", "REQ-SANCTIONS").GetProperty("status").GetString());
        Assert.Equal(["Access control", "Audit trail", "Pinned evidence"], root.GetProperty("responsibilities")
            .GetProperty("platform").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(["Determine applicable obligations", "Review evidence and record decisions", "Obtain specialist advice"],
            root.GetProperty("responsibilities").GetProperty("customer").EnumerateArray().Select(x => x.GetString()).ToArray());
        foreach (var forbidden in new[] { "subjectName", "licenseNumber", "SYN-PRIVATE-NAME", "SYN-PRIVATE-LICENCE",
                     "SYN-DOCUMENT-TEXT", "compliant", "clearance granted", "legal advice" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingTagsMissingWitnessesAndOtherRegimesNeverReceiveAnEvidenceMetStatus()
    {
        using var h = new Harness();
        using var response = await h.Get();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = body.RootElement;
        foreach (var (regime, id) in new[] { ("EASA", "REQ-NO-EVIDENCE"), ("EASA", "REQ-NO-OWNER"), ("FAA-91", "REQ-SHARED") })
        {
            var requirement = Requirement(root, regime, id);
            Assert.Equal("needs_review", requirement.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, requirement.GetProperty("decision").ValueKind);
        }
        var untagged = root.GetProperty("untagged").EnumerateArray().ToArray();
        Assert.Equal(["REQ-UNTAGGED"], untagged.Select(x => x.GetProperty("requirementId").GetString()).ToArray());
        Assert.Equal("needs_review", untagged[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, untagged[0].GetProperty("applicability").ValueKind);
    }

    [Fact]
    public async Task UsesOnePinnedSnapshotAcrossConcurrentBasisChangesAndMakesNoWritesOrSourceReads()
    {
        using var h = new Harness();
        var before = h.Store.Read(h.Scope);
        h.Probe.Reads = 0;
        h.Probe.AfterRead = () =>
        {
            h.Probe.AfterRead = null;
            var newer = EvidenceIntake.CreateSourceBasis([Pin("records", "doc-record", "document_index",
                new { fields = new { documentId = "DOC-01", documentVersion = "2", containsPersonalData = "true" } }, 2)]);
            h.Store.Commit(h.Scope, before.Revision, new CaseCommit(
                CaseStateStore.CreateBasisVersion(2, newer.Content, before.Basis[0].BasisDigest),
                null, "presenter", "source-basis.gathered", "New source snapshot"));
        };
        using var response = await h.Get();
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, h.Probe.Reads);
        Assert.Equal(0, h.Probe.Writes);
        Assert.Equal(0, h.Technical.Calls + h.Maintenance.Calls + h.Finance.Calls + h.Records.Calls + h.Contracts.Calls);
        Assert.Equal(1, body.RootElement.GetProperty("basisVersion").GetInt32());
        Assert.Equal(1, Requirement(body.RootElement, "EASA", "REQ-SHARED").GetProperty("evidence")[0].GetProperty("recordVersion").GetInt32());
        Assert.Equal(before.Revision + 1, h.Store.Read(h.Scope).Revision);
        using var repeat = await h.Get();
        using var next = JsonDocument.Parse(await repeat.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(next.RootElement.GetProperty("currentBasis").GetBoolean());
        Assert.Equal(1, next.RootElement.GetProperty("basisVersion").GetInt32());
    }

    [Theory]
    [InlineData("presenter", "case-001", 200, "not_permitted", true)]
    [InlineData("technical-reviewer", "case-001", 200, "permitted", true)]
    [InlineData("finance-reviewer", "case-001", 200, "not_permitted", false)]
    [InlineData("legal-reviewer", "case-001", 200, "not_permitted", false)]
    [InlineData("executive", "case-001", 404, "", false)]
    [InlineData("unknown", "case-001", 404, "", false)]
    [InlineData("presenter", "case-002", 404, "", false)]
    [InlineData("technical-reviewer", "case-002", 404, "", false)]
    public async Task EnforcesCaseScopeAndSourceMetadataMatrix(string role, string scope, int expected, string access, bool seesIdentity)
    {
        using var h = new Harness();
        h.Probe.Reads = 0;
        using var response = await h.Get(role: role, scopes: [scope]);
        Assert.Equal(expected, (int)response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        if (expected == 404)
        {
            Assert.Equal(0, h.Probe.Reads);
            Assert.Equal(0, h.Technical.Calls + h.Maintenance.Calls + h.Finance.Calls + h.Records.Calls + h.Contracts.Calls);
            Assert.Equal("""{"contractVersion":1,"error":{"code":"not_found"}}""", text);
            return;
        }
        using var body = JsonDocument.Parse(text);
        if (role == "legal-reviewer")
        {
            Assert.Equal("not_permitted", body.RootElement.GetProperty("applicabilityAccess").GetString());
            Assert.Empty(body.RootElement.GetProperty("regimes").EnumerateArray());
            Assert.DoesNotContain("tech-app-EASA", text, StringComparison.Ordinal);
            return;
        }
        var reference = Requirement(body.RootElement, "EASA", "REQ-SHARED").GetProperty("evidence")[0];
        Assert.Equal(access, reference.GetProperty("access").GetString());
        Assert.Equal(seesIdentity ? "doc-record" : null, reference.GetProperty("recordId").GetString());
        Assert.Equal(seesIdentity ? 1 : (int?)null, reference.GetProperty("recordVersion").Deserialize<int?>());
        if (role == "technical-reviewer")
        {
            var restricted = Requirement(body.RootElement, "GDPR", "REQ-GDPR").GetProperty("evidence")[0];
            Assert.Equal("not_permitted", restricted.GetProperty("access").GetString());
            Assert.Equal(JsonValueKind.Null, restricted.GetProperty("recordId").ValueKind);
        }
    }

    [Fact]
    public async Task PreservesExistingPresenterRestrictionsAndRejectsMutationsVersionsAndInvalidSelections()
    {
        using var h = new Harness();
        using var deniedSource = await h.Get("/api/v1/cases/case-001/sources/records/records");
        Assert.Equal(HttpStatusCode.Forbidden, deniedSource.StatusCode);
        using var deniedDossier = await h.Get($"/api/v1/cases/case-001/decisions/{h.DecisionSequence}/dossier");
        Assert.Equal(HttpStatusCode.NotFound, deniedDossier.StatusCode);
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, Url);
            using var response = await h.Client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
        foreach (var query in new[] { "?decisionId=", "?decisionId=0", "?decisionId=-1", "?decisionId=x", "?decisionId=2&decisionId=3", "?sourceId=contracts" })
        {
            using var response = await h.Get(Url + query);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("""{"contractVersion":1,"error":{"code":"invalid_request"}}""",
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        using var missing = await h.Get(Url + "?decisionId=999");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var version = await h.Get(Url.Replace("/v1/", "/v2/"));
        Assert.Equal(HttpStatusCode.BadRequest, version.StatusCode);
        h.Client.DefaultRequestHeaders.Remove("Cookie");
        using var anonymous = await h.Client.GetAsync(Url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task LaterAssessmentsDoNotRewriteAnEarlierDecisionAndHistoricalSelectionIsExact()
    {
        using var h = new Harness();
        var firstSequence = h.DecisionSequence;
        const string quote = "Evidence baseline supported.";
        var basis = new AssessmentBasis("basis-v1", 1, "2026-10-01", new(1, 1, 0, 0), false,
            new Dictionary<string, BasisDocument> { ["DOC-01"] = BasisDocument.Create("DOC-01", [quote]) });
        var assessments = new AssessmentStore();
        assessments.SetCurrentBasis("case-001", basis);
        var later = new AssessmentExecutionService(h.Store, new(), assessments).Assess(h.Scope, basis,
            [new AssessmentClaim(ClaimKind.Baseline, quote, "Engine", "SYN-01", [new("DOC-01", 1, quote)],
                RequirementId: "REQ-SHARED", EvidenceRegime: "FAA-91")]);
        Assert.Equal("Baseline", later.Outcome);
        var state = h.Store.Read(h.Scope);
        var rejected = TechnicalReviewPolicy.Decide(h.Store, ["case-001"], "technical-reviewer", "reviewer", "case-001",
            new(state.Revision, 1, "reject", "Rechecked"), "trace-2");
        using var latest = await h.Get();
        using var latestBody = JsonDocument.Parse(await latest.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("needs_review", Requirement(latestBody.RootElement, "EASA", "REQ-SHARED").GetProperty("status").GetString());
        Assert.Equal(rejected.Decision!.Sequence, latestBody.RootElement.GetProperty("basisDecision").GetProperty("sequence").GetInt64());
        using var historical = await h.Get(Url + $"?decisionId={firstSequence}");
        using var historyBody = JsonDocument.Parse(await historical.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("met_by_evidence", Requirement(historyBody.RootElement, "EASA", "REQ-SHARED").GetProperty("status").GetString());
        Assert.Equal("needs_review", Requirement(historyBody.RootElement, "FAA-91", "REQ-SHARED").GetProperty("status").GetString());
        Assert.Equal(firstSequence, historyBody.RootElement.GetProperty("basisDecision").GetProperty("sequence").GetInt64());
    }

    [Theory]
    [InlineData("missing", 200)]
    [InlineData("stale", 200)]
    [InlineData("duplicate-tag", 200)]
    [InlineData("version-mismatch", 422)]
    [InlineData("wrong-case", 422)]
    public async Task MissingStaleDuplicateAndCorruptPinsFailClosed(string condition, int expected)
    {
        using var h = new Harness(pins =>
        {
            var document = pins.Single(p => p.RecordId == "doc-record");
            if (condition == "missing") pins.Remove(document);
            if (condition == "stale")
            {
                pins.Remove(document);
                pins.Add(Pin("records", "doc-record", "document_index",
                    new { fields = new { documentId = "DOC-01", documentVersion = "1", containsPersonalData = "true" } }, stale: true));
            }
            if (condition == "duplicate-tag")
                pins.Add(Pin("technical", "duplicate", "applicability",
                    new { fields = new { requirementId = "REQ-SHARED", regime = "EASA", responsibleParty = "lessor" } }));
            if (condition is "version-mismatch" or "wrong-case")
            {
                pins.Remove(document);
                pins.Add(condition switch
                {
                    "version-mismatch" => document with { RecordVersion = 2 },
                    _ => Pin("records", "doc-record", "document_index",
                        new { fields = new { documentId = "DOC-01" } }, caseId: "case-002"),
                });
            }
        });
        using var response = await h.Get();
        Assert.Equal(expected, (int)response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        if (expected == 422)
            Assert.Equal("""{"contractVersion":1,"error":{"code":"evidence_incomplete"}}""", text);
        else
        {
            using var body = JsonDocument.Parse(text);
            Assert.All(body.RootElement.GetProperty("regimes").EnumerateArray()
                .Single(r => r.GetProperty("regime").GetString() == "EASA").GetProperty("requirements").EnumerateArray()
                .Where(r => r.GetProperty("requirementId").GetString() == "REQ-SHARED"),
                r => Assert.Equal("needs_review", r.GetProperty("status").GetString()));
        }
    }

    [Fact]
    public async Task MissingApplicabilityIncludesAssessmentAndFlagWithoutBorrowingAnotherRegimeTag()
    {
        using var h = new Harness(pins => pins.RemoveAll(p => p.RecordId is "tech-app-EASA-REQ-SHARED" or "tech-app-EU sanctions-REQ-SANCTIONS"));
        using var response = await h.Get();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var untagged = body.RootElement.GetProperty("untagged").EnumerateArray().ToArray();
        Assert.Equal(["REQ-SANCTIONS", "REQ-UNTAGGED", "REQ-SHARED"], untagged.Select(r => r.GetProperty("requirementId").GetString()).ToArray());
        Assert.All(untagged, r => Assert.Equal("needs_review", r.GetProperty("status").GetString()));
        Assert.Equal("needs_review", Requirement(body.RootElement, "FAA-91", "REQ-SHARED").GetProperty("status").GetString());
    }

    private static JsonElement Requirement(JsonElement body, string regime, string id) => body.GetProperty("regimes")
        .EnumerateArray().Single(r => r.GetProperty("regime").GetString() == regime).GetProperty("requirements")
        .EnumerateArray().Single(r => r.GetProperty("requirementId").GetString() == id);

    private static SourceBasisPin Pin(string source, string id, string kind, object payload, int version = 1, bool stale = false, string caseId = "case-001")
    {
        var fields = JsonSerializer.SerializeToElement(payload, Json);
        var recordPayload = fields.TryGetProperty("fields", out _) ? fields : JsonSerializer.SerializeToElement(new { fields = payload }, Json);
        var content = JsonSerializer.Serialize(new { sourceId = source, recordId = id, recordVersion = version,
            asOf = "2026-10-01", caseId, kind, synthetic = true, stale, payload = recordPayload }, Json);
        return new(source, id, version, content, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant());
    }

    private sealed class Harness : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"regime-evidence-{Guid.NewGuid():N}.json");
        private readonly WebApplicationFactory<Program> factory;
        public CaseStateStore Store { get; }
        public CaseScope Scope { get; }
        public ReadProbe Probe { get; }
        public HttpClient Client { get; }
        public long DecisionSequence { get; }
        public SourceProbe<TechnicalPayload> Technical { get; } = new();
        public SourceProbe<MaintenancePayload> Maintenance { get; } = new();
        public SourceProbe<FinancePayload> Finance { get; } = new();
        public SourceProbe<RecordsPayload> Records { get; } = new();
        public SourceProbe<ContractsPayload> Contracts { get; } = new();

        public Harness(Action<List<SourceBasisPin>>? change = null)
        {
            Store = new(path);
            Scope = Store.CreateCase("case-001", "presenter");
            var pins = new List<SourceBasisPin>();
            foreach (var (id, regime, party) in new[]
            {
                ("REQ-SHARED", "EASA", "lessor"), ("REQ-SHARED", "FAA-91", "lessor"),
                ("REQ-GDPR", "GDPR", "lessor"), ("REQ-SANCTIONS", "EU sanctions", "lessor"),
                ("REQ-SANCTIONS", "UK sanctions", "lessor"), ("REQ-NO-EVIDENCE", "EASA", "lessor"),
            })
                pins.Add(Pin("technical", $"tech-app-{regime}-{id}", "applicability",
                    new { fields = new { requirementId = id, regime, responsibleParty = party } }));
            pins.Add(Pin("technical", "tech-no-owner", "applicability", new { fields = new { requirementId = "REQ-NO-OWNER", regime = "EASA" } }));
            foreach (var (id, topic, regime, result) in new[]
            {
                ("REQ-GDPR", "gdpr", "", "not_assessed"), ("REQ-UNTAGGED", "gdpr", "", "not_assessed"),
                ("REQ-SANCTIONS", "sanctions", "EU", "specialist_review_required"), ("REQ-SANCTIONS", "sanctions", "UK", "not_assessed"),
            })
            {
                var fields = new Dictionary<string, string> { ["relatedRequirementId"] = id, ["topic"] = topic, ["result"] = result };
                if (regime.Length > 0) fields["regime"] = regime;
                pins.Add(Pin("contracts", $"flag-{id}-{regime}", "specialist_flag", new { fields }));
            }
            pins.Add(Pin("records", "doc-record", "document_index", new
            {
                documentId = "DOC-01", documentVersion = "1", containsPersonalData = true,
                subjectName = "SYN-PRIVATE-NAME", licenseNumber = "SYN-PRIVATE-LICENCE", text = "SYN-DOCUMENT-TEXT",
            }));
            change?.Invoke(pins);
            var sourceBasis = EvidenceIntake.CreateSourceBasis(pins);
            Store.Commit(Scope, 0, new CaseCommit(CaseStateStore.CreateBasisVersion(1, sourceBasis.Content, ""), null, "presenter", "submit-evidence", "basis"));
            const string quote = "Evidence baseline supported.";
            var basis = new AssessmentBasis("basis-v1", 1, "2026-10-01", new(1, 1, 0, 0), false,
                new Dictionary<string, BasisDocument> { ["DOC-01"] = BasisDocument.Create("DOC-01", [quote]) }, pins);
            var assessments = new AssessmentStore();
            assessments.SetCurrentBasis("case-001", basis);
            var assessment = new AssessmentExecutionService(Store, new(), assessments).Assess(Scope, basis,
                [new AssessmentClaim(ClaimKind.Baseline, quote, "Engine", "SYN-01", [new("DOC-01", 1, quote)], RequirementId: "REQ-SHARED", EvidenceRegime: "EASA")]);
            Assert.Equal("Baseline", assessment.Outcome);
            var state = Store.Read(Scope);
            DecisionSequence = TechnicalReviewPolicy.Decide(Store, ["case-001"], "technical-reviewer", "reviewer", "case-001",
                new(state.Revision, 1, "accept", "SYN-PRIVATE-NAME compliant legal advice"), "trace-evidence").Decision!.Sequence;
            Probe = new(Store);
            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                    { ["CaseState:Path"] = path, ["Demo:Passcode"] = "test-regimes" }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ICaseStateStore>(); services.AddSingleton<ICaseStateStore>(Probe);
                    Replace(services, Technical); Replace(services, Maintenance); Replace(services, Finance); Replace(services, Records); Replace(services, Contracts);
                });
            });
            Client = factory.CreateClient();
        }

        private static void Replace<T>(IServiceCollection services, SourceProbe<T> probe) where T : class
        { services.RemoveAll<ISourceSystem<T>>(); services.AddSingleton<ISourceSystem<T>>(probe); }

        public Task<HttpResponseMessage> Get(string url = Url, string role = "presenter", string[]? scopes = null)
        {
            var session = factory.Services.GetRequiredService<DemoSessionStore>().Create(new DemoUser("reader", "Reader", role, scopes ?? ["case-001"]));
            Client.DefaultRequestHeaders.Remove("Cookie");
            Client.DefaultRequestHeaders.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={session.Id}");
            return Client.GetAsync(url, TestContext.Current.CancellationToken);
        }
        public void Dispose() { Client.Dispose(); factory.Dispose(); if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class SourceProbe<T> : ISourceSystem<T> where T : class
    {
        public int Calls { get; private set; }
        public Task<SourceReadResult<IReadOnlyList<SourceRecord<T>>>> ListAsync(string caseId, CancellationToken cancellationToken = default, int? maxRecords = null)
        { Calls++; throw new InvalidOperationException("The view must not refresh source records."); }
        public Task<SourceReadResult<SourceRecord<T>>> GetAsync(string caseId, string recordId, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("The view must not refresh source records."); }
    }

    private sealed class ReadProbe(ICaseStateStore inner) : ICaseStateStore
    {
        public int Reads { get; set; }
        public int Writes { get; private set; }
        public Action? AfterRead { get; set; }
        public CaseScope AuthorizeCase(string id, string owner) => inner.AuthorizeCase(id, owner);
        public string? OwnerOf(string id) => inner.OwnerOf(id);
        public CaseScope CreateCase(string id, string owner) => inner.CreateCase(id, owner);
        public CaseState Read(CaseScope scope) { Reads++; var state = inner.Read(scope); AfterRead?.Invoke(); return state; }
        public BasisVersion GetBasisVersion(CaseScope scope, int version) => throw new InvalidOperationException();
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => throw new InvalidOperationException();
        public CommitResult Commit(CaseScope scope, long revision, CaseCommit commit) { Writes++; throw new InvalidOperationException(); }
        public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope) => inner.ListWorkItems(scope);
        public CommitResult CreateWorkItem(CaseScope scope, long revision, WorkItemDraft draft, string actor) => throw new InvalidOperationException();
        public CommitResult ChangeWorkItemState(CaseScope scope, long revision, string item, string state, string actor) => throw new InvalidOperationException();
        public void AppendTrace(CaseScope scope, InvestigationTrace trace) => throw new InvalidOperationException();
        public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId) => throw new InvalidOperationException();
    }
}
