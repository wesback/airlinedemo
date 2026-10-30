using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace AirlineDemo.Api.SourceSystems;

// Typed payload for records-index entries: document metadata only, never document content.
public sealed record RecordsPayload
{
    public RecordsPayload(IReadOnlyDictionary<string, string> fields)
    {
        Fields = new ReadOnlyDictionary<string, string>(
            fields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal));
        DocumentId = Fields.GetValueOrDefault("documentId") ?? string.Empty;
        DocumentVersion = Fields.GetValueOrDefault("documentVersion") ?? string.Empty;
        ContainsPersonalData = bool.TryParse(Fields.GetValueOrDefault("containsPersonalData"), out var marker) && marker;
    }

    public string DocumentId { get; }

    public string DocumentVersion { get; }

    public bool ContainsPersonalData { get; }

    [JsonIgnore]
    public IReadOnlyDictionary<string, string> Fields { get; }
}

// Synthetic document index over the case's evidence metadata (PRD FR-002, DD-003).
public sealed class RecordsSource()
    : FixtureSourceSystem<RecordsPayload>(
        "records",
        "AirlineDemo.Api.Fixtures.sources.records.json",
        CoreManifest,
        fields => new RecordsPayload(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["documentId"] = fields["documentId"],
            ["documentVersion"] = fields["documentVersion"],
            ["containsPersonalData"] = fields["containsPersonalData"],
        }))
{
    // Signed manifest for records.json: its SHA-256 digest and declared record counts (AC-004).
    internal static readonly SourceFixtureManifest CoreManifest = new()
    {
        SchemaVersion = SourceFixtureManifest.SupportedSchemaVersion,
        SourceFixtureVersion = SourceFixtureManifest.SupportedSourceFixtureVersion,
        ScenarioDate = "2026-10-01",
        Sha256 = "119f0945431845ff34c55dac02b49e9e059498f31adc4d73df745fdde2e03f21",
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
