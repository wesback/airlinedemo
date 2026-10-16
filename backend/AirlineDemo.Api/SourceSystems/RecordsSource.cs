namespace AirlineDemo.Api.SourceSystems;

// Typed payload for records-index entries: document metadata only, never document content.
public sealed record RecordsPayload(IReadOnlyDictionary<string, string> Fields);

// Synthetic document index over the case's evidence metadata (PRD FR-002, DD-003).
public sealed class RecordsSource()
    : FixtureSourceSystem<RecordsPayload>(
        "records",
        "AirlineDemo.Api.Fixtures.sources.records.json",
        CoreManifest,
        fields => new RecordsPayload(fields))
{
    // Signed manifest for records.json: its SHA-256 digest and declared record counts (AC-004).
    internal static readonly SourceFixtureManifest CoreManifest = new()
    {
        SchemaVersion = SourceFixtureManifest.SupportedSchemaVersion,
        SourceFixtureVersion = "2026.10.1",
        ScenarioDate = "2026-10-01",
        Sha256 = "958851a898418883d60773c0bc76b34a58fdcebf16bbcfafbc1d964a9e91a123",
        RecordCounts = new Dictionary<string, int>
        {
            ["technical"] = 0,
            ["maintenance"] = 0,
            ["finance"] = 0,
            ["records"] = 3,
            ["contracts"] = 0,
        },
    };
}
