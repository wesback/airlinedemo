using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Policy;
using AirlineDemo.Api.Requests;

namespace AirlineDemo.Api.Tests;

internal static class CasePackFixture
{
    public const string CaseId = "cp-case-b";
    public const string BasisId = "cp-case-b-basis";
    public const string RequirementId = "SYN-REQ-CP-B-001";
    public const string MissingRequirement = "The records do not establish which serial identifies the installed engine.";
    public const string ScenarioTime = "2026-10-14T00:00:00Z";

    public static string Root([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "AirlineDemo.Api", "Fixtures", "aircraft-case-pack"));

    public static JsonObject ReadFixture() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "case-b.json")))!.AsObject();

    public static Dictionary<string, string> ReadDocuments(JsonObject fixture)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in fixture["documents"]!.AsArray())
        {
            var path = node!["path"]!.GetValue<string>();
            files.Add(path, File.ReadAllText(Path.Combine(Root(), path.Replace('/', Path.DirectorySeparatorChar))));
        }

        return files;
    }

    public static AssessmentBasis Basis(
        IReadOnlyDictionary<string, string> files,
        bool identityAmbiguous = false,
        ProcessingCoverage? coverage = null,
        string basisId = BasisId,
        int version = 1)
    {
        var documents = files.ToDictionary(pair => pair.Key, pair => BasisDocument.Create(pair.Key, [pair.Value]), StringComparer.Ordinal);
        var pinContent = JsonSerializer.Serialize(new
        {
            kind = "applicability",
            payload = new { fields = new Dictionary<string, string> { ["requirementId"] = RequirementId, ["regime"] = "EASA", ["responsibleParty"] = "lessee-operator" } },
        });
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pinContent))).ToLowerInvariant();
        var pin = new SourceBasisPin("technical", "app-" + RequirementId, 1, pinContent, digest);
        return new AssessmentBasis(basisId, version, ScenarioTime,
            coverage ?? new ProcessingCoverage(documents.Count, documents.Count, 0, 0), identityAmbiguous, documents, [pin]);
    }

    public static AssessmentClaim MissingClaim(string value = MissingRequirement, string documentId = "case-b/engine-history-index.txt", int page = 1, string? quote = null) =>
        new(ClaimKind.MissingRequirement, value, "Engine", "ENG-CP-B-001",
            [new DocumentWitness(documentId, page, quote ?? File.ReadAllText(Path.Combine(Root(), documentId)).Trim())],
            RequirementId: RequirementId, EvidenceRegime: "EASA");
}

public sealed class CasePackCaseBTests
{
    [Fact]
    public void AC003_CaseBFixtureMatchesPackContract()
    {
        var fixture = CasePackFixture.ReadFixture();
        Assert.Equal("cp-case-b", fixture["caseId"]!.GetValue<string>());
        var files = CasePackFixture.ReadDocuments(fixture);
        var cases = new (string Name, Action<JsonObject, Dictionary<string, string>> Change, bool Valid)[]
        {
            ("fixture", (_, _) => { }, true),
            ("eight documents", (json, content) => Resize(json, content, 8), true),
            ("twelve documents", (json, content) => Resize(json, content, 12), true),
            ("seven documents", (json, content) => Resize(json, content, 7), false),
            ("thirteen documents", (json, content) => Resize(json, content, 13), false),
            ("empty component mapping", (json, _) => json["components"]![0]!["evidenceDocumentPaths"] = new JsonArray(), false),
            ("mapping to Case A document", (json, _) => json["components"]![0]!["evidenceDocumentPaths"]![0] = "case-a/llp-history-gap.txt", false),
            ("missing case synthetic marker", (json, _) => json.Remove("synthetic"), false),
            ("false case synthetic marker", (json, _) => json["synthetic"] = false, false),
            ("missing document synthetic marker", (json, _) => Documents(json)[0]!.AsObject().Remove("synthetic"), false),
            ("false document synthetic marker", (json, _) => Documents(json)[0]!["synthetic"] = false, false),
            ("blank case generator reference", (json, _) => json["generatorReference"] = " ", false),
            ("blank document generator reference", (json, _) => Documents(json)[0]!["generatorReference"] = "", false),
        };

        foreach (var row in cases)
        {
            var candidate = fixture.DeepClone().AsObject();
            var candidateFiles = new Dictionary<string, string>(files, StringComparer.Ordinal);
            row.Change(candidate, candidateFiles);
            var errors = ValidateFixture(candidate, candidateFiles);
            Assert.True(row.Valid == (errors.Count == 0), $"{row.Name}: {string.Join("; ", errors)}");
        }
    }

    [Fact]
    public void AC005_AmbiguousIdentityRoutesOnlyToReview()
    {
        var path = Path.Combine(Path.GetTempPath(), $"case-pack-case-b-{Guid.NewGuid():N}.json");
        try
        {
            var state = new CaseStateStore(path);
            var scope = state.CreateCase(CasePackFixture.CaseId, "presenter");
            var files = CasePackFixture.ReadDocuments(CasePackFixture.ReadFixture());
            var persistedBasis = CaseStateStore.CreateBasisVersion(1, string.Join('\n', files.Values), string.Empty);
            state.Commit(scope, 0, new CaseCommit(persistedBasis, null, "presenter", "submit-evidence", "synthetic Case B evidence basis"));
            var basis = CasePackFixture.Basis(files, identityAmbiguous: true);
            var inbox = new MockRequestInbox();
            var assessments = new AssessmentStore(state);
            assessments.SetCurrentBasis(CasePackFixture.CaseId, basis);
            var service = new AssessmentExecutionService(state, inbox, assessments);
            var claim = CasePackFixture.MissingClaim();

            var record = service.Assess(scope, basis, [claim]);

            Assert.Equal("Review", record.Outcome);
            Assert.Equal(CasePackFixture.BasisId, record.BasisId);
            Assert.Equal(1, record.BasisVersion);
            Assert.Null(record.RequestId);
            Assert.Equal("technical-reviewer", Assert.Single(state.ListWorkItems(scope)).Owner);
            Assert.Empty(inbox.Requests);
            Assert.Empty(state.ListRequestReceipts(scope));

            var validated = ClaimValidator.Validate(new InvestigationClaim(claim.Value, claim.Witnesses),
                claim.Kind, basis.Documents, claim.Value);
            Assert.Equal(ClaimStatus.Supported, validated.Status);
            var attempt = inbox.Submit(
                new DeliveryEnvelope("case-b-attempt", CasePackFixture.CaseId, "case-b-attempt", basis.BasisId,
                    basis.BasisVersion, basis.ScenarioTime, claim.Component, claim.SerialNumber, claim.Value),
                new DeliveryFinding(basis.Coverage, [validated], basis.IdentityAmbiguous, basis.BasisId, basis.BasisVersion),
                basis.ScenarioTime);
            Assert.Equal(DeliveryOutcome.Review, attempt.Outcome);
            Assert.Equal("ambiguous identity", attempt.Reason);
            Assert.Empty(inbox.Requests);
            Assert.Empty(state.ListRequestReceipts(scope));
            Assert.Equal(["Claims"], typeof(AssessmentSubmission).GetProperties().Select(property => property.Name));
        }
        finally
        {
            foreach (var file in new[] { path, path + ".lock", path + ".tmp" })
                if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void CaseB_ConflictingEngineIdentityRoutesToReview()
    {
        var path = Path.Combine(Path.GetTempPath(), $"case-pack-case-b-conflict-{Guid.NewGuid():N}.json");
        try
        {
            var state = new CaseStateStore(path);
            var scope = state.CreateCase(CasePackFixture.CaseId, "presenter");
            var evidence = CasePackFixture.ReadDocuments(CasePackFixture.ReadFixture());
            var persistedBasis = CaseStateStore.CreateBasisVersion(1, string.Join('\n', evidence.Values), string.Empty);
            state.Commit(scope, 0, new CaseCommit(persistedBasis, null, "presenter", "submit-evidence", "synthetic Case B evidence basis"));
            var basis = CasePackFixture.Basis(evidence, identityAmbiguous: true);
            var inbox = new MockRequestInbox();
            var assessments = new AssessmentStore(state);
            assessments.SetCurrentBasis(CasePackFixture.CaseId, basis);
            var service = new AssessmentExecutionService(state, inbox, assessments);
            var quote = evidence["case-b/identity-conflict.txt"].Trim();
            var conflict = new AssessmentClaim(
                ClaimKind.Conflict,
                "ENG-CP-B-001 and ENG-CP-B-009",
                "Engine",
                "ENG-CP-B-001",
                [new DocumentWitness("case-b/identity-conflict.txt", 1, quote)]);

            var record = service.Assess(scope, basis, [conflict]);

            Assert.Equal("Review", record.Outcome);
            Assert.Equal("ambiguous identity", record.Reason);
            Assert.Null(record.RequestId);
            Assert.NotNull(record.WorkItemId);
            var reviewItem = Assert.Single(state.ListWorkItems(scope));
            Assert.Equal(record.WorkItemId, reviewItem.ItemId);
            Assert.Equal(AssessmentExecutionService.ReviewCapability, reviewItem.CapabilityId);
            Assert.Equal("open", reviewItem.State);
            Assert.Equal(AssessmentExecutionService.ReviewOwner, reviewItem.Owner);
            Assert.Empty(inbox.Requests);
            Assert.Empty(state.ListRequestReceipts(scope));
            Assert.Equal("Review", Assert.Single(state.ListAssessments(scope)).Outcome);
        }
        finally
        {
            foreach (var file in new[] { path, path + ".lock", path + ".tmp" })
                if (File.Exists(file)) File.Delete(file);
        }
    }

    private static List<string> ValidateFixture(JsonObject fixture, IReadOnlyDictionary<string, string> files)
    {
        var errors = new List<string>();
        if (fixture["caseId"]?.GetValue<string>() != CasePackFixture.CaseId) errors.Add("caseId");
        if (fixture["synthetic"]?.GetValue<bool>() != true) errors.Add("synthetic");
        if (string.IsNullOrWhiteSpace(fixture["generatorReference"]?.GetValue<string>())) errors.Add("generatorReference");
        if (fixture["aircraftCount"]?.GetValue<int>() != 1) errors.Add("aircraftCount");
        if (fixture["engineCount"]?.GetValue<int>() != 1) errors.Add("engineCount");

        var documentNodes = Documents(fixture);
        if (documentNodes.Count is < 8 or > 12) errors.Add("documents count");
        var documentPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in documentNodes)
        {
            if (node is not JsonObject document) { errors.Add("document object"); continue; }
            var documentPath = document["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(documentPath) || !documentPaths.Add(documentPath)) errors.Add("document path");
            if (string.IsNullOrWhiteSpace(documentPath) || !documentPath.StartsWith("case-b/", StringComparison.Ordinal)) errors.Add("document scope");
            if (document["synthetic"]?.GetValue<bool>() != true) errors.Add("document synthetic");
            if (string.IsNullOrWhiteSpace(document["generatorReference"]?.GetValue<string>())) errors.Add("document generatorReference");
            if (string.IsNullOrWhiteSpace(documentPath) || !files.TryGetValue(documentPath, out var content) || string.IsNullOrWhiteSpace(content))
                errors.Add("document text");
        }

        if (!documentPaths.SetEquals(files.Keys)) errors.Add("document/file paths");
        var componentNodes = Components(fixture);
        if (componentNodes.Count != 2) errors.Add("components count");
        foreach (var node in componentNodes)
        {
            if (node is not JsonObject component) { errors.Add("component object"); continue; }
            if (string.IsNullOrWhiteSpace(component["componentId"]?.GetValue<string>())) errors.Add("componentId");
            if (component["evidenceDocumentPaths"] is not JsonArray mapping || mapping.Count == 0)
            {
                errors.Add("evidenceDocumentPaths");
                continue;
            }

            foreach (var mapped in mapping)
            {
                var documentPath = mapped?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(documentPath) || !documentPaths.Contains(documentPath) ||
                    !documentPath.StartsWith("case-b/", StringComparison.Ordinal))
                    errors.Add("evidence mapping");
            }
        }

        return errors;
    }

    private static JsonArray Documents(JsonObject fixture) => fixture["documents"]!.AsArray();
    private static JsonArray Components(JsonObject fixture) => fixture["components"]!.AsArray();

    private static void Resize(JsonObject fixture, Dictionary<string, string> files, int count)
    {
        var documents = Documents(fixture);
        while (documents.Count > count)
        {
            var path = documents[^1]!["path"]!.GetValue<string>();
            files.Remove(path);
            foreach (var component in Components(fixture))
            {
                var mapping = component!["evidenceDocumentPaths"]!.AsArray();
                for (var index = mapping.Count - 1; index >= 0; index--)
                    if (mapping[index]!.GetValue<string>() == path) mapping.RemoveAt(index);
            }
            documents.RemoveAt(documents.Count - 1);
        }
        while (documents.Count < count)
        {
            var path = $"case-b/boundary-document-{documents.Count + 1}.txt";
            documents.Add(new JsonObject
            {
                ["path"] = path,
                ["synthetic"] = true,
                ["generatorReference"] = "boundary-test-generator",
            });
            files.Add(path, "Synthetic boundary test text.");
        }
    }
}
