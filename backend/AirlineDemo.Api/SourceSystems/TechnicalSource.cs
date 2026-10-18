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
        Func<IReadOnlyDictionary<string, string>, TPayload> createPayload,
        Func<string, IReadOnlyDictionary<string, string>, IReadOnlyList<string>>? validatePayload = null)
    {
        _records = Load(sourceId, resourceName, manifest, createPayload, validatePayload);
    }

    public Task<SourceReadResult<IReadOnlyList<SourceRecord<TPayload>>>> ListAsync(
        string caseId,
        CancellationToken cancellationToken = default,
        int? maxRecords = null)
    {
        if (maxRecords is <= 0) throw new ArgumentOutOfRangeException(nameof(maxRecords));
        var limit = maxRecords ?? int.MaxValue;
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SourceRecord<TPayload>> scoped = _records.Where(r => r.CaseId == caseId).Take(limit).ToList();
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
        Func<IReadOnlyDictionary<string, string>, TPayload> createPayload,
        Func<string, IReadOnlyDictionary<string, string>, IReadOnlyList<string>>? validatePayload)
    {
        using var stream = typeof(FixtureSourceSystem<TPayload>).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded source fixture '{resourceName}' is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return ParseFixture(sourceId, resourceName, manifest, buffer.ToArray(), createPayload, validatePayload);
    }

    // Verifies the fixture bytes against the signed manifest (metadata, digest, record count) before any record is served (AC-004).
    internal static IReadOnlyList<SourceRecord<TPayload>> ParseFixture(
        string sourceId,
        string resourceName,
        SourceFixtureManifest manifest,
        byte[] content,
        Func<IReadOnlyDictionary<string, string>, TPayload> createPayload,
        Func<string, IReadOnlyDictionary<string, string>, IReadOnlyList<string>>? validatePayload = null)
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
            var payloadProblems = validatePayload?.Invoke(dto.Kind, fields) ?? [];
            if (payloadProblems.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Source fixture '{resourceName}' record '{dto.RecordId}' is invalid: {string.Join(" ", payloadProblems)}");
            }

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

public static class ApplicabilityPayloadValidator
{
    private static readonly string[] AllowedFields = ["requirementId", "regime", "responsibleParty"];

    public static IReadOnlyList<string> Validate<TValue>(IReadOnlyDictionary<string, TValue>? fields)
    {
        if (fields is null)
        {
            return ["Applicability payload fields are required."];
        }

        var problems = new List<string>();
        if (!fields.TryGetValue("requirementId", out var requirementId))
        {
            problems.Add("Applicability requirementId is required.");
        }
        else if (requirementId is not string requirement || string.IsNullOrWhiteSpace(requirement))
        {
            problems.Add("Applicability requirementId must be a non-empty string.");
        }

        if (fields.TryGetValue("regime", out var regime)
            && regime is not ("EASA" or "FAA-91" or "FAA-121" or "Other"))
        {
            problems.Add("Applicability regime must be EASA, FAA-91, FAA-121 or Other.");
        }

        if (fields.TryGetValue("responsibleParty", out var responsibleParty)
            && responsibleParty is not ("lessee-operator" or "lessor" or "camo" or "unassigned"))
        {
            problems.Add("Applicability responsibleParty must be lessee-operator, lessor, camo or unassigned.");
        }

        foreach (var field in fields.Keys.Except(AllowedFields, StringComparer.Ordinal))
        {
            problems.Add($"Unexpected applicability field '{field}'.");
        }

        return problems;
    }
}

internal static class TechnicalPayloadValidator
{
    public static IReadOnlyList<string> Validate(string kind, IReadOnlyDictionary<string, string> fields) =>
        kind == "applicability" ? ApplicabilityPayloadValidator.Validate(fields) : [];
}

public sealed class TechnicalSource()
    : FixtureSourceSystem<TechnicalPayload>(
        "technical",
        "AirlineDemo.Api.Fixtures.sources.technical.json",
        CoreManifest,
        fields => new TechnicalPayload(fields),
        TechnicalPayloadValidator.Validate)
{
    // Signed manifest for technical.json: its SHA-256 digest and declared record counts (AC-004).
    // Sources this story does not supply yet are declared with zero records.
    internal static readonly SourceFixtureManifest CoreManifest = new()
    {
        SchemaVersion = SourceFixtureManifest.SupportedSchemaVersion,
        SourceFixtureVersion = "2026.10.1",
        ScenarioDate = "2026-10-01",
        Sha256 = "f91580362c0291381eee10954dce58407a94eb553d223e29e334e68b629e5923",
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
