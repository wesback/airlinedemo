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
        Sha256 = "b5b6aca6a3379dddf2d0149b6be64779be313d172e958422b5b7b561fedc1ad4",
        RecordCounts = new Dictionary<string, int>
        {
            ["technical"] = 0,
            ["maintenance"] = 0,
            ["finance"] = 0,
            ["records"] = 10,
            ["contracts"] = 0,
        },
    };
}
