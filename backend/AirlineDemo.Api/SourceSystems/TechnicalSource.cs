using System.Collections.ObjectModel;
using System.Text.Json;

namespace AirlineDemo.Api.SourceSystems;

// Deterministic in-process stub for one synthetic source. Records are loaded once from an embedded,
// versioned fixture and validated against SourceRecord.Validate; every read is case-scoped and read-only (PRD FR-002).
public abstract class FixtureSourceSystem<TPayload> : ISourceSystem<TPayload> where TPayload : class
{
    // Fixed scenario date from the source fixture manifest; records dated before it are stale (DD-007).
    internal static readonly DateOnly ScenarioDate = new(2026, 10, 1);

    private readonly IReadOnlyList<SourceRecord<TPayload>> _records;

    protected FixtureSourceSystem(string sourceId, string resourceName, Func<IReadOnlyDictionary<string, string>, TPayload> createPayload)
    {
        _records = Load(sourceId, resourceName, createPayload);
    }

    public Task<SourceReadResult<IReadOnlyList<SourceRecord<TPayload>>>> ListAsync(
        string caseId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SourceRecord<TPayload>> scoped = _records.Where(r => r.CaseId == caseId).ToList();
        return Task.FromResult<SourceReadResult<IReadOnlyList<SourceRecord<TPayload>>>>(
            new SourceReadSucceeded<IReadOnlyList<SourceRecord<TPayload>>>(scoped));
    }

    public Task<SourceReadResult<SourceRecord<TPayload>>> GetAsync(
        string caseId,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var match = _records.FirstOrDefault(r => r.CaseId == caseId && r.RecordId == recordId);
        SourceReadResult<SourceRecord<TPayload>> result = match is null
            ? new SourceRecordNotFound<SourceRecord<TPayload>>(recordId)
            : new SourceReadSucceeded<SourceRecord<TPayload>>(match);
        return Task.FromResult(result);
    }

    private static IReadOnlyList<SourceRecord<TPayload>> Load(
        string sourceId,
        string resourceName,
        Func<IReadOnlyDictionary<string, string>, TPayload> createPayload)
    {
        using var stream = typeof(FixtureSourceSystem<TPayload>).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded source fixture '{resourceName}' is missing.");
        var file = JsonSerializer.Deserialize<FixtureFile>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException($"Embedded source fixture '{resourceName}' is empty.");

        if (!string.Equals(file.SourceId, sourceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Source fixture '{resourceName}' declares sourceId '{file.SourceId}', expected '{sourceId}'.");
        }

        var recordIds = new HashSet<string>(StringComparer.Ordinal);
        var records = new List<SourceRecord<TPayload>>();
        foreach (var dto in file.Records)
        {
            var fields = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(dto.Payload, StringComparer.Ordinal));
            var record = new SourceRecord<TPayload>
            {
                SourceId = sourceId,
                RecordId = dto.RecordId,
                RecordVersion = dto.RecordVersion,
                AsOf = dto.AsOf,
                CaseId = dto.CaseId,
                Kind = dto.Kind,
                Stale = dto.AsOf < ScenarioDate,
                Payload = createPayload(fields),
                Synthetic = dto.Synthetic,
            };

            var problems = record.Validate(record.CaseId);
            if (problems.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Source fixture '{resourceName}' record '{dto.RecordId}' is invalid: {string.Join(" ", problems)}");
            }

            if (!recordIds.Add(record.RecordId))
            {
                throw new InvalidOperationException($"Source fixture '{resourceName}' repeats recordId '{record.RecordId}'.");
            }

            records.Add(record);
        }

        return records.AsReadOnly();
    }

    // JSON shape of a signed source fixture file; field names are camelCase per docs/source-systems-contract.md.
    internal sealed class FixtureFile
    {
        public string SourceId { get; set; } = string.Empty;

        public List<FixtureRecord> Records { get; set; } = [];
    }

    internal sealed class FixtureRecord
    {
        public string RecordId { get; set; } = string.Empty;

        public int RecordVersion { get; set; }

        public DateOnly AsOf { get; set; }

        public string CaseId { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public bool Synthetic { get; set; }

        public Dictionary<string, string> Payload { get; set; } = [];
    }
}

// Typed payload for technical records: a kind-specific set of named string fields, read-only.
public sealed record TechnicalPayload(IReadOnlyDictionary<string, string> Fields);

public sealed class TechnicalSource()
    : FixtureSourceSystem<TechnicalPayload>(
        "technical",
        "AirlineDemo.Api.Fixtures.sources.technical.json",
        fields => new TechnicalPayload(fields));
