using System.Text.Json;
using System.Text.Json.Serialization;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.SourceSystems;

public sealed record SourceRecordInUse(string SourceId, string RecordId, int RecordVersion);

public sealed record SourceSummaryStatus(
    string SourceId,
    string Mode,
    string CapabilityLabel,
    bool Coverage,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RecordCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PinnedInBasis = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SourceRecordInUse>? RecordsInUse = null);

public sealed record SourceSummaryResult(IReadOnlyList<SourceSummaryStatus>? Sources, string? ErrorCode);

public sealed class SourceSummaryService(
    ISourceSystem<TechnicalPayload> technical,
    ISourceSystem<MaintenancePayload> maintenance,
    ISourceSystem<FinancePayload> finance,
    ISourceSystem<RecordsPayload> records,
    ISourceSystem<ContractsPayload> contracts)
{
    private const string CapabilityLabel = "Simulated";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SourceSummaryResult> BuildAsync(
        string caseId,
        string role,
        CaseState state,
        IReadOnlyDictionary<string, string> modes,
        CancellationToken cancellationToken)
    {
        var pins = ReadPins(state);
        var statuses = new List<SourceSummaryStatus>(SourceModes.Sources.Length);
        foreach (var (sourceId, read) in new (string, Func<string, string, CancellationToken, Task<(int Count, string? Error)>>)[]
        {
            ("technical", (id, mode, token) => Count(technical, "technical", id, mode, token)),
            ("maintenance", (id, mode, token) => Count(maintenance, "maintenance", id, mode, token)),
            ("finance", (id, mode, token) => Count(finance, "finance", id, mode, token)),
            ("records", (id, mode, token) => Count(records, "records", id, mode, token)),
            ("contracts", (id, mode, token) => Count(contracts, "contracts", id, mode, token)),
        })
        {
            var mode = modes[sourceId];
            var result = await read(caseId, mode, cancellationToken);
            if (result.Error is not null) return new(null, result.Error);

            var inUse = pins.Where(pin => pin.SourceId == sourceId)
                .Select(pin => new SourceRecordInUse(pin.SourceId, pin.RecordId, pin.RecordVersion)).ToArray();
            var details = CanSeeDetails(role, sourceId);
            statuses.Add(new SourceSummaryStatus(sourceId, mode, CapabilityLabel,
                mode == SourceModes.Available && result.Count > 0,
                details ? result.Count : null,
                details ? inUse.Length > 0 : null,
                details ? inUse : null));
        }

        return new(statuses, null);
    }

    private static async Task<(int Count, string? Error)> Count<TPayload>(
        ISourceSystem<TPayload> source, string sourceId, string caseId, string mode, CancellationToken cancellationToken)
        where TPayload : class
    {
        var result = await SourceModeFindingPolicy.Apply(source, sourceId, mode).ListAsync(caseId, cancellationToken);
        return result switch
        {
            SourceReadSucceeded<IReadOnlyList<SourceRecord<TPayload>>> success =>
                (success.Value.Count(record => string.Equals(record.CaseId, caseId, StringComparison.Ordinal)), null),
            SourceUnavailable<IReadOnlyList<SourceRecord<TPayload>>> when mode == "unavailable" => (0, null),
            SourceUnavailable<IReadOnlyList<SourceRecord<TPayload>>> => (0, "source_unavailable"),
            UnknownSource<IReadOnlyList<SourceRecord<TPayload>>> => (0, "source_read_failed"),
            _ => (0, "source_read_failed"),
        };
    }

    private static bool CanSeeDetails(string role, string sourceId) => role switch
    {
        "technical-reviewer" => sourceId is "technical" or "maintenance" or "records",
        "finance-reviewer" => sourceId is "technical" or "finance",
        "legal-reviewer" => sourceId is "contracts" or "records",
        _ => false,
    };

    private static IReadOnlyList<SourceRecordInUse> ReadPins(CaseState state)
    {
        if (state.Basis.Count == 0) return [];
        try
        {
            var snapshot = JsonSerializer.Deserialize<SourcePinSnapshot>(state.Basis[^1].Content, Json);
            return snapshot?.Format == "source-record-pins-v1" ? (snapshot.Pins ?? [])
                .Where(pin => pin is not null && pin.RecordVersion > 0 && !string.IsNullOrWhiteSpace(pin.SourceId)
                    && !string.IsNullOrWhiteSpace(pin.RecordId) && SourceModes.Sources.Contains(pin.SourceId, StringComparer.Ordinal))
                .ToArray() : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record SourcePinSnapshot(string? Format, IReadOnlyList<SourceRecordInUse>? Pins);
}
