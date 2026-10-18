using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Policy;
using AirlineDemo.Api.Requests;
using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Tests;

public sealed class ApplicabilityTagTests
{
    private static readonly string[] Sources = ["technical", "maintenance", "finance", "records", "contracts"];
    private const string RequirementId = "SYN-REQ-014";
    private const string Value = "pending reserve approval";
    private const string Quote = "Return readiness is pending reserve approval.";
    private static readonly ProcessingCoverage Complete = new(1, 1, 0, 0);
    private static string CommittedSources() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "sources");

    private sealed class AssessmentHarness : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"applicability-{Guid.NewGuid():N}.json");
        private readonly CaseStateStore _store;
        private readonly CaseScope _scope;
        private readonly AssessmentBasis _basis;
        private readonly AssessmentExecutionService _service;
        public MockRequestInbox Inbox { get; } = new();

        public AssessmentHarness(Dictionary<string, string> fields)
        {
            _store = new CaseStateStore(_path);
            _scope = _store.CreateCase("case-app", "owner");
            var content = JsonSerializer.Serialize(new { kind = "applicability", payload = new { fields } });
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            var pin = new SourceBasisPin("technical", "app-req-014", 1, content, digest);
            _basis = new AssessmentBasis("basis-app", 1, "2026-10-14T00:00:00Z", Complete, false,
                new Dictionary<string, BasisDocument> { ["doc"] = BasisDocument.Create("doc", [Quote]) }, [pin]);
            var assessments = new AssessmentStore();
            assessments.SetCurrentBasis("case-app", _basis);
            _service = new AssessmentExecutionService(_store, Inbox, assessments);
        }

        public AssessmentRecord Assess() => _service.Assess(_scope, _basis,
            [new AssessmentClaim(ClaimKind.MissingRequirement, Value, "Engine", "ENG-123",
                [new DocumentWitness("doc", 1, Quote)], RequirementId: RequirementId, EvidenceRegime: "EASA")]);

        public void Dispose()
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
    }

    [Fact]
    public async Task MissingRequirementClaimsRequireIdAndEvidenceRegime()
    {
        await using var h = new AssessmentEndpointsTests.Harness();
        var presenter = await AssessmentEndpointsTests.SignInAsync(h.Client, "presenter");
        var rejected = new (bool IncludeId, string? Id, bool IncludeRegime, string? Regime)[]
        {
            (false, null, true, "EASA"), (true, "", true, "EASA"), (true, "  ", true, "EASA"),
            (true, RequirementId, false, null), (true, RequirementId, true, ""),
            (true, RequirementId, true, "  "), (true, RequirementId, true, "ICAO"),
        };
        foreach (var item in rejected)
        {
            var claim = ClaimBody();
            if (item.IncludeId) claim["requirementId"] = item.Id;
            if (item.IncludeRegime) claim["evidenceRegime"] = item.Regime;
            using var response = await PostClaim(h.Client, presenter, claim);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("{\"error\":\"Assessment claim fields are invalid.\"}",
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        Assert.Empty(h.Inbox.Requests);

        foreach (var regime in new[] { "EASA", "FAA-91", "FAA-121", "Other" })
        {
            var claim = ClaimBody();
            claim["requirementId"] = RequirementId;
            claim["evidenceRegime"] = regime;
            using var response = await PostClaim(h.Client, presenter, claim);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    [Fact]
    public async Task NonMissingRequirementClaimsDoNotRequireIdOrEvidenceRegime()
    {
        await using var h = new AssessmentEndpointsTests.Harness();
        var presenter = await AssessmentEndpointsTests.SignInAsync(h.Client, "presenter");
        var claim = ClaimBody();
        claim["kind"] = "Baseline";

        using var response = await PostClaim(h.Client, presenter, claim);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Empty(h.Inbox.Requests);
    }

    [Fact]
    public void MissingTagsRouteToReviewAndNameEachMissingProperty()
    {
        var cases = new (Dictionary<string, string> Fields, string Reason)[]
        {
            (new() { ["requirementId"] = RequirementId, ["responsibleParty"] = "lessee-operator" },
                "missing applicability tags: regime"),
            (new() { ["requirementId"] = RequirementId, ["regime"] = "EASA" },
                "missing applicability tags: responsibleParty"),
            (new() { ["requirementId"] = RequirementId }, "missing applicability tags: regime, responsibleParty"),
        };
        foreach (var (fields, reason) in cases)
        {
            using var h = new AssessmentHarness(fields);
            var record = h.Assess();
            Assert.Equal("Review", record.Outcome);
            Assert.Equal(reason, record.Reason);
            Assert.Equal("needs_review", Assert.Single(record.Claims).RequirementStatus);
            Assert.Empty(h.Inbox.Requests);
        }
    }

    [Fact]
    public void TagsNeverProduceComplianceOrClearanceClaims()
    {
        using var h = new AssessmentHarness(new Dictionary<string, string>
        {
            ["requirementId"] = RequirementId,
            ["regime"] = "EASA",
            ["responsibleParty"] = "lessee-operator",
        });
        var record = h.Assess();
        var claim = Assert.Single(record.Claims);
        Assert.Equal("Request", record.Outcome);
        Assert.Equal("single supported missing requirement on current basis", record.Reason);
        Assert.Equal(RequirementId, claim.RequirementId);
        Assert.Equal("EASA", claim.EvidenceRegime);
        Assert.Equal(new RequirementApplicability("EASA", "lessee-operator"), claim.Applicability);
        Assert.Null(claim.RequirementStatus);
        var json = JsonSerializer.Serialize(record, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.DoesNotContain("compliance", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clearance", json, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> ClaimBody() => new()
    {
        ["kind"] = "MissingRequirement",
        ["value"] = Value,
        ["component"] = "Engine",
        ["serialNumber"] = "ENG-123",
        ["witnesses"] = new[] { new { documentId = "doc-17", page = 1, quote = Quote } },
    };

    private static async Task<HttpResponseMessage> PostClaim(
        HttpClient client,
        AssessmentEndpointsTests.Session session,
        Dictionary<string, object?> claim)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/cases/case-001/assessments");
        request.Headers.Add("Cookie", session.Cookie);
        request.Headers.Add("X-CSRF-Token", session.Csrf);
        request.Content = JsonContent.Create(new { claims = new[] { claim } });
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void PayloadContractAcceptsAllowedTagsAndRejectsExactInvalidCases()
    {
        const string RequirementId = "SYN-AD-2026-014";

        Assert.Empty(ApplicabilityPayloadValidator.Validate(new Dictionary<string, object?>
        {
            ["requirementId"] = RequirementId,
        }));
        foreach (var regime in new[] { "EASA", "FAA-91", "FAA-121", "Other" })
        {
            Assert.Empty(ApplicabilityPayloadValidator.Validate(new Dictionary<string, object?>
            {
                ["requirementId"] = RequirementId,
                ["regime"] = regime,
            }));
        }
        foreach (var responsibleParty in new[] { "lessee-operator", "lessor", "camo", "unassigned" })
        {
            Assert.Empty(ApplicabilityPayloadValidator.Validate(new Dictionary<string, object?>
            {
                ["requirementId"] = RequirementId,
                ["responsibleParty"] = responsibleParty,
            }));
        }

        Assert.Equal(
            ["Applicability requirementId is required."],
            ApplicabilityPayloadValidator.Validate(new Dictionary<string, object?>()));
        Assert.Equal(
            ["Applicability requirementId must be a non-empty string."],
            ApplicabilityPayloadValidator.Validate(new Dictionary<string, object?> { ["requirementId"] = "" }));
        Assert.Equal(
            ["Applicability requirementId must be a non-empty string."],
            ApplicabilityPayloadValidator.Validate(new Dictionary<string, object?> { ["requirementId"] = 14 }));
        Assert.Equal(
            ["Applicability regime must be EASA, FAA-91, FAA-121 or Other."],
            ApplicabilityPayloadValidator.Validate(new Dictionary<string, object?>
            {
                ["requirementId"] = RequirementId,
                ["regime"] = "ICAO",
            }));
        Assert.Equal(
            ["Applicability responsibleParty must be lessee-operator, lessor, camo or unassigned."],
            ApplicabilityPayloadValidator.Validate(new Dictionary<string, object?>
            {
                ["requirementId"] = RequirementId,
                ["responsibleParty"] = "operator",
            }));
        Assert.Equal(
            ["Unexpected applicability field 'clearance'."],
            ApplicabilityPayloadValidator.Validate(new Dictionary<string, object?>
            {
                ["requirementId"] = RequirementId,
                ["clearance"] = "cleared",
            }));
    }

    [Fact]
    public void UpdatedFixtureVerifiesAndTamperingWithoutDetachedSignatureFailsAtomically()
    {
        using var key = RSA.Create(2048);
        using var directory = new TemporaryDirectory();
        CopyDirectory(CommittedSources(), directory.Path);
        foreach (var source in Sources)
        {
            var manifestPath = Path.Combine(directory.Path, $"{source}.manifest.json");
            File.WriteAllBytes(
                Path.Combine(directory.Path, $"{source}.manifest.sig"),
                key.SignData(File.ReadAllBytes(manifestPath), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        }

        var store = new SourceFixtureStore(PublishedFixtureState.Empty);
        Assert.Equal(
            SourceFixturePublication.Published,
            SourceFixtureLoader.LoadAndPublish(directory.Path, key.ExportSubjectPublicKeyInfo(), store));
        var before = store.State;

        var fixturePath = Path.Combine(directory.Path, "technical.json");
        var fixture = File.ReadAllBytes(fixturePath);
        var text = Encoding.UTF8.GetString(fixture);
        var changedAt = text.IndexOf("SYN-AD-2026-014", StringComparison.Ordinal);
        Assert.True(changedAt >= 0);
        var changedCharacter = changedAt + "SYN-AD-2026-014".Length - 1;
        fixture[Encoding.UTF8.GetByteCount(text[..changedCharacter])] = (byte)'5';
        File.WriteAllBytes(fixturePath, fixture);

        var manifestPathToChange = Path.Combine(directory.Path, "technical.manifest.json");
        var manifest = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(manifestPathToChange))!;
        manifest["sha256"] = JsonSerializer.SerializeToElement(Convert.ToHexString(SHA256.HashData(fixture)).ToLowerInvariant());
        File.WriteAllBytes(manifestPathToChange, JsonSerializer.SerializeToUtf8Bytes(manifest));

        var error = Assert.Throws<SourceFixtureRefusedException>(() =>
            SourceFixtureLoader.LoadAndPublish(directory.Path, key.ExportSubjectPublicKeyInfo(), store));
        Assert.Equal("Source fixture 'technical' refused: manifest signature does not verify against the trust key.", error.Message);
        Assert.Equal(before.Packs, store.State.Packs);
        Assert.Equal(before.SourceRecords, store.State.SourceRecords);
        Assert.Equal(before.BasisPins, store.State.BasisPins);
        Assert.Equal(before.AuditEvents, store.State.AuditEvents);
        Assert.Equal(before.CaseData, store.State.CaseData);

        Assert.Equal(5, SourceFixtureLoader.LoadPinned(CommittedSources()).Count);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
