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
using AirlineDemo.Api.Requests;
using AirlineDemo.Api.Reviews;
using AirlineDemo.Api.SourceSystems;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        foreach (var versionNumber in new[] { 0, 2 })
        {
            using var version = await Get(client, cookie, $"/api/v{versionNumber}/cases/case-001/decisions/2/dossier", ct);
            Assert.Equal(HttpStatusCode.BadRequest, version.StatusCode);
            var versionBody = await version.Content.ReadAsStringAsync(ct);
            Assert.Equal("""{"contractVersion":1,"error":{"code":"unsupported_contract_version"}}""", versionBody);
            AssertNoRestrictedContent(versionBody);
        }
        foreach (var id in new[] { "not-a-sequence", "999" })
        {
            using var missing = await Get(client, cookie, $"/api/v1/cases/case-001/decisions/{id}/dossier", ct);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            var missingBody = await missing.Content.ReadAsStringAsync(ct);
            Assert.Equal("""{"contractVersion":1,"error":{"code":"not_found","message":"Not found."}}""", missingBody);
            AssertNoRestrictedContent(missingBody);
        }
        var incompletePath = Path.Combine(_dir, "incomplete.json");
        Seed(incompletePath, includeEvidence: false);
        using var incompleteFactory = Factory(incompletePath);
        using var incompleteClient = incompleteFactory.CreateClient();
        var incompleteCookie = await SignIn(incompleteClient, "reviewer", ct);
        using var incomplete = await Get(incompleteClient, incompleteCookie, "/api/v1/cases/case-001/decisions/2/dossier", ct);
        Assert.Equal((HttpStatusCode)422, incomplete.StatusCode);
        var incompleteBody = await incomplete.Content.ReadAsStringAsync(ct);
        Assert.Equal("""{"contractVersion":1,"error":{"code":"dossier_incomplete","message":"Dossier is incomplete."}}""", incompleteBody);
        AssertNoRestrictedContent(incompleteBody);
    }

    [Fact]
    public void HashesExactAuditEvidenceDeterministicallyAndMeetsFiftyPinLatency()
    {
        var path = Path.Combine(_dir, "snapshot.json");
        var inner = Seed(path, includeEvidence: true, extraPins: 48);
        var store = new TrackingStore(inner);
        var scope = store.AuthorizeCase(CaseId, "presenter");
        store.ResetCounts();
        var service = new DecisionDossierService(store, TimeProvider.System);
        var elapsed = new double[100]; string? originalHash = null;
        string? firstSerialized = null;
        for (var i = 0; i < elapsed.Length; i++)
        {
            var start = Stopwatch.GetTimestamp();
            var result = service.Export(scope, CaseId, 2, "technical-reviewer");
            Assert.Equal(DossierResultKind.Success, result.Kind);
            Assert.Equal(50, result.Dossier!.Basis.SourcePins.Count);
            originalHash ??= result.Dossier.ContentHash;
            Assert.Equal(originalHash, result.Dossier.ContentHash);
            var serialized = JsonSerializer.Serialize(result.Dossier, JsonOptions);
            if (firstSerialized is null) firstSerialized = serialized;
            else
            {
                var expected = JsonNode.Parse(firstSerialized)!;
                var actual = JsonNode.Parse(serialized)!;
                expected.AsObject().Remove("exportedAt");
                actual.AsObject().Remove("exportedAt");
                Assert.Equal(expected.ToJsonString(JsonOptions), actual.ToJsonString(JsonOptions));
            }
            elapsed[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(elapsed);
        Assert.True(elapsed[(int)Math.Ceiling(.95 * elapsed.Length) - 1] <= 2000, $"p95 {elapsed[94]} ms.");
        Assert.Equal(0, store.CommitCalls);
        var changed = TechnicalReviewPolicy.Decide(inner, [CaseId], CaseEndpoints.ReviewerRole, "reviewer", CaseId,
            new TechnicalReviewCommand(2, 1, "accept", "Changed recorded reason."), "trace-dossier-1");
        Assert.NotEqual(originalHash, service.Export(scope, CaseId, changed.Decision!.Sequence, "technical-reviewer").Dossier!.ContentHash);
    }

    [Fact]
    public async Task UsesOneExactSnapshotAndNeverRefreshesSourcePins()
    {
        var inner = Seed(Path.Combine(_dir, "pins.json"), includeEvidence: true, extraPins: 48);
        var store = new TrackingStore(inner);
        var scope = store.AuthorizeCase(CaseId, "presenter");
        store.AfterRead = (readScope, snapshot) =>
        {
            if (store.ReadCalls != 1) return;
            var newerBasis = EvidenceIntake.CreateSourceBasis([
                Pin("records", "record-doc-1", "document", new
                {
                    documentId = "SYN-DOC-01", documentVersion = "2", containsPersonalData = true,
                    subjectName = "SYN-PERSON-NAME-01",
                }),
            ]);
            inner.Commit(readScope, snapshot.Revision, new CaseCommit(
                CaseStateStore.CreateBasisVersion(2, newerBasis.Content, snapshot.Basis[^1].BasisDigest),
                null, "presenter", "submit-evidence", "newer source pin"));
        };
        var records = new SourceProbe<RecordsPayload>(new SourceRecord<RecordsPayload>
        {
            SourceId = "records", RecordId = "record-doc-1", RecordVersion = 2,
            AsOf = new DateOnly(2026, 10, 2), CaseId = CaseId, Kind = "document", Stale = false,
            Payload = new RecordsPayload(new Dictionary<string, string>
            {
                ["documentId"] = "SYN-DOC-01", ["documentVersion"] = "2", ["containsPersonalData"] = "true",
            }),
        });
        var technical = new SourceProbe<TechnicalPayload>();
        var finance = new SourceProbe<FinancePayload>();
        var maintenance = new SourceProbe<MaintenancePayload>();
        var contracts = new SourceProbe<ContractsPayload>();
        using var factory = Factory(Path.Combine(_dir, "pins.json"), store, null, services =>
        {
            services.RemoveAll<ISourceSystem<RecordsPayload>>();
            services.AddSingleton<ISourceSystem<RecordsPayload>>(records);
            services.RemoveAll<ISourceSystem<TechnicalPayload>>();
            services.AddSingleton<ISourceSystem<TechnicalPayload>>(technical);
            services.RemoveAll<ISourceSystem<FinancePayload>>();
            services.AddSingleton<ISourceSystem<FinancePayload>>(finance);
            services.RemoveAll<ISourceSystem<MaintenancePayload>>();
            services.AddSingleton<ISourceSystem<MaintenancePayload>>(maintenance);
            services.RemoveAll<ISourceSystem<ContractsPayload>>();
            services.AddSingleton<ISourceSystem<ContractsPayload>>(contracts);
        });
        using var client = factory.CreateClient();
        var cookie = await SignIn(client, "reviewer", TestContext.Current.CancellationToken);
        using var response = await Get(client, cookie, "/api/v1/cases/case-001/decisions/2/dossier", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var pins = json.RootElement.GetProperty("basis").GetProperty("sourcePins")
            .EnumerateArray().Select(pin => new DossierSourcePin(
                pin.GetProperty("sourceId").GetString()!, pin.GetProperty("recordId").GetString()!,
                pin.GetProperty("recordVersion").GetInt32())).ToArray();
        Assert.Equal(50, pins.Length);
        Assert.Contains(new DossierSourcePin("records", "record-doc-1", 1), pins);
        Assert.Contains(new DossierSourcePin("maintenance", "record-extra-47", 1), pins);
        Assert.DoesNotContain(new DossierSourcePin("records", "record-doc-1", 2), pins);
        Assert.Equal(1, store.ReadCalls);
        Assert.Equal(0, store.ReadAuditCalls);
        Assert.Equal(0, store.CommitCalls);
        Assert.Equal(2, inner.Read(scope).Basis.Count);
        Assert.Equal(0, records.Calls);
        Assert.Equal(0, technical.Calls);
        Assert.Equal(0, finance.Calls);
        Assert.Equal(0, maintenance.Calls);
        Assert.Equal(0, contracts.Calls);
    }

    [Fact]
    public async Task RequiresMatchingRoleAndCaseScopeBeforeProtectedReads()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_dir, "auth.json");
        var inner = Seed(path, includeEvidence: true);
        var store = new TrackingStore(inner);
        var dossierService = new CountingDossierService(store);
        store.CreateCase("case-002", "presenter");
        store.ResetCounts();
        using var factory = Factory(path, store, dossierService);
        using var client = factory.CreateClient();
        var deniedSessions = new[]
        {
            Session(factory, "finance", "finance-reviewer", [CaseId]),
            Session(factory, "legal", "legal-reviewer", [CaseId]),
            Session(factory, "presenter-role", "presenter", [CaseId]),
            Session(factory, "executive", "executive", [CaseId]),
            Session(factory, "unknown-role", "unknown-role", [CaseId]),
            Session(factory, "out-of-scope-reviewer", "technical-reviewer", ["case-002"]),
        };
        foreach (var cookie in deniedSessions)
        {
            using var denied = await Get(client, cookie, "/api/v1/cases/case-001/decisions/2/dossier", ct);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            var deniedBody = await denied.Content.ReadAsStringAsync(ct);
            Assert.Equal("""{"contractVersion":1,"error":{"code":"not_found","message":"Not found."}}""", deniedBody);
            AssertNoRestrictedContent(deniedBody);
            Assert.Equal(0, store.ReadCalls);
            Assert.Equal(0, store.ReadAuditCalls);
            Assert.Equal(0, dossierService.Calls);
        }

        var mismatchedPath = Path.Combine(_dir, "stored-role-mismatch.json");
        var mismatchedStore = new TrackingStore(Seed(mismatchedPath, includeEvidence: true))
        {
            ReadTransform = snapshot => ReplaceDecisionDetail(snapshot, detail => detail["Role"] = "finance-reviewer"),
        };
        var mismatchedService = new CountingDossierService(mismatchedStore);
        using var mismatchedFactory = Factory(mismatchedPath, mismatchedStore, mismatchedService);
        using var mismatchedClient = mismatchedFactory.CreateClient();
        var reviewer = await SignIn(mismatchedClient, "reviewer", ct);
        using var mismatched = await Get(mismatchedClient, reviewer,
            "/api/v1/cases/case-001/decisions/2/dossier", ct);
        Assert.Equal(HttpStatusCode.NotFound, mismatched.StatusCode);
        Assert.Equal("""{"contractVersion":1,"error":{"code":"not_found","message":"Not found."}}""",
            await mismatched.Content.ReadAsStringAsync(ct));
        Assert.Equal(1, mismatchedStore.ReadCalls);
        Assert.Equal(0, mismatchedStore.ReadAuditCalls);
        Assert.Equal(1, mismatchedService.Calls);
    }

    [Fact]
    public async Task ExcludesRestrictedFieldsAndCarriesSimulationLimitations()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_dir, "private.json");
        Seed(path, includeEvidence: true);
        using var factory = Factory(path);
        using var client = factory.CreateClient();
        var cookie = await SignIn(client, "reviewer", ct);
        using var success = await Get(client, cookie, "/api/v1/cases/case-001/decisions/2/dossier", ct);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        var successBody = await success.Content.ReadAsStringAsync(ct);
        using var successJson = JsonDocument.Parse(successBody);
        var limitations = successJson.RootElement.GetProperty("limitations").EnumerateArray()
            .Select(item => item.GetString()).ToArray();
        Assert.Equal(new[]
        {
            "Not a certification.", "Not an approval.", "Not a legal conclusion.",
            "The SHA-256 contentHash is a checksum only and does not prove publisher identity or origin.",
        }, limitations);
        Assert.Equal("approved", successJson.RootElement.GetProperty("decision").GetProperty("outcome").GetString());
        AssertNoRestrictedContent(successBody);

        var incompletePath = Path.Combine(_dir, "private-incomplete.json");
        Seed(incompletePath, includeEvidence: false);
        using var incompleteFactory = Factory(incompletePath);
        using var incompleteClient = incompleteFactory.CreateClient();
        var incompleteCookie = await SignIn(incompleteClient, "reviewer", ct);
        using var error = await Get(incompleteClient, incompleteCookie, "/api/v1/cases/case-001/decisions/2/dossier", ct);
        Assert.Equal((HttpStatusCode)422, error.StatusCode);
        var errorBody = await error.Content.ReadAsStringAsync(ct);
        Assert.Equal("""{"contractVersion":1,"error":{"code":"dossier_incomplete","message":"Dossier is incomplete."}}""", errorBody);
        AssertNoRestrictedContent(errorBody);
    }

    [Theory]
    [InlineData("case-id")]
    [InlineData("audit-event-identifier")]
    [InlineData("sequence-conflict")]
    [InlineData("basis-version")]
    [InlineData("basis-digest")]
    [InlineData("basis-content")]
    public async Task RejectsSnapshotIdentityMismatchesAsIncomplete(string mismatch)
    {
        var path = Path.Combine(_dir, "mismatch-" + mismatch + ".json");
        var inner = Seed(path, includeEvidence: true);
        var store = new TrackingStore(inner)
        {
            ReadTransform = snapshot => mismatch switch
            {
                "case-id" => snapshot with { CaseId = "case-other" },
                "audit-event-identifier" => ReplaceDecisionDetail(snapshot, detail => detail["AuditEventId"] = ""),
                "sequence-conflict" => snapshot with
                {
                    Audit = [.. snapshot.Audit, snapshot.Audit.Single(item => item.Sequence == 2)],
                },
                "basis-version" => ReplaceDecisionDetail(snapshot, detail => detail["BasisVersion"] = 2),
                "basis-digest" => ReplaceDecisionDetail(snapshot, detail => detail["BasisDigest"] = "different-digest"),
                "basis-content" => snapshot with { Basis = [snapshot.Basis[0] with { Content = snapshot.Basis[0].Content + " " }] },
                _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
            },
        };
        using var factory = Factory(path, store);
        using var client = factory.CreateClient();
        var cookie = await SignIn(client, "reviewer", TestContext.Current.CancellationToken);
        using var response = await Get(client, cookie, "/api/v1/cases/case-001/decisions/2/dossier", TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal("""{"contractVersion":1,"error":{"code":"dossier_incomplete","message":"Dossier is incomplete."}}""",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("basis")]
    [InlineData("basis-id")]
    [InlineData("policy-version")]
    [InlineData("persisted-role")]
    [InlineData("audit-event-id")]
    [InlineData("source-id")]
    [InlineData("record-id")]
    [InlineData("record-version")]
    [InlineData("document-id")]
    [InlineData("document-version")]
    [InlineData("requirement-id")]
    [InlineData("requirement-version")]
    [InlineData("requirement-regime")]
    [InlineData("requirement-responsible-party")]
    public async Task RejectsMissingRequiredDossierFieldsAsIncomplete(string field)
    {
        var path = Path.Combine(_dir, "missing-" + field + ".json");
        var inner = Seed(path, includeEvidence: true);
        var store = new TrackingStore(inner)
        {
            ReadTransform = snapshot => MissingField(snapshot, field),
        };
        using var factory = Factory(path, store);
        using var client = factory.CreateClient();
        var cookie = await SignIn(client, "reviewer", TestContext.Current.CancellationToken);
        using var response = await Get(client, cookie, "/api/v1/cases/case-001/decisions/2/dossier", TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal("""{"contractVersion":1,"error":{"code":"dossier_incomplete","message":"Dossier is incomplete."}}""",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingDecisionAuditEventInAnOtherwiseLaterAuditHistoryIsIncomplete()
    {
        var inner = Seed(Path.Combine(_dir, "missing-audit-event.json"), includeEvidence: true);
        var scope = inner.AuthorizeCase(CaseId, "presenter");
        inner.Commit(scope, 2, new CaseCommit(null, null, "presenter", "case.note", "later event"));
        var store = new TrackingStore(inner)
        {
            ReadTransform = snapshot => snapshot with { Audit = snapshot.Audit.Where(item => item.Sequence != 2).ToArray() },
        };
        var path = Path.Combine(_dir, "missing-audit-event.json");
        using var factory = Factory(path, store);
        using var client = factory.CreateClient();
        var cookie = await SignIn(client, "reviewer", TestContext.Current.CancellationToken);
        using var response = await Get(client, cookie, "/api/v1/cases/case-001/decisions/2/dossier", TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal("""{"contractVersion":1,"error":{"code":"dossier_incomplete","message":"Dossier is incomplete."}}""",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

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

    private static CaseState ReplaceDecisionDetail(CaseState state, Action<JsonObject> change)
    {
        var audit = state.Audit.Select(item =>
        {
            if (item.Sequence != 2) return item;
            var detail = JsonNode.Parse(item.Detail)!.AsObject();
            change(detail);
            return item with { Detail = detail.ToJsonString(JsonOptions) };
        }).ToArray();
        return state with { Audit = audit };
    }

    private static CaseState MissingField(CaseState state, string field)
    {
        if (field == "basis") return state with { Basis = [] };
        if (field is "basis-id" or "policy-version" or "persisted-role" or "audit-event-id")
        {
            return ReplaceDecisionDetail(state, detail => detail[field switch
            {
                "basis-id" => "BasisId",
                "policy-version" => "PolicyVersion",
                "persisted-role" => "Role",
                _ => "AuditEventId",
            }] = "");
        }

        var sourceId = field.StartsWith("requirement-", StringComparison.Ordinal) ? "technical" : "records";
        var basis = state.Basis[0];
        var content = JsonNode.Parse(basis.Content)!.AsObject();
        var pins = content["pins"]!.AsArray();
        var pin = pins.Select(node => node!.AsObject())
            .Single(item => item["sourceId"]!.GetValue<string>() == sourceId);
        if (field is "source-id" or "record-id" or "record-version" or "requirement-version")
        {
            pin.Remove(field switch { "source-id" => "sourceId", "record-id" => "recordId", _ => "recordVersion" });
        }
        else
        {
            var record = JsonNode.Parse(pin["content"]!.GetValue<string>())!.AsObject();
            var payload = record["payload"]!.AsObject();
            if (field is "document-id" or "document-version")
                payload.Remove(field == "document-id" ? "documentId" : "documentVersion");
            else
            {
                var fields = payload["fields"]!.AsObject();
                fields.Remove(field switch
                {
                    "requirement-id" => "requirementId",
                    "requirement-regime" => "regime",
                    _ => "responsibleParty",
                });
            }
            var recordContent = record.ToJsonString(JsonOptions);
            pin["content"] = recordContent;
            pin["contentDigest"] = Hex(Encoding.UTF8.GetBytes(recordContent));
        }

        var updatedContent = content.ToJsonString(JsonOptions);
        var refreshed = CaseStateStore.CreateBasisVersion(1, updatedContent, string.Empty);
        return ReplaceDecisionDetail(state with { Basis = [refreshed] },
            detail => detail["BasisDigest"] = refreshed.BasisDigest);
    }

    private static void AssertNoRestrictedContent(string body)
    {
        foreach (var restricted in new[]
        {
            "subjectName", "licenseNumber", "containsPersonalData", "text", "prompt", "output", "cookie", "passcode", "token",
            "SYN-PERSON-NAME-01", "SYN-LICENSE-NUMBER-01", "SYN-DOCUMENT-TEXT-01", "SYN-RAW-PROMPT-01",
            "SYN-MODEL-OUTPUT-01", "SYN-COOKIE-01", "SYN-PASSCODE-01", "SYN-AUTH-TOKEN-01",
        })
            Assert.DoesNotContain(restricted, body, StringComparison.Ordinal);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static WebApplicationFactory<Program> Factory(string path, ICaseStateStore? store = null,
        IDecisionDossierService? dossierService = null, Action<IServiceCollection>? configureServices = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Passcode"] = Passcode,
                ["CaseState:Path"] = path,
            }));
            builder.ConfigureTestServices(services =>
            {
                if (store is not null)
                {
                    services.RemoveAll<ICaseStateStore>();
                    services.AddSingleton(store);
                }
                if (dossierService is not null)
                {
                    services.RemoveAll<IDecisionDossierService>();
                    services.AddSingleton(dossierService);
                }
                configureServices?.Invoke(services);
            });
        });

    private static string Session(WebApplicationFactory<Program> factory, string userId, string role, string[] scopes)
    {
        var session = factory.Services.GetRequiredService<DemoSessionStore>()
            .Create(new DemoUser(userId, userId, role, scopes));
        return $"{DemoAuthEndpoints.CookieName}={session.Id}";
    }

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

    private sealed class CountingDossierService(ICaseStateStore store) : IDecisionDossierService
    {
        private readonly DecisionDossierService _inner = new(store, TimeProvider.System);
        public int Calls { get; private set; }
        public DossierResult Export(CaseScope scope, string caseId, long decisionId, string role)
        {
            Calls++;
            return _inner.Export(scope, caseId, decisionId, role);
        }
    }

    private sealed class TrackingStore(ICaseStateStore inner) : ICaseStateStore
    {
        public int ReadCalls { get; private set; }
        public int ReadAuditCalls { get; private set; }
        public int CommitCalls { get; private set; }
        public Func<CaseState, CaseState>? ReadTransform { get; init; }
        public Action<CaseScope, CaseState>? AfterRead { get; set; }

        public void ResetCounts() => (ReadCalls, ReadAuditCalls, CommitCalls) = (0, 0, 0);
        public CaseScope AuthorizeCase(string caseId, string owner) => inner.AuthorizeCase(caseId, owner);
        public string? OwnerOf(string caseId) => inner.OwnerOf(caseId);
        public CaseScope CreateCase(string caseId, string owner) => inner.CreateCase(caseId, owner);
        public CaseState Read(CaseScope scope)
        {
            ReadCalls++;
            var snapshot = inner.Read(scope);
            AfterRead?.Invoke(scope, snapshot);
            return ReadTransform?.Invoke(snapshot) ?? snapshot;
        }
        public BasisVersion GetBasisVersion(CaseScope scope, int version) => inner.GetBasisVersion(scope, version);
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope)
        {
            ReadAuditCalls++;
            return inner.ReadAudit(scope);
        }
        public IReadOnlyList<CaseAssessment> ListAssessments(CaseScope scope) => inner.ListAssessments(scope);
        public CaseAssessment? FindAssessment(CaseScope scope, string assessmentId) => inner.FindAssessment(scope, assessmentId);
        public IReadOnlyList<DeliveryReceipt> ListRequestReceipts(CaseScope scope) => inner.ListRequestReceipts(scope);
        public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit)
        {
            CommitCalls++;
            return inner.Commit(scope, expectedRevision, commit);
        }
        public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope) => inner.ListWorkItems(scope);
        public CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor) =>
            inner.CreateWorkItem(scope, expectedRevision, draft, actor);
        public CommitResult ChangeWorkItemState(CaseScope scope, long expectedRevision, string itemId, string state, string actor) =>
            inner.ChangeWorkItemState(scope, expectedRevision, itemId, state, actor);
        public void AppendTrace(CaseScope scope, InvestigationTrace trace) => inner.AppendTrace(scope, trace);
        public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId) =>
            inner.FindTrace(scope, caseId, runId, investigationId);
    }

    private sealed class SourceProbe<T>(SourceRecord<T>? newerRecord = null) : ISourceSystem<T> where T : class
    {
        public int Calls { get; private set; }
        public Task<SourceReadResult<IReadOnlyList<SourceRecord<T>>>> ListAsync(
            string caseId, CancellationToken cancellationToken = default, int? maxRecords = null)
        {
            Calls++;
            IReadOnlyList<SourceRecord<T>> records = newerRecord is null ? [] : [newerRecord];
            return Task.FromResult<SourceReadResult<IReadOnlyList<SourceRecord<T>>>>(
                new SourceReadSucceeded<IReadOnlyList<SourceRecord<T>>>(records));
        }
        public Task<SourceReadResult<SourceRecord<T>>> GetAsync(
            string caseId, string recordId, CancellationToken cancellationToken = default)
        {
            Calls++;
            SourceReadResult<SourceRecord<T>> result = newerRecord is not null && newerRecord.CaseId == caseId
                && newerRecord.RecordId == recordId
                ? new SourceReadSucceeded<SourceRecord<T>>(newerRecord)
                : new SourceRecordNotFound<SourceRecord<T>>(recordId);
            return Task.FromResult(result);
        }
    }
}
