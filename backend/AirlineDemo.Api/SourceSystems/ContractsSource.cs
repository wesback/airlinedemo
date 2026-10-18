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

// Typed payload for contract obligation clauses: a kind-specific set of named string fields, read-only.
public sealed record ContractsPayload(IReadOnlyDictionary<string, string> Fields)
{
    private static readonly string[] ClauseFields =
        ["clauseId", "obligation", "noticeWindowStart", "noticeWindowEnd", "noticeWindowDays"];
    private static readonly string[] SpecialistFields = ["relatedRequirementId", "topic", "result", "regime"];

    public static ContractsPayload FromFields(IReadOnlyDictionary<string, string> fields)
    {
        if (fields.Keys.Any(key => SpecialistFields.Contains(key, StringComparer.Ordinal))
            || fields.Count != ClauseFields.Length
            || ClauseFields.Any(key => !fields.ContainsKey(key)))
        {
            _ = SpecialistReviewFlag.Parse(fields);
        }

        return new ContractsPayload(fields);
    }
}

// Synthetic obligation-clause source for the core case (PRD FR-002).
public sealed class ContractsSource()
    : FixtureSourceSystem<ContractsPayload>(
        "contracts",
        "AirlineDemo.Api.Fixtures.sources.contracts.json",
        CoreManifest,
        ContractsPayload.FromFields)
{
    // Signed manifest for contracts.json: its SHA-256 digest and declared record counts (AC-004).
    internal static readonly SourceFixtureManifest CoreManifest = new()
    {
        SchemaVersion = SourceFixtureManifest.SupportedSchemaVersion,
        SourceFixtureVersion = "2026.10.1",
        ScenarioDate = "2026-10-01",
        Sha256 = "226282e03277a27018fc58a9f117f515a4792fbb0c5a0b4e15daa2b7a321958a",
        RecordCounts = new Dictionary<string, int>
        {
            ["technical"] = 0,
            ["maintenance"] = 0,
            ["finance"] = 0,
            ["records"] = 0,
            ["contracts"] = 11,
        },
    };
}
