using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DemoCaseGenerator;

namespace AirlineDemo.Api.Tests;

public class EvaluationCaseFixtureTests
{
    private static readonly string[] SupportedRequirementIds =
        ["FR-002", "FR-009", "AC-001", "AC-002", "AC-003", "AC-004", "AC-013", "AC-016"];

    private static readonly string[] Situations =
        ["baseline-complete", "missing-required-history", "ambiguous-identity", "blocked-or-unprocessed-evidence"];

    private static readonly Regex CaseIdPattern = new(@"^EVAL-V1-[0-9]{2}$");
    private static readonly Regex IdentifierPattern = new(@"^SYN-[A-Z0-9]{3,16}$");
    private static readonly Regex RequirementIdPattern = new(@"^(FR|NFR|AC)-[0-9]{3}$");
    private static readonly Regex EmailPattern = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}");
    private static readonly Regex[] PhonePatterns =
    [
        new(@"\+[0-9][0-9 ()-]{7,}[0-9]"),
        new(@"\([0-9]{3}\)[ ]?[0-9]{3}-[0-9]{4}"),
        new(@"[0-9]{3}-[0-9]{3}-[0-9]{4}"),
    ];
    private static readonly Regex SeparatorsBetweenDigits = new(@"(?<=\d)[ .()\-]+(?=\d)");
    private static readonly Regex SevenDigits = new(@"\d{7,}");

    private static readonly string[] V2PageStatuses = ["complete", "corrupt", "truncated", "unprocessed", "failed", "partial"];
    private static readonly Regex V2CaseIdPattern = new(@"^EVAL-V2-[0-9]{2}$");

    private static string FixtureDir(string version = "v1")
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Fixtures", "Evaluation", version);
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"Fixtures/Evaluation/{version} not found above " + AppContext.BaseDirectory);
    }

    private static string[] CommittedLines() =>
        File.ReadAllText(Path.Combine(FixtureDir(), "cases.jsonl"), new UTF8Encoding(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void AC01_GenerationIsDeterministicAndHasExactSituationCounts()
    {
        var first = Encoding.UTF8.GetBytes(EvaluationCaseGenerator.GenerateJsonl(164));
        var second = Encoding.UTF8.GetBytes(EvaluationCaseGenerator.GenerateJsonl(164));
        Assert.Equal(first, second);

        var lines = Encoding.UTF8.GetString(first).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(40, lines.Length);
        var counts = lines
            .Select(l => JsonNode.Parse(l)!["situation"]!.GetValue<string>())
            .GroupBy(s => s)
            .ToDictionary(g => g.Key, g => g.Count());
        foreach (var situation in Situations)
        {
            Assert.True(counts.TryGetValue(situation, out var n) && n == 10, $"{situation} count was {n}");
        }
        Assert.Equal(40, counts.Values.Sum());

        Assert.Equal(first, File.ReadAllBytes(Path.Combine(FixtureDir(), "cases.jsonl")));
    }

    [Fact]
    public void AC02_CasesHaveSupportedVersionedInputsAndRequirementAnnotations()
    {
        var lines = CommittedLines();
        Assert.Equal(40, lines.Length);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            Assert.Equal(JsonValueKind.Number, root.GetProperty("inputVersion").ValueKind);
            Assert.Equal(1, root.GetProperty("inputVersion").GetInt32());
            Assert.Equal(JsonValueKind.String, root.GetProperty("caseId").ValueKind);
            Assert.Matches(CaseIdPattern, root.GetProperty("caseId").GetString()!);
            Assert.Contains(root.GetProperty("situation").GetString(), Situations);
            Assert.Equal(JsonValueKind.Object, root.GetProperty("inputs").ValueKind);

            var annotations = root.GetProperty("annotations");
            Assert.Equal(JsonValueKind.Object, annotations.ValueKind);
            Assert.Equal(1, annotations.GetProperty("annotationVersion").GetInt32());
            var outcomes = annotations.GetProperty("expectedOutcomes");
            Assert.Equal(JsonValueKind.Array, outcomes.ValueKind);
            Assert.NotEmpty(outcomes.EnumerateArray());
            foreach (var outcome in outcomes.EnumerateArray())
            {
                var requirementId = outcome.GetProperty("requirementId").GetString();
                Assert.Contains(requirementId, SupportedRequirementIds);
                var text = outcome.GetProperty("outcome").GetString();
                Assert.False(string.IsNullOrWhiteSpace(text), "empty outcome in " + line);
            }
        }
    }

    [Fact]
    public void AC03_InputsUseSyntheticIdentifiersAndExcludeContactData()
    {
        foreach (var line in CommittedLines())
        {
            using var doc = JsonDocument.Parse(line);
            var inputs = doc.RootElement.GetProperty("inputs");
            Assert.Matches(CaseIdPattern, doc.RootElement.GetProperty("caseId").GetString()!);
            CheckValue(inputs, "", line);
        }
    }

    private static void CheckValue(JsonElement element, string name, string line)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    CheckValue(prop.Value, prop.Name, line);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CheckValue(item, name, line);
                break;
            case JsonValueKind.String:
                var value = element.GetString()!;
                if (name == "requirementId")
                {
                    Assert.Matches(RequirementIdPattern, value);
                    return;
                }
                if (IsIdentifierField(name))
                {
                    Assert.Matches(IdentifierPattern, value);
                    return;
                }
                Assert.DoesNotMatch(EmailPattern, value);
                foreach (var phone in PhonePatterns)
                    Assert.DoesNotMatch(phone, value);
                var digitsOnly = SeparatorsBetweenDigits.Replace(value, "");
                Assert.DoesNotMatch(SevenDigits, digitsOnly);
                break;
        }
    }

    private static bool IsIdentifierField(string name) =>
        name.EndsWith("Id", StringComparison.Ordinal) || name.EndsWith("Ids", StringComparison.Ordinal);

    [Fact]
    public void AC04_ManifestValidatesVersionAndExactCaseBytes()
    {
        EvaluationCaseGenerator.ValidateFixture(FixtureDir());

        var manifestText = File.ReadAllText(Path.Combine(FixtureDir(), "manifest.json"));
        var casesText = File.ReadAllText(Path.Combine(FixtureDir(), "cases.jsonl"), new UTF8Encoding(false));

        var temp = Path.Combine(Path.GetTempPath(), "eval-fixture-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);

            // Missing hash
            var noHash = JsonNode.Parse(manifestText)!.AsObject();
            noHash.Remove("casesSha256");
            WriteCopy(temp, noHash.ToJsonString(), casesText);
            Assert.ThrowsAny<Exception>(() => EvaluationCaseGenerator.ValidateFixture(temp));

            // Changed case byte, original hash retained
            var changed = casesText.Replace("\"parseStatus\":\"complete\"", "\"parseStatus\":\"complete \"");
            Assert.NotEqual(casesText, changed);
            WriteCopy(temp, manifestText, changed);
            Assert.ThrowsAny<Exception>(() => EvaluationCaseGenerator.ValidateFixture(temp));

            // Unsupported fixture version
            var v2 = manifestText.Replace("\"fixtureVersion\": 1", "\"fixtureVersion\": 2");
            Assert.NotEqual(manifestText, v2);
            WriteCopy(temp, v2, casesText);
            Assert.ThrowsAny<Exception>(() => EvaluationCaseGenerator.ValidateFixture(temp));
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    private static string[] V2Lines() =>
        File.ReadAllText(Path.Combine(FixtureDir("v2"), "cases.jsonl"), new UTF8Encoding(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void FrozenV2HasRequiredCaseMix()
    {
        var lines = V2Lines();
        Assert.Equal(40, lines.Length);
        var ids = new HashSet<string>();
        var counts = new Dictionary<string, int>();
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.True(ids.Add(doc.RootElement.GetProperty("caseId").GetString()!));
            var category = doc.RootElement.GetProperty("category").GetString()!;
            counts[category] = counts.GetValueOrDefault(category) + 1;
        }
        Assert.Equal(40, ids.Count);
        foreach (var situation in Situations)
        {
            Assert.Equal(10, counts.GetValueOrDefault(situation));
        }
        Assert.Equal(40, counts.Values.Sum());
    }

    private static void AssertStructuredV2(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("inputVersion").GetInt32());
        Assert.Matches(V2CaseIdPattern, root.GetProperty("caseId").GetString()!);
        Assert.Contains(root.GetProperty("category").GetString(), Situations);

        var inputs = root.GetProperty("inputs");
        foreach (var key in new[] { "packageId", "aircraftId", "componentId" })
            Assert.False(string.IsNullOrWhiteSpace(inputs.GetProperty(key).GetString()));
        Assert.NotEmpty(inputs.GetProperty("requiredHistory").EnumerateArray());

        var documents = inputs.GetProperty("documents").EnumerateArray().ToList();
        Assert.NotEmpty(documents);
        foreach (var d in documents)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.GetProperty("documentId").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(d.GetProperty("documentType").GetString()));
            Assert.True(d.TryGetProperty("subjectId", out _) || d.TryGetProperty("candidateSubjectIds", out _));
            Assert.True(d.GetProperty("pageNumber").GetInt32() >= 1);
            Assert.Contains(d.GetProperty("parseStatus").GetString(), V2PageStatuses);
            Assert.False(string.IsNullOrWhiteSpace(d.GetProperty("pageText").GetString()));
        }

        var annotations = root.GetProperty("annotations");
        Assert.Equal(2, annotations.GetProperty("annotationVersion").GetInt32());
        var outcomes = annotations.GetProperty("expectedOutcomes").EnumerateArray().ToList();
        Assert.NotEmpty(outcomes);
        foreach (var o in outcomes)
        {
            Assert.Contains(o.GetProperty("requirementId").GetString(), SupportedRequirementIds);
            Assert.False(string.IsNullOrWhiteSpace(o.GetProperty("expectedState").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(o.GetProperty("outcome").GetString()));
        }

        var citations = annotations.GetProperty("claimCitations").EnumerateArray().ToList();
        Assert.NotEmpty(citations);
        foreach (var c in citations)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("claim").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("documentId").GetString()));
            Assert.True(c.GetProperty("pageNumber").GetInt32() >= 1);
            var support = c.GetProperty("supportLabel").GetString();
            Assert.Contains(support, new[] { "supported", "unsupported", "contradicted", "not-evaluable" });
            var expected = support switch { "supported" => "accept", "not-evaluable" => "hold", _ => "reject" };
            Assert.Equal(expected, c.GetProperty("expectedResult").GetString());
        }
    }

    [Fact]
    public void EveryCaseHasStructuredScoringGroundTruth()
    {
        foreach (var line in V2Lines())
        {
            AssertStructuredV2(line);
        }
    }

    [Fact]
    public void V2ContentMeetsTheCaseDesign()
    {
        var blockedStatuses = new[] { "failed", "unprocessed", "partial" };
        foreach (var line in V2Lines())
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var category = root.GetProperty("category").GetString()!;
            var documents = root.GetProperty("inputs").GetProperty("documents").EnumerateArray().ToList();

            var pageNumbersPerDocument = documents.GroupBy(d => d.GetProperty("documentId").GetString()!)
                .ToDictionary(g => g.Key, g => g.Select(d => d.GetProperty("pageNumber").GetInt32()).ToList());
            Assert.InRange(pageNumbersPerDocument.Count, 8, 12);
            foreach (var (documentId, pageNumbers) in pageNumbersPerDocument)
            {
                Assert.Equal(pageNumbers.Count, pageNumbers.Distinct().Count());
                Assert.InRange(pageNumbers.Distinct().Count(), 1, 4);
                foreach (var pageNumber in pageNumbers)
                {
                    Assert.InRange(pageNumber, 1, 4);
                }
            }

            foreach (var d in documents)
            {
                Assert.True(d.GetProperty("pageText").GetString()!.Split('\n').Length >= 4);
            }

            var documentTypes = documents.Select(d => d.GetProperty("documentType").GetString()!).Distinct().Count();
            Assert.True(documentTypes >= 5, $"{root.GetProperty("caseId").GetString()} has {documentTypes} document types");

            var statuses = documents.Select(d => d.GetProperty("parseStatus").GetString()!).ToList();
            var blockedPages = statuses.Count(s => blockedStatuses.Contains(s));
            if (category == "blocked-or-unprocessed-evidence")
                Assert.True(blockedPages >= 1, $"{root.GetProperty("caseId").GetString()} has no blocked page");
            else
                Assert.Equal(0, blockedPages);

            var labels = root.GetProperty("annotations").GetProperty("claimCitations").EnumerateArray()
                .Select(c => c.GetProperty("supportLabel").GetString()).ToList();
            if (category == "baseline-complete")
                Assert.True(labels.Count(l => l == "unsupported") >= 1, $"{root.GetProperty("caseId").GetString()} has no unsupported claim");
        }
    }

    [Fact]
    public void V2ValidatorRejectsMissingSourceTextOrGroundTruth()
    {
        var line = V2Lines()[0];
        AssertStructuredV2(line);

        var noText = JsonNode.Parse(line)!.AsObject();
        noText["inputs"]!["documents"]![0]!.AsObject().Remove("pageText");
        Assert.ThrowsAny<Exception>(() => AssertStructuredV2(noText.ToJsonString()));

        var noCitations = JsonNode.Parse(line)!.AsObject();
        noCitations["annotations"]!.AsObject().Remove("claimCitations");
        Assert.ThrowsAny<Exception>(() => AssertStructuredV2(noCitations.ToJsonString()));
    }

    [Fact]
    public void V2ManifestHashMatchesFixtureBytes()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDir("v2"), "manifest.json")))!.AsObject();
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDir("v2"), "cases.jsonl"));
        Assert.Equal(2, manifest["fixtureVersion"]!.GetValue<int>());
        Assert.Equal(40, manifest["caseCount"]!.GetValue<int>());
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
            manifest["casesSha256"]!.GetValue<string>());
    }

    [Fact]
    public void V2ContainsOnlySyntheticFixtureData()
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir("v2"), "cases.jsonl"), new UTF8Encoding(false));
        Assert.DoesNotMatch(EmailPattern, raw);
        foreach (var phone in PhonePatterns)
        {
            Assert.DoesNotMatch(phone, raw);
        }

        foreach (var line in V2Lines())
        {
            using var doc = JsonDocument.Parse(line);
            CheckValue(doc.RootElement.GetProperty("inputs"), "", line);
            CheckValue(doc.RootElement.GetProperty("annotations"), "", line);
        }
    }

    [Fact]
    public void V1FrozenFixtureIsUnchanged()
    {
        var dir = FixtureDir("v1");
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "cases.jsonl"))))
            .ToLowerInvariant();
        Assert.Equal("280aa4f40d81eb85b9d3eca289b558ada94f2beebf46f3e796642182fe71b3a2", hash);

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!.AsObject();
        Assert.Equal(1, manifest["fixtureVersion"]!.GetValue<int>());
        Assert.Equal(hash, manifest["casesSha256"]!.GetValue<string>());
    }

    private static void WriteCopy(string dir, string manifest, string cases)
    {
        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "cases.jsonl"), cases, new UTF8Encoding(false));
    }
}
