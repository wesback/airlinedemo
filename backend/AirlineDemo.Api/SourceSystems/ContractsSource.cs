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
        Sha256 = "2010ebf2580c49a8b5e35ed3513b1a17027681cd8124682359b2238db94df150",
        RecordCounts = new Dictionary<string, int>
        {
            ["technical"] = 0,
            ["maintenance"] = 0,
            ["finance"] = 0,
            ["records"] = 0,
            ["contracts"] = 1,
        },
    };
}
