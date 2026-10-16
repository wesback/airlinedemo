namespace AirlineDemo.Api.SourceSystems;

// Typed payload for maintenance records. Field values, including task-card text, are untrusted data and are never executed.
public sealed record MaintenancePayload(IReadOnlyDictionary<string, string> Fields);

// Synthetic maintenance work-package and task-card source for the core case (PRD FR-002).
public sealed class MaintenanceSource()
    : FixtureSourceSystem<MaintenancePayload>(
        "maintenance",
        "AirlineDemo.Api.Fixtures.sources.maintenance.json",
        CoreManifest,
        fields => new MaintenancePayload(fields))
{
    // Signed manifest for maintenance.json: its SHA-256 digest and declared record counts (AC-004).
    internal static readonly SourceFixtureManifest CoreManifest = new()
    {
        SchemaVersion = SourceFixtureManifest.SupportedSchemaVersion,
        SourceFixtureVersion = "2026.10.1",
        ScenarioDate = "2026-10-01",
        Sha256 = "54a23e1cf7864948540bd0e0abc9fde7c5555759805b471d72b0de813bc9fb18",
        RecordCounts = new Dictionary<string, int>
        {
            ["technical"] = 0,
            ["maintenance"] = 2,
            ["finance"] = 0,
            ["records"] = 0,
            ["contracts"] = 0,
        },
    };
}
