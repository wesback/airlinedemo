using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Fixtures;

namespace AirlineDemo.Api.Tests;

public sealed class AircraftCasePackManifestTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static (AircraftCasePackManifest Manifest, Dictionary<string, byte[]> Files) Pack(string version = "1.0.0", string date = "2026-02-28", int caseCount = 3, int[]? counts = null, string text = "fixture")
    {
        counts ??= Enumerable.Repeat(9, caseCount).ToArray();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var cases = Enumerable.Range(0, caseCount).Select(i =>
        {
            var id = $"case-{i + 1}";
            var docs = Enumerable.Range(0, counts[i]).Select(j =>
            {
                var path = $"{id}/doc-{j + 1}.txt";
                files.Add(path, Encoding.UTF8.GetBytes(text));
                return new AircraftCasePackDocument(path, true, "generator-v1");
            }).ToList();
            return new AircraftCasePackCase(id, true, "generator-v1", 1, 1,
                [new($"{id}-component-1", [docs[0].Path]), new($"{id}-component-2", [docs[1].Path])], docs);
        }).ToList();
        var manifest = new AircraftCasePackManifest
        {
            SchemaVersion = 1, PackVersion = version, ScenarioDate = date, ProductOwnerReviewReference = "review-123",
            Cases = cases, Files = files.Keys.Order(StringComparer.Ordinal).ToList()
        };
        return (manifest with { Sha256 = AircraftCasePackManifest.ComputeSha256(manifest, files) }, files);
    }
    private static JsonObject Json((AircraftCasePackManifest Manifest, Dictionary<string, byte[]> Files) pack) =>
        JsonNode.Parse(JsonSerializer.Serialize(pack.Manifest, WebJson))!.AsObject();
    private static string FixtureRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "AirlineDemo.Api", "Fixtures", "aircraft-case-pack"));

    private static void RefreshHash(JsonObject json, IReadOnlyDictionary<string, byte[]> files)
    {
        var manifest = JsonSerializer.Deserialize<AircraftCasePackManifest>(json.ToJsonString(), WebJson)!;
        json["sha256"] = AircraftCasePackManifest.ComputeSha256(manifest, files);
    }

    [Fact]
    public void AC001_PackManifestUsesArrFixtureDateAndValidContentHash()
    {
        var root = FixtureRoot();
        var manifestPath = Path.Combine(root, "manifest.json");
        var rawManifest = File.ReadAllText(manifestPath);
        var declaredManifest = JsonSerializer.Deserialize<AircraftCasePackManifest>(rawManifest, WebJson)!;
        var files = declaredManifest.Files.ToDictionary(
            path => path,
            path => File.ReadAllBytes(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))),
            StringComparer.Ordinal);

        var result = AircraftCasePackManifest.Parse(rawManifest, files);

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Manifest);
        Assert.Equal("1.0.1", result.Manifest.PackVersion);
        Assert.Equal("2026-10-01", result.Manifest.ScenarioDate);
        Assert.Equal("issue-460", result.Manifest.ProductOwnerReviewReference);
    }

    [Fact]
    public void ManifestContractAcceptsAndRejectsTableCases()
    {
        var documentBoundaries = new[] { 8, 12 };
        foreach (var count in documentBoundaries)
        {
            var boundaryPack = Pack(counts: Enumerable.Repeat(count, 3).ToArray());
            Assert.Empty(AircraftCasePackManifest.Validate(boundaryPack.Manifest, boundaryPack.Files));
        }
        var rows = new (string Name, Action<JsonObject> Change, bool Valid, string? Error)[]
        {
            ("integer schema version 1", _ => { }, true, null), ("leap date", j => j["scenarioDate"] = "2024-02-29", true, null),
            ("missing schema version", j => j.Remove("schemaVersion"), false, "schemaVersion"), ("string schema version", j => j["schemaVersion"] = "1", false, "schemaVersion"),
            ("version 2", j => j["schemaVersion"] = 2, false, "schemaVersion"), ("blank pack version", j => j["packVersion"] = " ", false, "packVersion"),
            ("two aircraft", j => j["cases"]![0]!["aircraftCount"] = 2, false, "aircraftCount"),
            ("no engine", j => j["cases"]![0]!["engineCount"] = 0, false, "engineCount"), ("impossible date", j => j["scenarioDate"] = "2026-02-30", false, "scenarioDate"),
            ("timestamp date", j => j["scenarioDate"] = "2026-02-28T00:00:00Z", false, "scenarioDate"), ("offset date", j => j["scenarioDate"] = "2026-02-28+00:00", false, "scenarioDate"),
            ("two cases", j => j["cases"]!.AsArray().RemoveAt(2), false, "cases"),
            ("four cases", j => j["cases"]!.AsArray().Add(JsonNode.Parse(JsonSerializer.Serialize(Pack(caseCount: 4).Manifest.Cases[3], WebJson))), false, "cases"),
            ("seven documents", j => { var a = j["cases"]![0]!["documents"]!.AsArray(); while (a.Count > 7) a.RemoveAt(a.Count - 1); }, false, "documents"),
            ("thirteen documents", j => { var a = j["cases"]![0]!["documents"]!.AsArray(); while (a.Count < 13) a.Add(JsonNode.Parse($$"""{"path":"case-1/extra-{{a.Count}}.txt","synthetic":true,"generatorReference":"generator-v1"}""")); }, false, "documents"),
            ("total below range", j => { foreach (var c in j["cases"]!.AsArray()) while (c!["documents"]!.AsArray().Count > 7) c["documents"]!.AsArray().RemoveAt(7); }, false, "total"),
            ("total above range", j => { foreach (var c in j["cases"]!.AsArray()) { var a = c!["documents"]!.AsArray(); while (a.Count < 13) a.Add(a[a.Count - 1]!.DeepClone()); } }, false, "total"),
            ("one component", j => j["cases"]![0]!["components"]!.AsArray().RemoveAt(1), false, "components"),
            ("three components", j => j["cases"]![0]!["components"]!.AsArray().Add(j["cases"]![0]!["components"]![0]!.DeepClone()), false, "components"),
            ("component without mapping", j => j["cases"]![0]!["components"]![0]!["evidenceDocumentPaths"] = new JsonArray(), false, "evidenceDocumentPaths")
        };
        foreach (var row in rows)
        {
            var pack = Pack();
            var json = Json(pack);
            row.Change(json);
            try { RefreshHash(json, pack.Files); } catch (JsonException) { }
            var result = AircraftCasePackManifest.Parse(json.ToJsonString(), pack.Files);
            Assert.True(row.Valid == (result.Manifest is not null), row.Name + ": " + string.Join(";", result.Errors));
            Assert.Equal(row.Valid, result.Errors.Count == 0);
            if (row.Error is not null) Assert.Contains(row.Error, string.Join(";", result.Errors));
        }
    }
    [Fact]
    public void ManifestProvenanceRequiresMarkersAndReferences()
    {
        var baseline = Pack();
        Assert.Empty(AircraftCasePackManifest.Validate(baseline.Manifest, baseline.Files));
        var rows = new List<(string Field, Action<JsonObject> Change)>
        {
            ("productOwnerReviewReference", j => j["productOwnerReviewReference"] = ""), ("productOwnerReviewReference", j => j.Remove("productOwnerReviewReference")),
            ("cases[1].synthetic", j => j["cases"]![1]!["synthetic"] = false), ("cases[1].generatorReference", j => j["cases"]![1]!["generatorReference"] = " "),
            ("cases[1].synthetic", j => j["cases"]![1]!.AsObject().Remove("synthetic")), ("cases[1].generatorReference", j => j["cases"]![1]!.AsObject().Remove("generatorReference"))
        };
        for (var i = 0; i < 3; i++)
        {
            var index = i;
            rows.Add(($"cases[{i}].synthetic", j => j["cases"]![index]!["synthetic"] = false));
            rows.Add(($"cases[{i}].generatorReference", j => j["cases"]![index]!["generatorReference"] = ""));
        }
        for (var i = 0; i < 9; i++)
        {
            var index = i;
            rows.Add(($"cases[0].documents[{i}].synthetic", j => j["cases"]![0]!["documents"]![index]!["synthetic"] = false));
            rows.Add(($"cases[0].documents[{i}].generatorReference", j => j["cases"]![0]!["documents"]![index]!["generatorReference"] = " "));
        }
        rows.Add(("cases[0].documents[0].synthetic", j => j["cases"]![0]!["documents"]![0]!.AsObject().Remove("synthetic")));
        rows.Add(("cases[0].documents[0].generatorReference", j => j["cases"]![0]!["documents"]![0]!.AsObject().Remove("generatorReference")));
        foreach (var row in rows)
        {
            var json = Json(baseline); row.Change(json); RefreshHash(json, baseline.Files);
            var result = AircraftCasePackManifest.Parse(json.ToJsonString(), baseline.Files);
            Assert.Null(result.Manifest); Assert.Contains(row.Field, string.Join(";", result.Errors));
        }
    }
    [Fact]
    public void ManifestHashAndVersionComparisonUseCanonicalContent()
    {
        var prior = Pack();
        Assert.Equal("ba766fe55dbc51fc96bee68a79230dd23207d5e3b45ef183df494e1022bf4509", prior.Manifest.Sha256);
        var changedFiles = new Dictionary<string, byte[]>(prior.Files) { [prior.Files.Keys.First()] = Encoding.UTF8.GetBytes("fixturE") };
        var changedWithOldHash = prior.Manifest;
        Assert.NotEmpty(AircraftCasePackManifest.Compare(prior.Manifest, changedWithOldHash, prior.Files, changedFiles));
        var changedSameVersion = changedWithOldHash with { Sha256 = AircraftCasePackManifest.ComputeSha256(changedWithOldHash, changedFiles) };
        Assert.Contains(AircraftCasePackManifest.Compare(prior.Manifest, changedSameVersion, prior.Files, changedFiles),
            e => e.Contains("packVersion", StringComparison.Ordinal));
        var changedVersion = changedSameVersion with { PackVersion = "1.0.1", Sha256 = "" };
        changedVersion = changedVersion with { Sha256 = AircraftCasePackManifest.ComputeSha256(changedVersion, changedFiles) };
        Assert.Empty(AircraftCasePackManifest.Compare(prior.Manifest, changedVersion, prior.Files, changedFiles));
    }
    [Fact]
    public void ManifestHashUsesDeterministicManifestListedContent()
    {
        const string expectedHash = "ba766fe55dbc51fc96bee68a79230dd23207d5e3b45ef183df494e1022bf4509";
        var pack = Pack();
        var json = Json(pack);
        var reversedJson = new JsonObject();
        foreach (var property in json.Reverse())
            reversedJson[property.Key] = property.Value?.DeepClone();

        var parsed = AircraftCasePackManifest.Parse(json.ToJsonString(), pack.Files);
        var reversed = AircraftCasePackManifest.Parse(reversedJson.ToJsonString(), pack.Files);
        Assert.NotNull(parsed.Manifest);
        Assert.NotNull(reversed.Manifest);
        Assert.Equal(expectedHash, AircraftCasePackManifest.ComputeSha256(parsed.Manifest, pack.Files));
        Assert.Equal(expectedHash, AircraftCasePackManifest.ComputeSha256(reversed.Manifest, pack.Files));

        var listedPath = pack.Manifest.Files[0];
        var changedContent = pack.Files[listedPath].ToArray();
        changedContent[0] ^= 1;
        var changedFiles = new Dictionary<string, byte[]>(pack.Files, StringComparer.Ordinal)
        {
            [listedPath] = changedContent
        };
        Assert.NotEqual(expectedHash, AircraftCasePackManifest.ComputeSha256(parsed.Manifest, changedFiles));

        var filesWithUnlistedContent = new Dictionary<string, byte[]>(pack.Files, StringComparer.Ordinal)
        {
            ["unlisted.txt"] = Encoding.UTF8.GetBytes("unlisted")
        };
        Assert.Equal(expectedHash, AircraftCasePackManifest.ComputeSha256(parsed.Manifest, filesWithUnlistedContent));

        var changedSha = parsed.Manifest with { Sha256 = new string('0', 64) };
        Assert.Equal(expectedHash, AircraftCasePackManifest.ComputeSha256(changedSha, pack.Files));

        json["sha256"] = new string('0', 64);
        var mismatchingHash = AircraftCasePackManifest.Parse(json.ToJsonString(), pack.Files);
        Assert.Null(mismatchingHash.Manifest);
        Assert.Contains("sha256: does not match manifest and file contents", mismatchingHash.Errors);
    }
    [Fact]
    public void ManifestParsingFailsClosedForInvalidInputs()
    {
        var pack = Pack();
        var rows = new (string Name, Action<JsonObject>? Change, string Field)[]
        {
            ("missing packVersion", j => j.Remove("packVersion"), "packVersion"), ("wrong packVersion type", j => j["packVersion"] = 4, "packVersion"),
            ("missing cases", j => j.Remove("cases"), "cases"), ("wrong files type", j => j["files"] = "not-an-array", "files"),
            ("bad date", j => j["scenarioDate"] = "2026-02-30", "scenarioDate"), ("timestamp", j => j["scenarioDate"] = "2026-02-28T00:00:00Z", "scenarioDate"),
            ("offset", j => j["scenarioDate"] = "2026-02-28+01:00", "scenarioDate"), ("short hash", j => j["sha256"] = new string('a', 63), "sha256"),
            ("long hash", j => j["sha256"] = new string('a', 65), "sha256"), ("uppercase hash", j => j["sha256"] = new string('A', 64), "sha256"),
            ("non-hex hash", j => j["sha256"] = new string('g', 64), "sha256"), ("malformed JSON", null, "input")
        };
        foreach (var row in rows)
        {
            string raw;
            if (row.Change is null) raw = "{ not json";
            else
            {
                var json = Json(pack);
                row.Change(json);
                if (!row.Name.Contains("hash", StringComparison.Ordinal))
                    try { RefreshHash(json, pack.Files); } catch (JsonException) { }
                raw = json.ToJsonString();
            }
            var result = AircraftCasePackManifest.Parse(raw, pack.Files); Assert.True(result.Manifest is null, row.Name);
            Assert.Contains(row.Field, string.Join(";", result.Errors));
        }
    }
}
