namespace AirlineDemo.Api.SourceSystems;

// Versioned fixture manifest (PRD FR-002). manifest.json is signed as its exact bytes in manifest.sig.
// The per-file digests detect fixture drift only; they do not establish authenticity.
public sealed record SourceFixtureManifest
{
    public int SchemaVersion { get; init; }

    public string SourceFixtureVersion { get; init; } = string.Empty;

    public DateOnly ScenarioDate { get; init; }

    public IReadOnlyList<SourceFixtureFile> Files { get; init; } = [];
}

public sealed record SourceFixtureFile
{
    public string Source { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    // Lowercase hex SHA-256 of the file's exact bytes.
    public string Sha256 { get; init; } = string.Empty;

    // Declared record count per caseId. Every record in the file must belong to a declared case.
    public IReadOnlyDictionary<string, int> RecordCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

public static class SourceFixtureContract
{
    public const int SupportedSchemaVersion = 1;

    // Fixed scenario date of the synthetic fixture set.
    public static readonly DateOnly ScenarioDate = new(2026, 10, 16);

    public static readonly IReadOnlyList<string> Sources =
        ["technical", "maintenance", "finance", "records", "contracts"];
}
