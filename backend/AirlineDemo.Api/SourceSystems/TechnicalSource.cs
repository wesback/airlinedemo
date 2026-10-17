using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace AirlineDemo.Api.SourceSystems;

// Deterministic in-process stub for one synthetic source. Records are loaded once from an embedded,
// versioned fixture and validated against SourceRecord.Validate; every read is case-scoped and read-only (PRD FR-002).
public abstract class FixtureSourceSystem<TPayload> : ISourceSystem<TPayload> where TPayload : class
{
    // Fixed scenario date from the source fixture manifest; records dated before it are stale (DD-007).
    internal static readonly DateOnly ScenarioDate = new(2026, 10, 1);

    private readonly IReadOnlyList<SourceRecord<TPayload>> _records;

    protected FixtureSourceSystem(
        string sourceId,
        string resourceName,
        SourceFixtureManifest manifest,
        Func<IReadOnlyDictionary<string, string>, TPayload> createPayload)
    {
        _records = Load(sourceId, resourceName, manifest, createPayload);
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
        SourceFixtureManifest manifest,
        Func<IReadOnlyDictionary<string, string>, TPayload> createPayload)
    {
        using var stream = typeof(FixtureSourceSystem<TPayload>).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded source fixture '{resourceName}' is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return ParseFixture(sourceId, resourceName, manifest, buffer.ToArray(), createPayload);
    }

    // Verifies the fixture bytes against the signed manifest (metadata, digest, record count) before any record is served (AC-004).
    internal static IReadOnlyList<SourceRecord<TPayload>> ParseFixture(
        string sourceId,
        string resourceName,
        SourceFixtureManifest manifest,
        byte[] content,
        Func<IReadOnlyDictionary<string, string>, TPayload> createPayload)
    {
        var manifestProblems = manifest.Validate();
        if (manifestProblems.Count > 0)
        {
            throw new InvalidOperationException($"Source manifest for '{sourceId}' is invalid: {string.Join(" ", manifestProblems)}");
        }

        if (!string.Equals(manifest.ScenarioDate, ScenarioDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Source manifest for '{sourceId}' declares scenarioDate '{manifest.ScenarioDate}', expected '{ScenarioDate:yyyy-MM-dd}'.");
        }

        var digest = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!string.Equals(digest, manifest.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Source fixture '{resourceName}' does not match its signed digest.");
        }

        var file = JsonSerializer.Deserialize<FixtureFile>(new ReadOnlySpan<byte>(content), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException($"Embedded source fixture '{resourceName}' is empty.");

        if (!string.Equals(file.SourceId, sourceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Source fixture '{resourceName}' declares sourceId '{file.SourceId}', expected '{sourceId}'.");
        }

        if (!manifest.RecordCounts.TryGetValue(sourceId, out var declaredCount) || declaredCount != file.Records.Count)
        {
            throw new InvalidOperationException($"Source fixture '{resourceName}' has {file.Records.Count} records; manifest declares {declaredCount}.");
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
        CoreManifest,
        fields => new TechnicalPayload(fields))
{
    // Signed manifest for technical.json: its SHA-256 digest and declared record counts (AC-004).
    // Sources this story does not supply yet are declared with zero records.
    internal static readonly SourceFixtureManifest CoreManifest = new()
    {
        SchemaVersion = SourceFixtureManifest.SupportedSchemaVersion,
        SourceFixtureVersion = "2026.10.1",
        ScenarioDate = "2026-10-01",
        Sha256 = "757f3eb44b5dd835577c661257e60c3605d10e7d39cf8668f985b362e4e92604",
        RecordCounts = new Dictionary<string, int>
        {
            ["technical"] = 13,
            ["maintenance"] = 0,
            ["finance"] = 0,
            ["records"] = 0,
            ["contracts"] = 0,
        },
    };
}
