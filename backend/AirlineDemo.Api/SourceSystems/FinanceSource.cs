namespace AirlineDemo.Api.SourceSystems;

// Typed payload for finance records: a kind-specific set of named string fields, read-only.
public sealed record FinancePayload(IReadOnlyDictionary<string, string> Fields);

// Synthetic lease and rate source for the core case (PRD FR-002, DD-019).
public sealed class FinanceSource()
    : FixtureSourceSystem<FinancePayload>(
        "finance",
        "AirlineDemo.Api.Fixtures.sources.finance.json",
        CoreManifest,
        fields => new FinancePayload(fields))
{
    // Signed manifest for finance.json: its SHA-256 digest and declared record counts (AC-004).
    // Sources this story does not supply yet are declared with zero records.
    internal static readonly SourceFixtureManifest CoreManifest = new()
    {
        SchemaVersion = SourceFixtureManifest.SupportedSchemaVersion,
        SourceFixtureVersion = "2026.10.1",
        ScenarioDate = "2026-10-01",
        Sha256 = "caa09f0c177f382232301dc65e50740bf24e45ceffc4831795ff8ca770d294b1",
        RecordCounts = new Dictionary<string, int>
        {
            ["technical"] = 0,
            ["maintenance"] = 0,
            ["finance"] = 4,
            ["records"] = 0,
            ["contracts"] = 0,
        },
    };
}
