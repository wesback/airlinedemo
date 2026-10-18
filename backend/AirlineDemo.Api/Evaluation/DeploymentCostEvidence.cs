using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirlineDemo.Api.Evaluation;

public sealed record DeploymentCostEvidence(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("repositoryRevision")] string RepositoryRevision,
    [property: JsonPropertyName("deploymentId")] string DeploymentId,
    [property: JsonPropertyName("observations")] IReadOnlyList<DeploymentCostObservation> Observations)
{
    private static readonly string[] RootFields = ["schemaVersion", "repositoryRevision", "deploymentId", "observations"];
    private static readonly string[] ObservationFields =
    [
        "scope", "category", "plannedAmount", "observedAmount", "varianceAmount", "currency",
        "periodStartUtc", "periodEndUtc", "plannedEvidenceReference", "observedEvidenceReference",
        "plannedEvidenceLabel", "observedEvidenceLabel", "varianceEvidenceLabel"
    ];
    private static readonly string[] PlannedLabels =
    [
        "case-study stipulation", "external evidence", "scenario assumption", "architectural decision",
        "synthetic demonstration result", "unvalidated target", "derived"
    ];
    private static readonly string[] Scopes = ["A", "B", "C", "D", "transition", "deployment"];
    private static readonly string[] Categories =
        ["cost-per-page", "cost-per-case", "transition-cost", "active", "idle", "residual"];
    private static readonly HashSet<string> Iso4217Currencies =
    [
        "AED", "AFN", "ALL", "AMD", "ANG", "AOA", "ARS", "AUD", "AWG", "AZN",
        "BAM", "BBD", "BDT", "BGN", "BHD", "BIF", "BMD", "BND", "BOB", "BOV",
        "BRL", "BSD", "BTN", "BWP", "BYN", "BZD", "CAD", "CDF", "CHE", "CHF",
        "CHW", "CLF", "CLP", "CNY", "COP", "COU", "CRC", "CUC", "CUP", "CVE",
        "CZK", "DJF", "DKK", "DOP", "DZD", "EGP", "ERN", "ETB", "EUR", "FJD",
        "FKP", "GBP", "GEL", "GHS", "GIP", "GMD", "GNF", "GTQ", "GYD", "HKD",
        "HNL", "HTG", "HUF", "IDR", "ILS", "INR", "IQD", "IRR", "ISK", "JMD",
        "JOD", "JPY", "KES", "KGS", "KHR", "KMF", "KPW", "KRW", "KWD", "KYD",
        "KZT", "LAK", "LBP", "LKR", "LRD", "LSL", "LYD", "MAD", "MDL", "MGA",
        "MKD", "MMK", "MNT", "MOP", "MRU", "MUR", "MVR", "MWK", "MXN", "MXV",
        "MYR", "MZN", "NAD", "NGN", "NIO", "NOK", "NPR", "NZD", "OMR", "PAB",
        "PEN", "PGK", "PHP", "PKR", "PLN", "PYG", "QAR", "RON", "RSD", "RUB",
        "RWF", "SAR", "SBD", "SCR", "SDG", "SEK", "SGD", "SHP", "SLE", "SLL",
        "SOS", "SRD", "SSP", "STN", "SVC", "SYP", "SZL", "THB", "TJS", "TMT",
        "TND", "TOP", "TRY", "TTD", "TWD", "TZS", "UAH", "UGX", "USD", "USN",
        "UYI", "UYU", "UYW", "UZS", "VED", "VES", "VND", "VUV", "WST", "XAF",
        "XAG", "XAU", "XBA", "XBB", "XBC", "XBD", "XCD", "XCG", "XDR", "XOF",
        "XPD", "XPF", "XPT", "XSU", "XTS", "XUA", "XXX", "YER", "ZAR", "ZMW",
        "ZWG"
    ];

    public static DeploymentCostEvidence Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        RequireFields(root, RootFields);
        if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number ||
            root.GetProperty("observations").ValueKind != JsonValueKind.Array)
            throw new JsonException("Invalid deployment cost evidence types.");
        RequireString(root, "repositoryRevision");
        RequireString(root, "deploymentId");
        foreach (var observation in root.GetProperty("observations").EnumerateArray())
        {
            RequireFields(observation, ObservationFields);
            foreach (var field in ObservationFields.Where(field =>
                         field is not "plannedAmount" and not "observedAmount" and not "varianceAmount"))
                RequireString(observation, field);
        }

        var evidence = JsonSerializer.Deserialize<DeploymentCostEvidence>(json)
            ?? throw new JsonException("Deployment cost evidence is required.");
        evidence.Validate();
        return evidence;
    }

    public string ToJson()
    {
        Validate();
        return JsonSerializer.Serialize(this);
    }

    public void Validate()
    {
        if (SchemaVersion != 1)
            throw new ArgumentException("Unsupported deployment cost evidence schema version.");
        if (RepositoryRevision is null || !System.Text.RegularExpressions.Regex.IsMatch(
                RepositoryRevision, @"\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z"))
            throw new ArgumentException("Repository revision must be a 40- or 64-character lowercase hexadecimal value.");
        if (string.IsNullOrWhiteSpace(DeploymentId))
            throw new ArgumentException("Deployment ID must be non-empty.");
        if (Observations is null)
            throw new ArgumentException("Observations are required.");

        foreach (var observation in Observations)
        {
            if (observation is null ||
                observation.Scope is null || observation.Category is null || observation.Currency is null ||
                observation.PeriodStartUtc is null || observation.PeriodEndUtc is null ||
                observation.PlannedEvidenceReference is null || observation.ObservedEvidenceReference is null ||
                observation.PlannedEvidenceLabel is null || observation.ObservedEvidenceLabel is null ||
                observation.VarianceEvidenceLabel is null)
                throw new ArgumentException("Observation string fields cannot be null.");

            ValidateAmount(observation.PlannedAmount, observation.PlannedEvidenceReference,
                observation.PlannedEvidenceLabel, "planned", PlannedLabels);
            ValidateAmount(observation.ObservedAmount, observation.ObservedEvidenceReference,
                observation.ObservedEvidenceLabel, "observed", ["external evidence"]);
            var allUnmeasured = !observation.PlannedAmount.IsMeasured &&
                !observation.ObservedAmount.IsMeasured && !observation.VarianceAmount.IsMeasured;
            if (!Scopes.Contains(observation.Scope) && !(allUnmeasured && observation.Scope == "not measured"))
                throw new ArgumentException("Scope must be an allowed value.");
            if (!Categories.Contains(observation.Category) && !(allUnmeasured && observation.Category == "not measured"))
                throw new ArgumentException("Category must be an allowed value.");
            if ((observation.Currency == "not measured") != allUnmeasured ||
                (!allUnmeasured && !Iso4217Currencies.Contains(observation.Currency)))
                throw new ArgumentException("Currency must be an uppercase ISO 4217 alphabetic code or not measured when all amounts are unmeasured.");

            DateTimeOffset? periodStart = null;
            DateTimeOffset? periodEnd = null;
            if (!allUnmeasured || observation.PeriodStartUtc != "not measured")
                periodStart = ParseUtc(observation.PeriodStartUtc);
            if (!allUnmeasured || observation.PeriodEndUtc != "not measured")
                periodEnd = ParseUtc(observation.PeriodEndUtc);
            if (periodStart is not null && periodEnd is not null && periodEnd <= periodStart)
                throw new ArgumentException("Period end must be later than period start.");

            if (observation.VarianceAmount.IsMeasured)
            {
                if (!observation.PlannedAmount.IsMeasured || !observation.ObservedAmount.IsMeasured ||
                    observation.VarianceEvidenceLabel != "derived")
                    throw new ArgumentException("Measured variance requires measured inputs and the derived label.");
            }
            else if (observation.VarianceEvidenceLabel != "not measured")
                throw new ArgumentException("Unmeasured variance requires the not measured label.");

            if (observation.PlannedAmount.IsMeasured && observation.ObservedAmount.IsMeasured)
            {
                if (!observation.VarianceAmount.IsMeasured ||
                    observation.VarianceAmount.Value != observation.ObservedAmount.Value - observation.PlannedAmount.Value)
                    throw new ArgumentException("Variance must equal observed cost minus planned cost.");
            }
            else if (observation.VarianceAmount.IsMeasured)
                throw new ArgumentException("Variance cannot be measured when either cost is unmeasured.");
        }
    }

    public static DeploymentCostAmount CalculateVariance(DeploymentCostAmount planned, DeploymentCostAmount observed) =>
        planned.IsMeasured && observed.IsMeasured
            ? DeploymentCostAmount.Measured(observed.Value!.Value - planned.Value!.Value)
            : DeploymentCostAmount.NotMeasured;

    private static void ValidateAmount(
        DeploymentCostAmount amount, string reference, string label, string name, IReadOnlyCollection<string> acceptedLabels)
    {
        if (amount.IsMeasured)
        {
            if (string.IsNullOrWhiteSpace(reference) || !acceptedLabels.Contains(label))
                throw new ArgumentException($"Measured {name} amount requires a reference and an allowed evidence label.");
        }
        else if (reference != "not measured" || label != "not measured")
            throw new ArgumentException($"Unmeasured {name} amount requires not measured reference and label.");
    }

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : throw new ArgumentException("Period values must be UTC timestamps.");

    private static void RequireFields(JsonElement element, IReadOnlyCollection<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException("Evidence records must be JSON objects.");
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Count || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            !actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            throw new JsonException("Evidence record has missing, duplicate, or unknown fields.");
    }

    private static void RequireString(JsonElement element, string field)
    {
        if (element.GetProperty(field).ValueKind != JsonValueKind.String)
            throw new JsonException($"'{field}' must be a JSON string.");
    }
}

public sealed record DeploymentCostObservation(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("plannedAmount")] DeploymentCostAmount PlannedAmount,
    [property: JsonPropertyName("observedAmount")] DeploymentCostAmount ObservedAmount,
    [property: JsonPropertyName("varianceAmount")] DeploymentCostAmount VarianceAmount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("periodStartUtc")] string PeriodStartUtc,
    [property: JsonPropertyName("periodEndUtc")] string PeriodEndUtc,
    [property: JsonPropertyName("plannedEvidenceReference")] string PlannedEvidenceReference,
    [property: JsonPropertyName("observedEvidenceReference")] string ObservedEvidenceReference,
    [property: JsonPropertyName("plannedEvidenceLabel")] string PlannedEvidenceLabel,
    [property: JsonPropertyName("observedEvidenceLabel")] string ObservedEvidenceLabel,
    [property: JsonPropertyName("varianceEvidenceLabel")] string VarianceEvidenceLabel)
{
}

[JsonConverter(typeof(DeploymentCostAmountConverter))]
public readonly record struct DeploymentCostAmount
{
    private DeploymentCostAmount(decimal? value) => Value = value;

    public decimal? Value { get; }
    public bool IsMeasured => Value.HasValue;
    public static DeploymentCostAmount NotMeasured => new(null);
    public static DeploymentCostAmount Measured(decimal value) => new(value);
}

public sealed class DeploymentCostAmountConverter : JsonConverter<DeploymentCostAmount>
{
    public override DeploymentCostAmount Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetDecimal(out var value) => DeploymentCostAmount.Measured(value),
            JsonTokenType.String when reader.GetString() == "not measured" => DeploymentCostAmount.NotMeasured,
            _ => throw new JsonException("Cost amount must be a JSON number or the exact string 'not measured'.")
        };

    public override void Write(Utf8JsonWriter writer, DeploymentCostAmount value, JsonSerializerOptions options)
    {
        if (value.Value is decimal amount)
            writer.WriteNumberValue(amount);
        else
            writer.WriteStringValue("not measured");
    }
}
