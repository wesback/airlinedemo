using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirlineDemo.Api.SourceSystems;

public sealed record SpecialistReviewFlag(
    string RelatedRequirementId,
    string Topic,
    string Result,
    string? Regime = null)
{
    private static readonly string[] Topics = ["sanctions", "cape_town", "ifrs16", "gdpr", "other"];
    private static readonly string[] Results = ["specialist_review_required", "not_assessed"];
    private static readonly string[] Regimes = ["EU", "US", "UK"];
    private static readonly string[] Fields = ["relatedRequirementId", "topic", "result", "regime"];

    public static bool TryParse(IReadOnlyDictionary<string, string> fields, out SpecialistReviewFlag? flag)
    {
        ArgumentNullException.ThrowIfNull(fields);
        flag = null;
        if (fields.Keys.Any(key => !Fields.Contains(key, StringComparer.Ordinal))
            || !fields.TryGetValue("relatedRequirementId", out var relatedRequirementId)
            || string.IsNullOrWhiteSpace(relatedRequirementId)
            || !fields.TryGetValue("topic", out var topic)
            || !Topics.Contains(topic, StringComparer.Ordinal)
            || !fields.TryGetValue("result", out var result)
            || !Results.Contains(result, StringComparer.Ordinal))
        {
            return false;
        }

        if (topic == "sanctions")
        {
            if (fields.Count != 4
                || !fields.TryGetValue("regime", out var regime)
                || !Regimes.Contains(regime, StringComparer.Ordinal))
            {
                return false;
            }

            flag = new SpecialistReviewFlag(relatedRequirementId, topic, result, regime);
            return true;
        }

        if (fields.Count != 3 || fields.ContainsKey("regime"))
            return false;

        flag = new SpecialistReviewFlag(relatedRequirementId, topic, result);
        return true;
    }

    public static SpecialistReviewFlag Parse(IReadOnlyDictionary<string, string> fields) =>
        TryParse(fields, out var flag)
            ? flag!
            : throw new InvalidDataException("Contracts specialist_flag payload is invalid.");
}

public sealed record CapeTownInterestEvidence(
    string InternationalInterestRef,
    string RegistrationRef,
    string Idera,
    bool StateDeclarationNoted);

public sealed record ContractsPayload(
    IReadOnlyDictionary<string, string> Fields,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    CapeTownInterestEvidence? CapeTownInterest = null)
{
    private static readonly string[] ClauseFields =
        ["clauseId", "obligation", "noticeWindowStart", "noticeWindowEnd", "noticeWindowDays"];
    private static readonly string[] SpecialistFields = ["relatedRequirementId", "topic", "result", "regime"];

    public static ContractsPayload FromFields(IReadOnlyDictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Keys.Any(key => SpecialistFields.Contains(key, StringComparer.Ordinal))
            || fields.Count != ClauseFields.Length
            || ClauseFields.Any(key => !fields.ContainsKey(key)))
        {
            _ = SpecialistReviewFlag.Parse(fields);
        }

        return new ContractsPayload(fields);
    }

    internal static ContractsPayload FromJsonPayload(string kind, JsonElement payload)
    {
        if (kind == "cape_town_interest")
        {
            var interest = ParseCapeTownInterest(payload, false);
            var fields = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["internationalInterestRef"] = interest.InternationalInterestRef,
                ["registrationRef"] = interest.RegistrationRef,
                ["idera"] = interest.Idera,
                ["stateDeclarationNoted"] = interest.StateDeclarationNoted ? "true" : "false",
            });
            return new ContractsPayload(fields, interest);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Contracts payload must be an object.");
        foreach (var property in payload.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String || !values.TryAdd(property.Name, property.Value.GetString()!))
                throw new InvalidDataException("Contracts payload fields must be unique strings.");
        }
        return FromFields(new ReadOnlyDictionary<string, string>(values));
    }
    internal static CapeTownInterestEvidence ParsePinnedCapeTownInterest(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("fields", out var fields))
            return ParseCapeTownInterest(fields, true);
        return ParseCapeTownInterest(payload, false);
    }

    private static CapeTownInterestEvidence ParseCapeTownInterest(JsonElement payload, bool allowStringBoolean)
    {
        string[] fields = ["internationalInterestRef", "registrationRef", "idera", "stateDeclarationNoted"];
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Contracts cape_town_interest payload is invalid.");
        var properties = payload.EnumerateObject().ToArray();
        if (properties.Length != fields.Length
            || properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length
            || fields.Any(field => !payload.TryGetProperty(field, out _)))
        {
            throw new InvalidDataException("Contracts cape_town_interest payload is invalid.");
        }

        var internationalInterestRef = payload.GetProperty("internationalInterestRef");
        var registrationRef = payload.GetProperty("registrationRef");
        var idera = payload.GetProperty("idera");
        var stateDeclarationNoted = payload.GetProperty("stateDeclarationNoted");
        if (internationalInterestRef.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(internationalInterestRef.GetString())
            || registrationRef.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(registrationRef.GetString())
            || idera.ValueKind != JsonValueKind.String
            || !new[] { "present", "absent", "not_recorded" }.Contains(idera.GetString(), StringComparer.Ordinal)
            || stateDeclarationNoted.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                && !(allowStringBoolean && stateDeclarationNoted.ValueKind == JsonValueKind.String
                    && stateDeclarationNoted.GetString() is "true" or "false"))
        {
            throw new InvalidDataException("Contracts cape_town_interest payload is invalid.");
        }

        return new CapeTownInterestEvidence(
            internationalInterestRef.GetString()!,
            registrationRef.GetString()!,
            idera.GetString()!,
            stateDeclarationNoted.ValueKind == JsonValueKind.True
                || stateDeclarationNoted.ValueKind == JsonValueKind.String && stateDeclarationNoted.GetString() == "true");
    }
}

// Synthetic contracts source; parsing stages the complete immutable record list before publishing it.
public sealed class ContractsSource : ISourceSystem<ContractsPayload>
{
    private const string ResourceName = "AirlineDemo.Api.Fixtures.sources.contracts.json";
    private readonly IReadOnlyList<SourceRecord<ContractsPayload>> records;

    public ContractsSource()
    {
        using var stream = typeof(ContractsSource).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded source fixture '{ResourceName}' is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        records = ParseFixture(ResourceName, CoreManifest, buffer.ToArray());
    }
    public Task<SourceReadResult<IReadOnlyList<SourceRecord<ContractsPayload>>>> ListAsync(
        string caseId,
        CancellationToken cancellationToken = default,
        int? maxRecords = null)
    {
        if (maxRecords is <= 0) throw new ArgumentOutOfRangeException(nameof(maxRecords));
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SourceRecord<ContractsPayload>> scoped = records
            .Where(record => record.CaseId == caseId)
            .Take(maxRecords ?? int.MaxValue)
            .ToArray();
        return Task.FromResult<SourceReadResult<IReadOnlyList<SourceRecord<ContractsPayload>>>>(
            new SourceReadSucceeded<IReadOnlyList<SourceRecord<ContractsPayload>>>(scoped));
    }
    public Task<SourceReadResult<SourceRecord<ContractsPayload>>> GetAsync(
        string caseId,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var match = records.FirstOrDefault(record => record.CaseId == caseId && record.RecordId == recordId);
        SourceReadResult<SourceRecord<ContractsPayload>> result = match is null
            ? new SourceRecordNotFound<SourceRecord<ContractsPayload>>(recordId)
            : new SourceReadSucceeded<SourceRecord<ContractsPayload>>(match);
        return Task.FromResult(result);
    }
    internal static IReadOnlyList<SourceRecord<ContractsPayload>> ParseFixture(
        string resourceName,
        SourceFixtureManifest manifest,
        byte[] content)
    {
        var manifestProblems = manifest.Validate();
        if (manifestProblems.Count > 0)
            throw new InvalidOperationException($"Source manifest for 'contracts' is invalid: {string.Join(" ", manifestProblems)}");
        if (manifest.ScenarioDate != "2026-10-01")
            throw new InvalidOperationException($"Source manifest for 'contracts' declares scenarioDate '{manifest.ScenarioDate}', expected '2026-10-01'.");
        var digest = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!string.Equals(manifest.Sha256, digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Source fixture '{resourceName}' does not match its signed digest.");
        FixtureFile file;
        try
        {
            file = JsonSerializer.Deserialize<FixtureFile>(content, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException($"Source fixture '{resourceName}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Source fixture '{resourceName}' is not valid JSON.", exception);
        }

        if (file.SourceId != "contracts")
            throw new InvalidOperationException($"Source fixture '{resourceName}' declares sourceId '{file.SourceId}', expected 'contracts'.");
        if (!manifest.RecordCounts.TryGetValue("contracts", out var declaredCount) || declaredCount != file.Records.Count)
            throw new InvalidOperationException($"Source fixture '{resourceName}' has {file.Records.Count} records; manifest declares {declaredCount}.");
        var recordIds = new HashSet<string>(StringComparer.Ordinal);
        var parsed = new List<SourceRecord<ContractsPayload>>();
        foreach (var dto in file.Records)
        {
            var record = new SourceRecord<ContractsPayload>
            {
                SourceId = "contracts",
                RecordId = dto.RecordId,
                RecordVersion = dto.RecordVersion,
                AsOf = dto.AsOf,
                CaseId = dto.CaseId,
                Kind = dto.Kind,
                Stale = dto.AsOf < new DateOnly(2026, 10, 1),
                Payload = ContractsPayload.FromJsonPayload(dto.Kind, dto.Payload),
                Synthetic = dto.Synthetic,
            };
            var problems = record.Validate(record.CaseId);
            if (problems.Count > 0)
                throw new InvalidOperationException($"Source fixture '{resourceName}' record '{dto.RecordId}' is invalid: {string.Join(" ", problems)}");
            if (!recordIds.Add(record.RecordId))
                throw new InvalidOperationException($"Source fixture '{resourceName}' repeats recordId '{record.RecordId}'.");
            parsed.Add(record);
        }
        return parsed.AsReadOnly();
    }
    private sealed record FixtureFile(string SourceId, List<FixtureRecord> Records);
    private sealed record FixtureRecord(
        string RecordId, int RecordVersion, DateOnly AsOf, string CaseId, string Kind, bool Synthetic, JsonElement Payload);

    // Signed manifest for contracts.json: its SHA-256 digest and declared record counts (AC-004).
    internal static readonly SourceFixtureManifest CoreManifest = new()
    {
        SchemaVersion = SourceFixtureManifest.SupportedSchemaVersion,
        SourceFixtureVersion = SourceFixtureManifest.SupportedSourceFixtureVersion,
        ScenarioDate = "2026-10-01",
        Sha256 = "b3e7cbc75cb162baba2df9afd754110450e86929bbcac4244e8f09c38835f981",
        RecordCounts = new Dictionary<string, int>
        {
            ["technical"] = 0,
            ["maintenance"] = 0,
            ["finance"] = 0,
            ["records"] = 0,
            ["contracts"] = 12,
        },
    };
}
