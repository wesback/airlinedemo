using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Policy;
using AirlineDemo.Api.Requests;
using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Tests;

public sealed class SpecialistReviewFlagTests
{
    private static readonly string[] Sources = ["technical", "maintenance", "finance", "records", "contracts"];
    private const string CaseId = "specialist-case";
    private const string Quote = "The return package needs review.";

    [Fact]
    public void PayloadContractAcceptsExactValuesAndRejectsEveryOutOfScopeCase()
    {
        var accepted = new[]
        {
            Flag("sanctions", "specialist_review_required", "EU"), Flag("sanctions", "specialist_review_required", "US"), Flag("sanctions", "specialist_review_required", "UK"),
            Flag("sanctions", "not_assessed", "EU"), Flag("sanctions", "not_assessed", "US"), Flag("sanctions", "not_assessed", "UK"),
            Flag("cape_town", "specialist_review_required"), Flag("cape_town", "not_assessed"),
            Flag("ifrs16", "specialist_review_required"), Flag("ifrs16", "not_assessed"),
            Flag("gdpr", "specialist_review_required"), Flag("gdpr", "not_assessed"),
            Flag("other", "specialist_review_required"), Flag("other", "not_assessed"),
        };
        foreach (var expected in accepted)
        {
            Assert.True(SpecialistReviewFlag.TryParse(ToFields(expected), out var actual));
            Assert.Equal(expected, actual);
        }
        var rejected = new[]
        {
            new Dictionary<string, string> { ["topic"] = "other", ["result"] = "not_assessed" },
            Fields(related: null), Fields(topic: null), Fields(result: null), Fields(related: ""), Fields(topic: ""), Fields(result: ""),
            Fields(topic: "Sanctions"), Fields(topic: "export_control"),
            Fields(result: "cleared"), Fields(result: "NOT_ASSESSED"),
            Fields(topic: "sanctions", regime: null), Fields(topic: "sanctions", regime: "eu"), Fields(topic: "sanctions", regime: "CN"),
            Fields(topic: "ifrs16", regime: "EU"), Fields(extra: true),
        };
        foreach (var fields in rejected)
        {
            Assert.False(SpecialistReviewFlag.TryParse(fields, out var actual));
            Assert.Null(actual);
        }
        Assert.Throws<InvalidDataException>(() => ParseContractRecord(
            """{"relatedRequirementId":"REQ-1","topic":"sanctions","result":"not_assessed"}"""));
    }

    [Fact]
    public void FlagsRouteMatchingFindingsToReviewWithoutClearanceOrAcceptance()
    {
        var flag = Flag("other", "specialist_review_required");
        using var result = Assess(flag, "REQ-1");
        Assert.Equal("Review", result.Record.Outcome);
        Assert.Equal("specialist review required", result.Record.Reason);
        Assert.Equal("REQ-1", Assert.Single(result.Record.Claims).RelatedRequirementId);
        Assert.Equal("specialist_review_required", result.Record.Claims.Single().SpecialistResult);
        Assert.Null(result.Record.RequestId);
        Assert.NotNull(result.Record.WorkItemId);
        Assert.Empty(result.Inbox.Requests);
        Assert.Empty(result.Inbox.Audit);
        Assert.DoesNotContain(result.Store.Read(result.Scope).Audit,
            item => item.Action is "assessment-delivered" or "mock-response.accepted");

        var response = JsonSerializer.Serialize(result.Record,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Contains("\"outcome\":\"Review\"", response, StringComparison.Ordinal);
        Assert.Contains("\"relatedRequirementId\":\"REQ-1\"", response, StringComparison.Ordinal);
        Assert.DoesNotContain("cleared", response, StringComparison.Ordinal);
        Assert.DoesNotContain("compliant", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recoverable", response, StringComparison.OrdinalIgnoreCase);

        using var notAssessed = Assess(Flag("other", "not_assessed"), "REQ-1");
        Assert.Equal("not_assessed", notAssessed.Record.Outcome);
        Assert.Equal("not_assessed", notAssessed.Record.Reason);
        Assert.Equal("not_assessed", notAssessed.Record.Claims.Single().SpecialistResult);
        Assert.NotNull(notAssessed.Record.WorkItemId);
        Assert.Empty(notAssessed.Inbox.Requests);
        Assert.Empty(notAssessed.Inbox.Audit);

        using var unrelated = Assess(flag, "REQ-2");
        Assert.Equal("Request", unrelated.Record.Outcome);
        Assert.Equal("REQ-2", unrelated.Record.Claims.Single().RelatedRequirementId);
        Assert.Single(unrelated.Inbox.Requests);
        using var caseMismatch = Assess(flag, "req-1");
        Assert.Equal("Request", caseMismatch.Record.Outcome);
        Assert.Single(caseMismatch.Inbox.Requests);

        var submission = new AssessmentClaimSubmission(
            "MissingRequirement", "return package", "Engine", "ENG-1", [], "REQ-1", "EU");
        var mapped = AssessmentEndpoints.ToClaim(submission, ClaimKind.MissingRequirement, []);
        Assert.Equal("REQ-1", mapped.RelatedRequirementId);
        Assert.Equal("EU", mapped.Regime);
    }
    [Fact]
    public void Ifrs16FlagNeverProducesAnAccountingConclusion()
    {
        using var result = Assess(Flag("ifrs16", "specialist_review_required"), "REQ-1");
        Assert.Equal("Review", result.Record.Outcome);
        Assert.Equal("specialist review required", result.Record.Reason);
        Assert.Equal("specialist_review_required", result.Record.Claims.Single().SpecialistResult);
        Assert.Empty(result.Inbox.Requests);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var assessmentOutput = JsonSerializer.Serialize(result.Record, options);
        var persisted = Assert.IsType<CaseAssessment>(
            result.Store.FindAssessment(result.Scope, result.Record.AssessmentId!));
        Assert.Equal(("Review", "specialist review required", "specialist_review_required"),
            (persisted.Outcome, persisted.Reason, Assert.Single(persisted.Claims).SpecialistResult));
        var persistedOutput = JsonSerializer.Serialize(persisted, options);
        foreach (var output in new[] { assessmentOutput, persistedOutput })
        {
            foreach (var conclusion in new[] { "lease classification", "impairment", "accounting recognition", "recognized" })
                Assert.DoesNotContain(conclusion, output, StringComparison.OrdinalIgnoreCase);
        }
        using var unrelated = Assess(Flag("ifrs16", "specialist_review_required"), "OTHER-REQ");
        Assert.Equal("Request", unrelated.Record.Outcome);
        Assert.Single(unrelated.Inbox.Requests);
    }
    [Fact]
    public void SanctionsArePerRegimeAndFlagsRouteToReview()
    {
        foreach (var activeRegime in new[] { "EU", "US", "UK" })
        {
            using var results = Assess(Flag("sanctions", "specialist_review_required", activeRegime), "REQ-1", regimes: ["EU", "US", "UK"]);
            Assert.Equal("Review", results.Record.Outcome);
            Assert.Equal(
                new[] { "EU", "US", "UK" }.Select(regime =>
                    regime == activeRegime ? "specialist_review_required" : "not_assessed"),
                results.Record.Claims.Select(claim => claim.SpecialistResult));
            Assert.Empty(results.Inbox.Requests);
            Assert.NotNull(results.Record.WorkItemId);
            if (activeRegime is "US" or "UK")
                Assert.Equal("not_assessed", results.Record.Claims[0].SpecialistResult);
        }
    }
    [Fact]
    public void ValidFixtureAndTamperingFailureAreAtomic()
    {
        var committed = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sources");
        Assert.Equal(5, SourceFixtureLoader.LoadPinned(committed).Count);

        using var key = RSA.Create(2048);
        using var directory = new TempDirectory();
        WriteFixtureSet(directory.Path, key);
        var seed = new PublishedFixtureState(
            [new StoredFixturePack("2026.09.1", "seed")],
            [new StoredSourceRecord("technical", "2026.09.1", 0, "case-001")],
            [new StoredBasisPin("technical", "2026.09.1", "seed")],
            [new StoredAuditEvent(1, "source-fixtures.published", "2026.09.1")],
            [new StoredCaseDatum("case-001", "unchanged")]);
        var store = new SourceFixtureStore(seed);
        Assert.Equal(SourceFixturePublication.Published,
            SourceFixtureLoader.LoadAndPublish(directory.Path, key.ExportSubjectPublicKeyInfo(), store));
        var before = Snapshot(store.State);

        var fixturePath = Path.Combine(directory.Path, "contracts.json");
        var changed = File.ReadAllBytes(fixturePath).Append((byte)'\n').ToArray();
        File.WriteAllBytes(fixturePath, changed);
        var manifestPath = Path.Combine(directory.Path, "contracts.manifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var updated = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(manifest.RootElement.GetRawText())!;
        updated["sha256"] = JsonSerializer.SerializeToElement(
            Convert.ToHexString(SHA256.HashData(changed)).ToLowerInvariant());
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(updated));

        Assert.Throws<SourceFixtureRefusedException>(() =>
            SourceFixtureLoader.LoadAndPublish(directory.Path, key.ExportSubjectPublicKeyInfo(), store));
        var after = Snapshot(store.State);
        Assert.Equal(before.Packs, after.Packs);
        Assert.Equal(before.Records, after.Records);
        Assert.Equal(before.Pins, after.Pins);
        Assert.Equal(before.Audits, after.Audits);
        Assert.Equal(before.Cases, after.Cases);
    }
    private sealed class AssessmentScenario(
        string path, AssessmentRecord record, MockRequestInbox inbox, CaseStateStore store, CaseScope scope) : IDisposable
    {
        public AssessmentRecord Record { get; } = record;
        public MockRequestInbox Inbox { get; } = inbox;
        public CaseStateStore Store { get; } = store;
        public CaseScope Scope { get; } = scope;
        public void Dispose()
        {
            foreach (var file in new[] { path, path + ".lock", path + ".tmp" })
                if (File.Exists(file)) File.Delete(file);
        }
    }
    private static AssessmentScenario Assess(
        SpecialistReviewFlag flag, string relatedRequirementId, string[]? regimes = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"specialist-review-{Guid.NewGuid():N}.json");
        var store = new CaseStateStore(path);
        var scope = store.CreateCase(CaseId, "presenter");
        var inbox = new MockRequestInbox();
        var assessments = new AssessmentStore(store);
        var basis = new AssessmentBasis("basis-specialist", 1, "2026-10-01T00:00:00Z",
            new ProcessingCoverage(1, 1, 0, 0), false,
            new Dictionary<string, BasisDocument>
            {
                ["doc-1"] = BasisDocument.Create("doc-1", [Quote]),
            },
            [CreateFlagPin(flag)]);
        assessments.SetCurrentBasis(CaseId, basis);
        var claims = (regimes ?? [null!]).Select(regime =>
            new AssessmentClaim(ClaimKind.MissingRequirement, "return package", "Engine", "ENG-1",
                [new DocumentWitness("doc-1", 1, Quote)], relatedRequirementId, regime)).ToArray();
        var record = new AssessmentExecutionService(store, inbox, assessments).Assess(scope, basis, claims);
        return new AssessmentScenario(path, record, inbox, store, scope);
    }
    private static SourceBasisPin CreateFlagPin(SpecialistReviewFlag flag)
    {
        var content = JsonSerializer.Serialize(new
        {
            sourceId = "contracts",
            recordId = "flag-1",
            recordVersion = 1,
            kind = "specialist_flag",
            payload = new { fields = ToFields(flag) },
        });
        return new SourceBasisPin("contracts", "flag-1", 1, content,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant());
    }
    private static SpecialistReviewFlag Flag(string topic, string result, string? regime = null) =>
        new("REQ-1", topic, result, regime);

    private static Dictionary<string, string> ToFields(SpecialistReviewFlag flag)
    {
        var fields = new Dictionary<string, string>
        {
            ["relatedRequirementId"] = flag.RelatedRequirementId,
            ["topic"] = flag.Topic,
            ["result"] = flag.Result,
        };
        if (flag.Regime is not null) fields["regime"] = flag.Regime;
        return fields;
    }

    private static Dictionary<string, string> Fields(string? related = "REQ-1", string? topic = "other",
        string? result = "not_assessed", string? regime = null, bool extra = false)
    {
        var fields = new Dictionary<string, string>();
        if (related is not null) fields["relatedRequirementId"] = related;
        if (topic is not null) fields["topic"] = topic;
        if (result is not null) fields["result"] = result;
        if (regime is not null) fields["regime"] = regime;
        if (extra) fields["unexpected"] = "value";
        return fields;
    }

    private static void ParseContractRecord(string payload)
    {
        var fixture = Encoding.UTF8.GetBytes(
            $$"""{"sourceId":"contracts","records":[{"recordId":"flag-1","recordVersion":1,"asOf":"2026-10-01","caseId":"case-001","kind":"specialist_flag","synthetic":true,"payload":{{payload}}}]}""");
        var manifest = new SourceFixtureManifest
        {
            SchemaVersion = "1",
            SourceFixtureVersion = "2026.10.1",
            ScenarioDate = "2026-10-01",
            Sha256 = Convert.ToHexString(SHA256.HashData(fixture)).ToLowerInvariant(),
            RecordCounts = Sources.ToDictionary(source => source, source => source == "contracts" ? 1 : 0),
        };
        FixtureSourceSystem<ContractsPayload>.ParseFixture(
            "contracts", "test-contracts.json", manifest, fixture, ContractsPayload.FromFields);
    }

    private static (IReadOnlyList<StoredFixturePack> Packs, IReadOnlyList<StoredSourceRecord> Records, IReadOnlyList<StoredBasisPin> Pins,
        IReadOnlyList<StoredAuditEvent> Audits, IReadOnlyList<StoredCaseDatum> Cases) Snapshot(PublishedFixtureState state) =>
        (state.Packs.ToArray(), state.SourceRecords.ToArray(), state.BasisPins.ToArray(),
            state.AuditEvents.ToArray(), state.CaseData.ToArray());

    private static void WriteFixtureSet(string directory, RSA key)
    {
        foreach (var source in Sources)
        {
            var fixture = JsonSerializer.SerializeToUtf8Bytes(new
            {
                sourceId = source,
                records = new[] { new { caseId = "case-001" } },
            });
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "1",
                sourceFixtureVersion = "2026.10.1",
                scenarioDate = "2026-10-01",
                owningCaseIds = new[] { "case-001" },
                sha256 = Convert.ToHexString(SHA256.HashData(fixture)).ToLowerInvariant(),
                recordCounts = Sources.ToDictionary(id => id, _ => 1),
            });
            File.WriteAllBytes(Path.Combine(directory, $"{source}.json"), fixture);
            File.WriteAllBytes(Path.Combine(directory, $"{source}.manifest.json"), manifest);
            File.WriteAllBytes(Path.Combine(directory, $"{source}.manifest.sig"),
                key.SignData(manifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
