using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Policy;
using AirlineDemo.Api.Requests;

namespace AirlineDemo.Api.Tests;

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
            files.Add(path, "Synthetic boundary test text.");
        }
    }
}
