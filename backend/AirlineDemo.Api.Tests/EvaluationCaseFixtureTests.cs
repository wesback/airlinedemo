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
    private static readonly Regex EmailPattern = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}");
    private static readonly Regex[] PhonePatterns =
    [
        new(@"\+[0-9][0-9 ()-]{7,}[0-9]"),
        new(@"\([0-9]{3}\)[ ]?[0-9]{3}-[0-9]{4}"),
        new(@"[0-9]{3}-[0-9]{3}-[0-9]{4}"),
    ];
    private static readonly Regex SeparatorsBetweenDigits = new(@"(?<=\d)[ .()\-]+(?=\d)");
    private static readonly Regex SevenDigits = new(@"\d{7,}");

    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Fixtures", "Evaluation", "v1");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Fixtures/Evaluation/v1 not found above " + AppContext.BaseDirectory);
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

    private static void WriteCopy(string dir, string manifest, string cases)
    {
        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "cases.jsonl"), cases, new UTF8Encoding(false));
    }
}
