using System.Runtime.CompilerServices;
<<<<<<< HEAD
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Evidence;
=======
using System.Text.Json.Nodes;
>>>>>>> origin/main
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Policy;
using AirlineDemo.Api.Requests;

namespace AirlineDemo.Api.Tests;

<<<<<<< HEAD
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
=======
public sealed class CasePackCaseBTests
{
    private const string CaseId = "cp-case-b";
    private const string BasisId = "cp-case-b-basis";
    private const string ScenarioTime = "2026-10-14T00:00:00Z";

    [Fact]
    public void AC003_CaseBFixtureValidatesShapeAndProvenance()
    {
        var root = FixtureRoot();
        var fixturePath = Path.Combine(root, "case-b.json");
        Assert.True(File.Exists(fixturePath), $"Case B fixture is missing: {fixturePath}");
        var fixture = JsonNode.Parse(File.ReadAllText(fixturePath))!.AsObject();
        var files = ReadFixtureFiles(fixture, root);
        var cases = new (string Name, Action<JsonObject, Dictionary<string, string>> Change, bool Valid)[]
        {
            ("fixture contract", (_, _) => { }, true),
            ("seven documents", (json, contents) => RemoveDocuments(json, contents, 7), false),
            ("thirteen documents", (json, contents) => AddDocuments(json, contents, 13), false),
            ("missing case synthetic marker", (json, _) => json.Remove("synthetic"), false),
            ("false case synthetic marker", (json, _) => json["synthetic"] = false, false),
            ("missing document synthetic marker", (json, _) => Documents(json)[0]!.AsObject().Remove("synthetic"), false),
            ("false document synthetic marker", (json, _) => Documents(json)[0]!["synthetic"] = false, false),
            ("blank case generator reference", (json, _) => json["generatorReference"] = " ", false),
            ("blank document generator reference", (json, _) => Documents(json)[0]!["generatorReference"] = "", false),
            ("mapping to Case A document", (json, _) =>
                Components(json)[0]!["evidenceDocumentPaths"]![0] = "case-a/aircraft-summary.txt", false),
>>>>>>> origin/main
        };

        foreach (var row in cases)
        {
            var candidate = fixture.DeepClone().AsObject();
            var candidateFiles = new Dictionary<string, string>(files, StringComparer.Ordinal);
            row.Change(candidate, candidateFiles);
<<<<<<< HEAD
            var errors = Validate(candidate, candidateFiles);
=======
            var errors = ValidateFixture(candidate, candidateFiles);
>>>>>>> origin/main
            Assert.True(row.Valid == (errors.Count == 0), $"{row.Name}: {string.Join("; ", errors)}");
        }
    }

    [Fact]
<<<<<<< HEAD
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
=======
    public void AC005_CaseBConflictRoutesToReviewWithoutRequest()
    {
        var root = FixtureRoot();
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "case-b.json")))!.AsObject();
        var evidence = ReadFixtureFiles(fixture, root);
        var statePath = Path.Combine(Path.GetTempPath(), $"case-pack-case-b-{Guid.NewGuid():N}.json");

        try
        {
            var caseStore = new CaseStateStore(statePath);
            var scope = caseStore.CreateCase(CaseId, "presenter");
            caseStore.Commit(scope, 0, new CaseCommit(
                CaseStateStore.CreateBasisVersion(1, string.Join('\n', evidence.Values), string.Empty),
                null, "presenter", "submit-evidence", "synthetic Case B evidence basis"));

            var documents = evidence.ToDictionary(
                pair => pair.Key,
                pair => BasisDocument.Create(pair.Key, [pair.Value]),
                StringComparer.Ordinal);
            var basis = new AssessmentBasis(
                BasisId,
                1,
                ScenarioTime,
                new ProcessingCoverage(documents.Count, documents.Count, 0, 0),
                true,
                documents);
            var inbox = new MockRequestInbox();
            var assessments = new AssessmentStore(caseStore);
            assessments.SetCurrentBasis(CaseId, basis);
            var service = new AssessmentExecutionService(caseStore, inbox, assessments);
            var conflictQuote = evidence["case-b/identity-conflict.txt"].Trim();
            var conflict = new AssessmentClaim(
                ClaimKind.Conflict,
                "ENG-CP-B-001 and ENG-CP-B-009",
                "Engine",
                "ENG-CP-B-001",
                [new DocumentWitness("case-b/identity-conflict.txt", 1, conflictQuote)]);

            var record = service.Assess(scope, basis, [conflict]);

            Assert.Equal("Review", record.Outcome);
            Assert.Equal("ambiguous identity", record.Reason);
            Assert.Null(record.RequestId);
            Assert.NotNull(record.WorkItemId);
            var reviewItem = Assert.Single(caseStore.ListWorkItems(scope));
            Assert.Equal(record.WorkItemId, reviewItem.ItemId);
            Assert.Equal(AssessmentExecutionService.ReviewCapability, reviewItem.CapabilityId);
            Assert.Equal("open", reviewItem.State);
            Assert.Equal(AssessmentExecutionService.ReviewOwner, reviewItem.Owner);
            AssertNoRequests(inbox);
            Assert.Equal("Review", Assert.Single(caseStore.ListAssessments(scope)).Outcome);

            var injectedInbox = new MockRequestInbox();
            var injectedEnvelope = new DeliveryEnvelope(
                $"{CaseId}:{BasisId}:1:assessment",
                CaseId,
                "assessment-1",
                BasisId,
                1,
                ScenarioTime,
                conflict.Component,
                conflict.SerialNumber,
                "conflicting engine identity");
            injectedInbox.RecordPrepared(
                injectedEnvelope,
                new DeliveryReceipt(
                    injectedEnvelope.EventId,
                    "REQ-INJECTED",
                    CaseId,
                    injectedEnvelope.ReportId,
                    BasisId,
                    1,
                    "1",
                    "Injected request for the ambiguous engine identity."));
            Assert.Single(injectedInbox.Requests);
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoRequests(injectedInbox));
        }
        finally
        {
            foreach (var path in new[] { statePath, statePath + ".lock", statePath + ".tmp" })
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    private static void AssertNoRequests(MockRequestInbox inbox) => Assert.Empty(inbox.Requests);

    private static string FixtureRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "AirlineDemo.Api", "Fixtures", "aircraft-case-pack"));

    private static Dictionary<string, string> ReadFixtureFiles(JsonObject fixture, string root)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var document in Documents(fixture))
        {
            var path = document!["path"]!.GetValue<string>();
            var fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath), $"Case B document is missing: {path}");
            files.Add(path, File.ReadAllText(fullPath));
        }

        return files;
    }

    private static List<string> ValidateFixture(JsonObject fixture, IReadOnlyDictionary<string, string> files)
    {
        var errors = new List<string>();
        if (fixture["caseId"]?.GetValue<string>() != CaseId) errors.Add("caseId");
        if (fixture["synthetic"]?.GetValue<bool>() != true) errors.Add("synthetic");
        if (string.IsNullOrWhiteSpace(fixture["generatorReference"]?.GetValue<string>())) errors.Add("generatorReference");
        if (fixture["aircraftCount"]?.GetValue<int>() != 1) errors.Add("aircraftCount");
        if (fixture["engineCount"]?.GetValue<int>() != 1) errors.Add("engineCount");

        var documentNodes = Documents(fixture);
        if (documentNodes.Count != 8) errors.Add("documents count");
        var documentPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in documentNodes)
        {
            if (node is not JsonObject document) { errors.Add("document object"); continue; }
            var path = document["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path) || !documentPaths.Add(path)) errors.Add("document path");
            if (document["synthetic"]?.GetValue<bool>() != true) errors.Add("document synthetic");
            if (string.IsNullOrWhiteSpace(document["generatorReference"]?.GetValue<string>())) errors.Add("document generatorReference");
            if (string.IsNullOrWhiteSpace(path) || !files.TryGetValue(path, out var content) || string.IsNullOrWhiteSpace(content))
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
                var path = mapped?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(path) || !documentPaths.Contains(path) || !path.StartsWith("case-b/", StringComparison.Ordinal))
                    errors.Add("evidence mapping");
            }
        }

        return errors;
    }

    private static JsonArray Documents(JsonObject fixture) => fixture["documents"]!.AsArray();
    private static JsonArray Components(JsonObject fixture) => fixture["components"]!.AsArray();

    private static void RemoveDocuments(JsonObject fixture, Dictionary<string, string> files, int count)
    {
        var documents = Documents(fixture);
        while (documents.Count > count)
        {
            var last = documents[^1]!;
            var path = last["path"]!.GetValue<string>();
            files.Remove(path);
            foreach (var component in Components(fixture))
            {
                var mapping = component!["evidenceDocumentPaths"]!.AsArray();
                for (var index = mapping.Count - 1; index >= 0; index--)
                    if (mapping[index]!.GetValue<string>() == path) mapping.RemoveAt(index);
            }
            documents.RemoveAt(documents.Count - 1);
        }
    }

    private static void AddDocuments(JsonObject fixture, Dictionary<string, string> files, int count)
    {
        var documents = Documents(fixture);
        while (documents.Count < count)
        {
            var path = $"case-b/boundary-document-{documents.Count + 1}.txt";
            documents.Add(new JsonObject
            {
                ["path"] = path,
                ["synthetic"] = true,
                ["generatorReference"] = "boundary-test-generator",
            });
>>>>>>> origin/main
            files.Add(path, "Synthetic boundary test text.");
        }
    }
}
