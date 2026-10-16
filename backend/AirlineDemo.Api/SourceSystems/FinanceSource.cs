namespace AirlineDemo.Api.SourceSystems;

// Typed payload for finance records: a kind-specific set of named string fields, read-only.
public sealed record FinancePayload(IReadOnlyDictionary<string, string> Fields);

// Synthetic lease and rate source for the core case (PRD FR-002, DD-019).
public sealed class FinanceSource()
    : FixtureSourceSystem<FinancePayload>(
        "finance",
        "AirlineDemo.Api.Fixtures.sources.finance.json",
        fields => new FinancePayload(fields));
