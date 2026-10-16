using System.Globalization;
using System.Text.RegularExpressions;

namespace AirlineDemo.Api.SourceSystems;

// Versioned metadata that describes a signed source fixture set (PRD FR-002, AC-004).
public sealed record SourceFixtureManifest
{
    public const string SupportedSchemaVersion = "1";

    public static readonly IReadOnlyList<string> RequiredSources =
        ["technical", "maintenance", "finance", "records", "contracts"];

    private static readonly Regex Sha256Pattern = new(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant);

    public string SchemaVersion { get; init; } = string.Empty;

    public string SourceFixtureVersion { get; init; } = string.Empty;

    // Calendar date serialized as YYYY-MM-DD.
    public string ScenarioDate { get; init; } = string.Empty;

    // Hex-encoded SHA-256 digest of the fixture payload.
    public string Sha256 { get; init; } = string.Empty;

    // Declared record count per source; every entry in RequiredSources must be present.
    public IReadOnlyDictionary<string, int> RecordCounts { get; init; } = new Dictionary<string, int>();

    // Returns every metadata violation. An empty list means the manifest is valid.
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (SchemaVersion != SupportedSchemaVersion)
        {
            problems.Add($"schemaVersion '{SchemaVersion}' is not supported; expected '{SupportedSchemaVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(SourceFixtureVersion))
        {
            problems.Add("sourceFixtureVersion is required.");
        }

        if (!DateOnly.TryParseExact(ScenarioDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            problems.Add($"scenarioDate '{ScenarioDate}' must be a valid date in YYYY-MM-DD form.");
        }

        if (!Sha256Pattern.IsMatch(Sha256))
        {
            problems.Add("sha256 must be 64 hexadecimal characters.");
        }

        foreach (var source in RequiredSources)
        {
            if (!RecordCounts.TryGetValue(source, out var count))
            {
                problems.Add($"recordCounts is missing a count for source '{source}'.");
            }
            else if (count < 0)
            {
                problems.Add($"recordCounts for source '{source}' must not be negative.");
            }
        }

        return problems;
    }
}
