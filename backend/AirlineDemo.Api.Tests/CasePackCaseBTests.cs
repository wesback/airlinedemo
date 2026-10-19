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
    public const string MissingRequirement = "signed life-limited-parts history record";
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

    public static AssessmentClaim MissingClaim(string value = MissingRequirement, string documentId = "case-b/llp-history-gap.txt", int page = 1, string? quote = null) =>
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
        };

        foreach (var row in cases)
        {
            var candidate = fixture.DeepClone().AsObject();
            var candidateFiles = new Dictionary<string, string>(files, StringComparer.Ordinal);
            row.Change(candidate, candidateFiles);
            var errors = Validate(candidate, candidateFiles);
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

    private static IReadOnlyList<string> Validate(JsonObject fixture, IReadOnlyDictionary<string, string> files)
    {
        var errors = new List<string>();
        if (fixture["synthetic"]?.GetValue<bool>() != true || string.IsNullOrWhiteSpace(fixture["generatorReference"]?.GetValue<string>())) errors.Add("case provenance");
        if (fixture["aircraftCount"]?.GetValue<int>() != 1 || fixture["engineCount"]?.GetValue<int>() != 1) errors.Add("aircraft or engine count");
        var docs = fixture["documents"]!.AsArray();
        var paths = docs.Select(node => node!["path"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        if (docs.Count is < 8 or > 12) errors.Add("document count");
        if (!paths.SetEquals(files.Keys)) errors.Add("document paths");
        foreach (var doc in docs)
            if (doc!["synthetic"]?.GetValue<bool>() != true || string.IsNullOrWhiteSpace(doc["generatorReference"]?.GetValue<string>())) errors.Add("document provenance");
        var components = fixture["components"]!.AsArray();
        if (components.Count != 2) errors.Add("component count");
        foreach (var component in components)
        {
            var mapping = component!["evidenceDocumentPaths"]!.AsArray();
            if (mapping.Count == 0 || mapping.Any(node => !paths.Contains(node!.GetValue<string>()))) errors.Add("component mapping");
        }
        return errors;
    }

    private static void Resize(JsonObject fixture, Dictionary<string, string> files, int count)
    {
        var docs = fixture["documents"]!.AsArray();
        while (docs.Count > count)
        {
            var path = docs[^1]!["path"]!.GetValue<string>();
            foreach (var component in fixture["components"]!.AsArray())
            {
                var mapping = component!["evidenceDocumentPaths"]!.AsArray();
                for (var i = mapping.Count - 1; i >= 0; i--)
                    if (mapping[i]!.GetValue<string>() == path) mapping.RemoveAt(i);
            }
            files.Remove(path);
            docs.RemoveAt(docs.Count - 1);
        }
        while (docs.Count < count)
        {
            var path = $"case-b/boundary-{docs.Count + 1}.txt";
            docs.Add(new JsonObject { ["path"] = path, ["synthetic"] = true, ["generatorReference"] = "boundary-test-generator" });
            files.Add(path, "Synthetic boundary test text.");
        }
    }
}
