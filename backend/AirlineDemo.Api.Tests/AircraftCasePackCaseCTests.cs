using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Fixtures;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Policy;
using AirlineDemo.Api.Requests;

namespace AirlineDemo.Api.Tests;

public sealed class AircraftCasePackCaseCTests
{
    private const string CaseId = "cp-case-c";
    private const string BasisId = "cp-case-c-basis";
    private const string ScenarioTime = "2026-10-14T00:00:00Z";
    private const string GeneratorReference = "aircraft-case-pack-generator-v1";
    private const string BlockedPath = "case-c/blocked-document.txt";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private sealed record ScenarioDocument(string Path, string Status, bool Retryable, bool UsableAsEvidence, string Content);

    [Fact]
    public void AC003_CaseCFixtureValidatesShapeAndProvenance()
    {
        var root = FixtureRoot();
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "case-c.json")))!.AsObject();
        var manifestJson = File.ReadAllText(Path.Combine(root, "manifest.json"));
        var manifest = AircraftCasePackManifest.Parse(manifestJson, ReadPackFiles(root,
            JsonSerializer.Deserialize<AircraftCasePackManifest>(manifestJson, WebJson)!));
        Assert.NotNull(manifest.Manifest);
        Assert.Equal(JsonSerializer.Serialize(manifest.Manifest.Cases.Single(c => c.CaseId == CaseId), WebJson),
            JsonSerializer.Serialize(JsonSerializer.Deserialize<AircraftCasePackCase>(fixture.ToJsonString(), WebJson), WebJson));
        var original = manifest.Manifest;
        var files = ReadPackFiles(root, original);

        var rows = new (string Name, Action<JsonObject, Dictionary<string, byte[]>> Change, bool Valid)[]
        {
            ("valid eight-document fixture", (_, _) => { }, true),
            ("seven documents", (json, contents) => RemoveDocuments(json, contents, 7), false),
            ("thirteen documents", (json, contents) => AddDocuments(json, contents, 13), false),
            ("missing case synthetic marker", (json, _) => json.Remove("synthetic"), false),
            ("false case synthetic marker", (json, _) => json["synthetic"] = false, false),
            ("missing document synthetic marker", (json, _) => Documents(json)[0]!.AsObject().Remove("synthetic"), false),
            ("false document synthetic marker", (json, _) => Documents(json)[0]!["synthetic"] = false, false),
            ("empty case generator reference", (json, _) => json["generatorReference"] = "", false),
            ("empty document generator reference", (json, _) => Documents(json)[0]!["generatorReference"] = " ", false),
            ("mapping to a Case A document", (json, _) =>
                Components(json)[0]!["evidenceDocumentPaths"]![0] = "case-a/aircraft-summary.txt", false),
            ("mapping to a Case B document", (json, _) =>
                Components(json)[0]!["evidenceDocumentPaths"]![0] = "case-b/aircraft-summary.txt", false),
        };

        foreach (var row in rows)
        {
            var candidate = fixture.DeepClone().AsObject();
            var candidateFiles = new Dictionary<string, byte[]>(files, StringComparer.Ordinal);
            row.Change(candidate, candidateFiles);
            var result = ValidateFixture(original, candidate, candidateFiles);
            Assert.Equal(row.Valid, result.Manifest is not null);
            Assert.Equal(row.Valid, result.Errors.Count == 0);
        }
    }

    [Fact]
    public void AC006_CaseCBaselineAndBlockedVariantRemainDistinct()
    {
        var root = FixtureRoot();
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "case-c.json")))!.AsObject();
        var baselineDocuments = LoadDocuments(fixture, root, blocked: false);
        var blockedDocuments = LoadDocuments(fixture, root, blocked: true);
        Assert.Empty(ValidateScenario(baselineDocuments, blockedVariant: false));
        Assert.Empty(ValidateScenario(blockedDocuments, blockedVariant: true));
        var blocked = Assert.Single(blockedDocuments, document => document.Path == BlockedPath);
        Assert.Equal("rejected-retryable", blocked.Status);
        Assert.True(blocked.Retryable);
        Assert.False(blocked.UsableAsEvidence);
        Assert.NotEmpty(ValidateScenario(blockedDocuments.Select(document => document.Path == BlockedPath
            ? document with { UsableAsEvidence = true }
            : document).ToArray(), blockedVariant: true));

        var statePath = Path.Combine(Path.GetTempPath(), $"case-pack-case-c-{Guid.NewGuid():N}.json");
        try
        {
            var store = new CaseStateStore(statePath);
            var scope = store.CreateCase(CaseId, "presenter");
            store.Commit(scope, 0, new CaseCommit(
                CaseStateStore.CreateBasisVersion(1, string.Join('\n', baselineDocuments.Select(document => document.Content)), ""),
                null, "presenter", "submit-evidence", "synthetic Case C evidence basis"));
            var inbox = new MockRequestInbox();
            var assessments = new AssessmentStore(store);
            var service = new AssessmentExecutionService(store, inbox, assessments);
            var baseline = CreateBasis(BasisId, baselineDocuments);
            assessments.SetCurrentBasis(CaseId, baseline);
            var serialText = baselineDocuments.Single(document => document.Path == "case-c/engine-serial-record.txt").Content;
            var claim = new AssessmentClaim(ClaimKind.Baseline, "ENG-CP-C-001", "Engine", "ENG-CP-C-001",
                [new DocumentWitness("case-c/engine-serial-record.txt", 1, serialText)]);

            var complete = service.Assess(scope, baseline, [claim]);
            Assert.Equal("Baseline", complete.Outcome);
            Assert.Null(complete.RequestId);
            Assert.Null(complete.WorkItemId);
            Assert.Empty(inbox.Requests);
            Assert.Empty(inbox.Audit);

            var blockedBasis = CreateBasis($"{BasisId}-blocked", blockedDocuments);
            assessments.SetCurrentBasis(CaseId, blockedBasis);
            var result = service.Assess(scope, blockedBasis, [claim]);
            Assert.Equal("Review", result.Outcome);
            Assert.Null(result.RequestId);
            Assert.Empty(inbox.Requests);
            Assert.Empty(inbox.Audit);
            Assert.DoesNotContain(BlockedPath, result.Claims.SelectMany(item => item.Witnesses).Select(witness => witness.DocumentId));
            Assert.DoesNotContain(result.Claims, item => item.Kind == ClaimKind.MissingRequirement.ToString()
                && item.Witnesses.Any(witness => witness.DocumentId == BlockedPath));
        }
        finally
        {
            foreach (var path in new[] { statePath, statePath + ".lock", statePath + ".tmp" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    private static AircraftCasePackManifest.ParseResult ValidateFixture(
        AircraftCasePackManifest original, JsonObject fixture, Dictionary<string, byte[]> files)
    {
        var caseRecord = JsonSerializer.Deserialize<AircraftCasePackCase>(fixture.ToJsonString(), WebJson)!;
        var cases = original.Cases.Select(item => item.CaseId == CaseId ? caseRecord : item).ToList();
        var candidate = original with { Cases = cases, Files = files.Keys.Order(StringComparer.Ordinal).ToList() };
        candidate = candidate with { Sha256 = AircraftCasePackManifest.ComputeSha256(candidate, files) };
        return AircraftCasePackManifest.Parse(JsonSerializer.Serialize(candidate, WebJson), files);
    }

    private static Dictionary<string, byte[]> ReadPackFiles(string root, AircraftCasePackManifest manifest) =>
        manifest.Files.ToDictionary(path => path, path => File.ReadAllBytes(
            Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))), StringComparer.Ordinal);

    private static List<ScenarioDocument> LoadDocuments(JsonObject fixture, string root, bool blocked) =>
        Documents(fixture).Select(node =>
        {
            var path = node!["path"]!.GetValue<string>();
            var isBlocked = blocked && path == BlockedPath;
            return new ScenarioDocument(path, isBlocked ? "rejected-retryable" : "accepted", isBlocked, !isBlocked,
                File.ReadAllText(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))));
        }).ToList();

    private static IReadOnlyList<string> ValidateScenario(
        IReadOnlyCollection<ScenarioDocument> documents, bool blockedVariant)
    {
        var errors = new List<string>();
        if (documents.Count != 8) errors.Add("document count");
        var blocked = documents.Where(document => document.Status == "rejected-retryable").ToArray();
        if (blocked.Length != (blockedVariant ? 1 : 0) ||
            (blockedVariant && (blocked[0].Path != BlockedPath || !blocked[0].Retryable || blocked[0].UsableAsEvidence)))
            errors.Add("blocked document state");
        if (documents.Any(document => document.Path != BlockedPath &&
            (document.Status != "accepted" || document.Retryable || !document.UsableAsEvidence)))
            errors.Add("baseline document state");
        if (!blockedVariant && documents.Any(document => document.Status != "accepted" ||
            document.Retryable || !document.UsableAsEvidence))
            errors.Add("complete baseline state");
        return errors;
    }

    private static AssessmentBasis CreateBasis(string basisId, IReadOnlyCollection<ScenarioDocument> documents)
    {
        var usable = documents.Where(document => document.UsableAsEvidence).ToArray();
        return new AssessmentBasis(basisId, 1, ScenarioTime,
            new ProcessingCoverage(documents.Count, usable.Length, documents.Count - usable.Length, 0), false,
            usable.ToDictionary(document => document.Path,
                document => BasisDocument.Create(document.Path, [document.Content]), StringComparer.Ordinal));
    }

    private static void RemoveDocuments(JsonObject fixture, Dictionary<string, byte[]> files, int count)
    {
        var documents = Documents(fixture);
        while (documents.Count > count)
        {
            var path = documents[^1]!["path"]!.GetValue<string>();
            files.Remove(path);
            foreach (var component in Components(fixture))
            {
                var mapping = component!["evidenceDocumentPaths"]!.AsArray();
                for (var i = mapping.Count - 1; i >= 0; i--)
                    if (mapping[i]!.GetValue<string>() == path) mapping.RemoveAt(i);
            }
            documents.RemoveAt(documents.Count - 1);
        }
    }

    private static void AddDocuments(JsonObject fixture, Dictionary<string, byte[]> files, int count)
    {
        var documents = Documents(fixture);
        while (documents.Count < count)
        {
            var path = $"case-c/boundary-document-{documents.Count + 1}.txt";
            documents.Add(new JsonObject { ["path"] = path, ["synthetic"] = true, ["generatorReference"] = GeneratorReference });
            files.Add(path, "Synthetic boundary test text."u8.ToArray());
        }
    }

    private static JsonArray Documents(JsonObject fixture) => fixture["documents"]!.AsArray();
    private static JsonArray Components(JsonObject fixture) => fixture["components"]!.AsArray();
    private static string FixtureRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "AirlineDemo.Api", "Fixtures", "aircraft-case-pack"));
}
