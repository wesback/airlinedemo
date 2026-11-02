namespace AirlineDemo.Api.SourceSystems;

// Typed payload for maintenance records. Field values, including task-card text, are untrusted data and are never executed.
public sealed record MaintenancePayload(IReadOnlyDictionary<string, string> Fields);

// Committed task card identities per case, read synchronously from loaded fixture data (no source read, no read budget).
public interface ICopilotTaskCards
{
    IReadOnlyList<(string RecordId, int RecordVersion)> TaskCards(string caseId);
}

// Synthetic maintenance work-package and task-card source for the core case (PRD FR-002).
public sealed class MaintenanceSource()
    : FixtureSourceSystem<MaintenancePayload>(
        "maintenance",
        "AirlineDemo.Api.Fixtures.sources.maintenance.json",
        CoreManifest,
        fields => new MaintenancePayload(fields)), ICopilotTaskCards
{
    public IReadOnlyList<(string RecordId, int RecordVersion)> TaskCards(string caseId) =>
        [.. Records.Where(record => record.CaseId == caseId && record.Kind == "task_card").Select(record => (record.RecordId, record.RecordVersion))];

    // Signed manifest for maintenance.json: its SHA-256 digest and declared record counts (AC-004).
    internal static readonly SourceFixtureManifest CoreManifest = new()
    {
        SchemaVersion = SourceFixtureManifest.SupportedSchemaVersion,
        SourceFixtureVersion = SourceFixtureManifest.SupportedSourceFixtureVersion,
        ScenarioDate = "2026-10-01",
        Sha256 = "87558838dada9a19406ed6ac5a0410e17f5f9818ef3435f1906a671a984e9a0f",
        RecordCounts = new Dictionary<string, int>
        {
            ["technical"] = 0,
            ["maintenance"] = 6,
            ["finance"] = 0,
            ["records"] = 0,
            ["contracts"] = 0,
        },
    };
}
