using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DemoCaseGenerator;

/// <summary>
/// Writes the synthetic 40-case evaluation corpus (JSONL) and its manifest. Output depends only on the seed:
/// variation comes from a fixed linear congruential generator, never from the clock or <see cref="Random"/>.
/// </summary>
public static class EvaluationCaseGenerator
{
    public const int FixtureVersion = 1;
    public const int InputVersion = 1;
    public const int AnnotationVersion = 1;
    public const int CasesPerSituation = 10;

    public static readonly IReadOnlyList<string> Situations =
    [
        "baseline-complete",
        "missing-required-history",
        "ambiguous-identity",
        "blocked-or-unprocessed-evidence",
    ];

    private static readonly string[] DocumentTypes = ["release-to-service", "component-release", "shop-visit-record"];
    private static readonly string[] BlockedStatuses = ["corrupt", "truncated", "unprocessed"];

    public static string GenerateJsonl(int seed)
    {
        var state = unchecked((uint)seed);
        var builder = new StringBuilder();
        for (var index = 1; index <= Situations.Count * CasesPerSituation; index++)
        {
            var situation = Situations[(index - 1) / CasesPerSituation];
            builder.Append(BuildCase(index, situation, ref state).ToJsonString()).Append('\n');
        }

        return builder.ToString();
    }

    public static void WriteFixture(string outDir, int seed)
    {
        Directory.CreateDirectory(outDir);
        var bytes = new UTF8Encoding(false).GetBytes(GenerateJsonl(seed));
        File.WriteAllBytes(Path.Combine(outDir, "cases.jsonl"), bytes);

        var manifest = new JsonObject
        {
            ["fixtureVersion"] = FixtureVersion,
            ["suite"] = "evaluation-v1",
            ["seed"] = seed,
            ["caseCount"] = Situations.Count * CasesPerSituation,
            ["casesSha256"] = Sha256Hex(bytes),
        };
        File.WriteAllText(
            Path.Combine(outDir, "manifest.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));
    }

    /// <summary>Throws <see cref="InvalidDataException"/> unless the manifest version is 1 and its hash matches cases.jsonl bytes.</summary>
    public static void ValidateFixture(string dir)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")));
        if (manifest?["fixtureVersion"] is not JsonValue version
            || !version.TryGetValue<int>(out var versionNumber)
            || versionNumber != FixtureVersion)
        {
            throw new InvalidDataException($"fixtureVersion must be {FixtureVersion}.");
        }

        var expected = manifest["casesSha256"] is JsonValue hash && hash.TryGetValue<string>(out var hashText)
            ? hashText
            : null;
        var actual = Sha256Hex(File.ReadAllBytes(Path.Combine(dir, "cases.jsonl")));
        if (expected is null || !string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidDataException("casesSha256 is missing or does not match cases.jsonl bytes.");
        }
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static JsonObject BuildCase(int index, string situation, ref uint state)
    {
        var aircraftId = $"SYN-AC{index:D3}";
        var componentId = $"SYN-CMP{index:D3}";
        var documents = new List<JsonObject>();
        for (var k = 0; k < DocumentTypes.Length; k++)
        {
            documents.Add(new JsonObject
            {
                ["documentId"] = $"SYN-DOC{index:D3}{k + 1}",
                ["documentType"] = DocumentTypes[k],
                ["subjectId"] = k == 1 ? componentId : aircraftId,
                ["parseStatus"] = "complete",
            });
        }

        var outcomes = new JsonArray();
        switch (situation)
        {
            case "baseline-complete":
                outcomes.Add(Outcome("AC-001", "Assessment cites source evidence for the complete baseline, creates no missing-evidence request and does not grant technical acceptance."));
                outcomes.Add(Outcome("FR-002", "Investigation returns source-linked claims that are validated against document witnesses."));
                break;
            case "missing-required-history":
                documents.RemoveAt(NextBelow(ref state, documents.Count));
                outcomes.Add(Outcome("AC-002", "Exactly one routine evidence request is delivered to the mock inbox with approved wording and a recorded current basis."));
                break;
            case "ambiguous-identity":
                documents[2]["candidateSubjectIds"] = new JsonArray(aircraftId, $"SYN-AC{index + 100:D3}");
                outcomes.Add(Outcome("AC-003", "An internal review task is created for the ambiguous identity and no request is sent for that finding."));
                break;
            case "blocked-or-unprocessed-evidence":
                documents[NextBelow(ref state, documents.Count)]["parseStatus"] =
                    BlockedStatuses[NextBelow(ref state, BlockedStatuses.Length)];
                outcomes.Add(Outcome("AC-004", "The corrupt, truncated or unprocessed document is shown as a visible retryable failure and cannot justify a completeness request or acceptance."));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(situation), situation, "Unknown situation.");
        }

        outcomes.Add(Outcome("FR-009", "Case belongs to the frozen 40-case suite; all four arms receive identical inputs and eligible counts are reported."));

        return new JsonObject
        {
            ["inputVersion"] = InputVersion,
            ["caseId"] = $"EVAL-V1-{index:D2}",
            ["situation"] = situation,
            ["inputs"] = new JsonObject
            {
                ["packageId"] = $"SYN-PKG{index:D3}",
                ["aircraftId"] = aircraftId,
                ["componentId"] = componentId,
                ["requiredHistory"] = new JsonArray(DocumentTypes.Select(type => (JsonNode?)type).ToArray()),
                ["documents"] = new JsonArray(documents.Cast<JsonNode?>().ToArray()),
            },
            ["annotations"] = new JsonObject
            {
                ["annotationVersion"] = AnnotationVersion,
                ["expectedOutcomes"] = outcomes,
            },
        };
    }

    private static JsonObject Outcome(string requirementId, string outcome) =>
        new() { ["requirementId"] = requirementId, ["outcome"] = outcome };

    private static int NextBelow(ref uint state, int bound)
    {
        state = unchecked(state * 1664525u + 1013904223u);
        return (int)((state >> 16) % (uint)bound);
    }
}
