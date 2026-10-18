using System.Text.Json;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Investigation;
public sealed record SourceModeFindingResult(string? Reason, IReadOnlyDictionary<int, IReadOnlyList<AssessedWitness>> WitnessesByClaim);
/// <summary>Applies simulated source modes to reads and routes source-dependent findings deterministically.</summary>
public static class SourceModeFindingPolicy
{
    private static readonly DateOnly StaleAsOf = new(2026, 9, 30);
    public static ISourceSystem<TPayload> Apply<TPayload>(ISourceSystem<TPayload> source, string sourceId, string mode)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!SourceModes.Modes.Contains(mode, StringComparer.Ordinal))
            throw new ArgumentException("Source mode is invalid.", nameof(mode));
        return new ModeSource<TPayload>(source, sourceId, mode);
    }
    public static SourceModeFindingResult Assess(IReadOnlyList<AssessmentClaim> claims,
        IReadOnlyDictionary<string, string> modes, IReadOnlyList<SourceBasisPin> pins)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(modes);
        ArgumentNullException.ThrowIfNull(pins);
        var sourceMode = modes.GetValueOrDefault("technical", SourceModes.Available);
        var sourceRecords = sourceMode == "unavailable"
            ? []
            : pins.Where(pin => pin.SourceId == "technical")
                .Select(pin => (Pin: pin, Identity: ReadTechnicalIdentity(pin.Content)))
                .Where(record => record.Identity.SerialNumber is not null)
                .Select(record => (
                    record.Identity.SerialNumber,
                    record.Identity.Position,
                    Witness: new AssessedWitness(
                        $"source:{record.Pin.SourceId}:{record.Pin.RecordId}:v{record.Pin.RecordVersion}",
                        0)))
                .ToArray();
        return AssessRecords(claims, modes, sourceRecords);
    }

    public static SourceModeFindingResult AssessTechnicalRecords(IReadOnlyList<AssessmentClaim> claims,
        IReadOnlyDictionary<string, string> modes, IReadOnlyList<SourceRecord<TechnicalPayload>> records)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(modes);
        ArgumentNullException.ThrowIfNull(records);
        var sourceRecords = records
            .Select(record => (
                SerialNumber: record.Payload.Fields.GetValueOrDefault("serialNumber"),
                Position: record.Payload.Fields.GetValueOrDefault("position"),
                Witness: new AssessedWitness(
                    $"source:{record.SourceId}:{record.RecordId}:v{record.RecordVersion}",
                    0)))
            .Where(record => record.SerialNumber is not null)
            .ToArray();
        return AssessRecords(claims, modes, sourceRecords);
    }

    private static SourceModeFindingResult AssessRecords(
        IReadOnlyList<AssessmentClaim> claims,
        IReadOnlyDictionary<string, string> modes,
        (string? SerialNumber, string? Position, AssessedWitness Witness)[] sourceRecords)
    {
        var sourceMode = modes.GetValueOrDefault("technical", SourceModes.Available);
        var reason = (string?)null;
        var witnesses = new Dictionary<int, IReadOnlyList<AssessedWitness>>();
        for (var index = 0; index < claims.Count; index++)
        {
            var claim = claims[index];
            if (string.IsNullOrWhiteSpace(claim.SerialNumber) ||
                claim.Witnesses is null ||
                !claim.Witnesses.Any(witness => witness is not null
                    && !string.IsNullOrEmpty(witness.Quote)
                    && witness.Quote.Contains(claim.SerialNumber, StringComparison.Ordinal)))
                continue;
            var relevantRecords = sourceRecords
                .Where(record => MatchesComponent(record.Position, claim.Component))
                .ToArray();
            if (sourceMode == "unavailable")
            {
                reason ??= "source_unavailable";
                continue;
            }
            if (relevantRecords.Length > 0)
            {
                witnesses[index] = relevantRecords
                    .Select(record => record.Witness)
                    .ToArray();
            }
            if (sourceMode == "stale" && relevantRecords.Length > 0)
            {
                reason ??= "source_stale";
            }
            else if (relevantRecords.Length > 0 &&
                     (sourceMode == "conflicting" ||
                      relevantRecords.Any(record => !string.Equals(record.SerialNumber, claim.SerialNumber, StringComparison.Ordinal))))
            {
                reason ??= "source_conflict";
            }
        }
        return new SourceModeFindingResult(reason, witnesses);
    }
    private static (string? SerialNumber, string? Position) ReadTechnicalIdentity(string content)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (!root.TryGetProperty("payload", out var payload)
            || !payload.TryGetProperty("fields", out var fields))
            return (null, null);
        var serialNumber = fields.TryGetProperty("serialNumber", out var serial)
            ? serial.GetString()
            : null;
        var position = fields.TryGetProperty("position", out var fieldPosition)
            ? fieldPosition.GetString()
            : null;
        return (serialNumber, position);
    }
    private static bool MatchesComponent(string? position, string component)
    {
        if (string.IsNullOrWhiteSpace(position) || string.IsNullOrWhiteSpace(component))
            return false;
        var positionComponent = position.Split('_', 2)[0];
        return string.Equals(positionComponent, component.Trim(), StringComparison.OrdinalIgnoreCase);
    }
    private sealed class ModeSource<TPayload>(ISourceSystem<TPayload> inner, string sourceId, string mode) : ISourceSystem<TPayload> where TPayload : class
    {
        public async Task<SourceReadResult<IReadOnlyList<SourceRecord<TPayload>>>> ListAsync(
            string caseId, CancellationToken cancellationToken = default, int? maxRecords = null)
        {
            if (mode == "unavailable")
                return new SourceUnavailable<IReadOnlyList<SourceRecord<TPayload>>>(sourceId);
            var result = await inner.ListAsync(caseId, cancellationToken, maxRecords);
            return result is SourceReadSucceeded<IReadOnlyList<SourceRecord<TPayload>>> success
                ? new SourceReadSucceeded<IReadOnlyList<SourceRecord<TPayload>>>(Transform(success.Value))
                : result;
        }
        public async Task<SourceReadResult<SourceRecord<TPayload>>> GetAsync(
            string caseId, string recordId, CancellationToken cancellationToken = default)
        {
            if (mode == "unavailable")
                return new SourceUnavailable<SourceRecord<TPayload>>(sourceId);
            var result = await inner.GetAsync(caseId, recordId, cancellationToken);
            return result is SourceReadSucceeded<SourceRecord<TPayload>> success
                ? new SourceReadSucceeded<SourceRecord<TPayload>>(Transform(success.Value))
                : result;
        }
        private IReadOnlyList<SourceRecord<TPayload>> Transform(IReadOnlyList<SourceRecord<TPayload>> records) =>
            mode switch
            {
                "available" => records,
                "stale" => records.Select(MarkStale).ToArray(),
                "conflicting" => records.SelectMany(record => new[]
                    { record, record with { RecordVersion = checked(record.RecordVersion + 1) } }).ToArray(),
                _ => throw new InvalidOperationException($"Unsupported source mode '{mode}'."),
            };
        private SourceRecord<TPayload> Transform(SourceRecord<TPayload> record) =>
            mode switch
            {
                "available" => record,
                "stale" => MarkStale(record),
                "conflicting" => record with { RecordVersion = checked(record.RecordVersion + 1) },
                _ => throw new InvalidOperationException($"Unsupported source mode '{mode}'."),
            };
        private static SourceRecord<TPayload> MarkStale(SourceRecord<TPayload> record) =>
            record with { AsOf = StaleAsOf, Stale = true };
    }
}
