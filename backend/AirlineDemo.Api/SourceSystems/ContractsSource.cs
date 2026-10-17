namespace AirlineDemo.Api.SourceSystems;

// Typed payload for contract obligation clauses: a kind-specific set of named string fields, read-only.
public sealed record ContractsPayload(IReadOnlyDictionary<string, string> Fields);

// Synthetic obligation-clause source for the core case (PRD FR-002).
public sealed class ContractsSource()
    : FixtureSourceSystem<ContractsPayload>(
        "contracts",
        "AirlineDemo.Api.Fixtures.sources.contracts.json",
        CoreManifest,
        fields => new ContractsPayload(fields))
{
    // Signed manifest for contracts.json: its SHA-256 digest and declared record counts (AC-004).
    internal static readonly SourceFixtureManifest CoreManifest = new()
    {
        SchemaVersion = SourceFixtureManifest.SupportedSchemaVersion,
        SourceFixtureVersion = "2026.10.1",
        ScenarioDate = "2026-10-01",
        Sha256 = "6a40c37f1e410b9635bc4266ea526ba2d7405bc946ec4a3460c02d0e961953f0",
        RecordCounts = new Dictionary<string, int>
        {
            ["technical"] = 0,
            ["maintenance"] = 0,
            ["finance"] = 0,
            ["records"] = 0,
            ["contracts"] = 4,
        },
    };
}
