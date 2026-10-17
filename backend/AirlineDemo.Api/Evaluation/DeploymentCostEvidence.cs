using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AirlineDemo.Api.Evaluation;

public static class DeploymentCostEvidence
{
    private const string NotMeasured = "not measured";

    private static readonly string[] RootFields =
        ["schemaVersion", "repositoryRevision", "deploymentId", "observations"];

    private static readonly string[] ObservationFields =
    [
        "scope", "category", "plannedAmount", "observedAmount", "varianceAmount", "currency",
        "periodStartUtc", "periodEndUtc", "plannedEvidenceReference", "observedEvidenceReference",
        "plannedEvidenceLabel", "observedEvidenceLabel", "varianceEvidenceLabel"
    ];

    private static readonly HashSet<string> Scopes =
        ["A", "B", "C", "D", "transition", "deployment"];

    private static readonly HashSet<string> Categories =
        ["cost-per-page", "cost-per-case", "transition-cost", "active", "idle", "residual"];

    private static readonly HashSet<string> PlannedLabels =
    [
        "case-study stipulation", "external evidence", "scenario assumption", "architectural decision",
        "synthetic demonstration result", "unvalidated target", "derived"
    ];

    private static readonly HashSet<string> CurrencyCodes = GetCurrencyCodes();

    public static decimal CalculateVariance(decimal plannedAmount, decimal observedAmount) =>
        observedAmount - plannedAmount;

    public static void ValidateJson(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        RequireKind(root, JsonValueKind.Object, "root");
        RequireExactFields(root, RootFields, "root");

        var version = root.GetProperty("schemaVersion");
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schemaVersion) ||
            schemaVersion != 1)
        {
            throw Invalid("schemaVersion must be the integer 1.");
        }

        var revision = ReadString(root.GetProperty("repositoryRevision"), "repositoryRevision");
        if (!Regex.IsMatch(revision, @"\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z", RegexOptions.CultureInvariant))
        {
            throw Invalid("repositoryRevision must be 40 or 64 lowercase hexadecimal characters.");
        }

        RequireNonEmpty(ReadString(root.GetProperty("deploymentId"), "deploymentId"), "deploymentId");
        var observations = root.GetProperty("observations");
        RequireKind(observations, JsonValueKind.Array, "observations");
        foreach (var observation in observations.EnumerateArray())
        {
            ValidateObservation(observation);
        }
    }

    private static void ValidateObservation(JsonElement observation)
    {
        RequireKind(observation, JsonValueKind.Object, "observation");
        RequireExactFields(observation, ObservationFields, "observation");

        var scope = ReadString(observation.GetProperty("scope"), "scope");
        if (!Scopes.Contains(scope))
        {
            throw Invalid("scope is not supported.");
        }

        var category = ReadString(observation.GetProperty("category"), "category");
        if (!Categories.Contains(category))
        {
            throw Invalid("category is not supported.");
        }

        var planned = ReadAmount(observation.GetProperty("plannedAmount"), "plannedAmount");
        var observed = ReadAmount(observation.GetProperty("observedAmount"), "observedAmount");
        var variance = ReadAmount(observation.GetProperty("varianceAmount"), "varianceAmount");
        var plannedReference = ReadString(observation.GetProperty("plannedEvidenceReference"), "plannedEvidenceReference");
        var observedReference = ReadString(observation.GetProperty("observedEvidenceReference"), "observedEvidenceReference");
        var plannedLabel = ReadString(observation.GetProperty("plannedEvidenceLabel"), "plannedEvidenceLabel");
        var observedLabel = ReadString(observation.GetProperty("observedEvidenceLabel"), "observedEvidenceLabel");
        var varianceLabel = ReadString(observation.GetProperty("varianceEvidenceLabel"), "varianceEvidenceLabel");

        ValidateEvidence(planned, plannedReference, plannedLabel, "plannedAmount");
        ValidateEvidence(observed, observedReference, observedLabel, "observedAmount");
        if (variance is null)
        {
            if (varianceLabel != NotMeasured)
            {
                throw Invalid("varianceEvidenceLabel must be not measured when varianceAmount is not measured.");
            }
        }
        else if (varianceLabel != "derived")
        {
            throw Invalid("varianceAmount evidence label must be derived.");
        }

        if (planned is null && observed is null)
        {
            if (variance is not null)
            {
                throw Invalid("varianceAmount must be not measured when a cost amount is not measured.");
            }
        }
        else if (planned is null || observed is null)
        {
            if (variance is not null)
            {
                throw Invalid("varianceAmount must be not measured when either cost amount is not measured.");
            }
        }
        else if (variance is null)
        {
            throw Invalid("varianceAmount must equal observedAmount minus plannedAmount.");
        }
        else
        {
            decimal calculatedVariance;
            try
            {
                calculatedVariance = CalculateVariance(planned.Value, observed.Value);
            }
            catch (OverflowException)
            {
                throw Invalid("varianceAmount is outside the supported numeric range.");
            }

            if (variance.Value != calculatedVariance)
            {
                throw Invalid("varianceAmount must equal observedAmount minus plannedAmount.");
            }
        }

        var currency = ReadString(observation.GetProperty("currency"), "currency");
        var allUnmeasured = planned is null && observed is null && variance is null;
        if (allUnmeasured)
        {
            if (currency != NotMeasured)
            {
                throw Invalid("currency must be not measured when all amounts are not measured.");
            }
        }
        else if (!CurrencyCodes.Contains(currency))
        {
            throw Invalid("currency must be an ISO 4217 alphabetic currency code.");
        }

        var start = ReadUtcTimestamp(observation.GetProperty("periodStartUtc"), "periodStartUtc");
        var end = ReadUtcTimestamp(observation.GetProperty("periodEndUtc"), "periodEndUtc");
        if (start >= end)
        {
            throw Invalid("periodStartUtc must precede periodEndUtc.");
        }
    }

    private static void ValidateEvidence(decimal? amount, string reference, string label, string field)
    {
        if (amount is null)
        {
            if (reference != NotMeasured || label != NotMeasured)
            {
                throw Invalid($"{field} evidence reference and label must be not measured when its amount is not measured.");
            }

            return;
        }

        RequireNonEmpty(reference, $"{field} evidence reference");
        if (reference == NotMeasured)
        {
            throw Invalid($"{field} evidence reference must identify its source when its amount is measured.");
        }

        var requiredLabel = field == "observedAmount" ? "external evidence" : null;
        if (requiredLabel is not null && label != requiredLabel)
        {
            throw Invalid($"{field} evidence label must be {requiredLabel}.");
        }

        if (field == "plannedAmount" && !PlannedLabels.Contains(label))
        {
            throw Invalid("plannedEvidenceLabel is not supported.");
        }
    }

    private static decimal? ReadAmount(JsonElement element, string field)
    {
        if (element.ValueKind == JsonValueKind.String && element.GetString() == NotMeasured)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var amount))
        {
            return amount;
        }

        throw Invalid($"{field} must be a JSON number or the exact string not measured.");
    }

    private static DateTimeOffset ReadUtcTimestamp(JsonElement element, string field)
    {
        var value = ReadString(element, field);
        if ((!value.EndsWith('Z') && !value.EndsWith("+00:00", StringComparison.Ordinal)) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp) ||
            timestamp.Offset != TimeSpan.Zero)
        {
            throw Invalid($"{field} must be a UTC timestamp.");
        }

        return timestamp;
    }

    private static string ReadString(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"{field} must be a string.");
        }

        return element.GetString()!;
    }

    private static void RequireNonEmpty(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid($"{field} must be non-empty.");
        }
    }

    private static void RequireExactFields(JsonElement element, string[] expectedFields, string field)
    {
        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != expectedFields.Length ||
            names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            !names.Order(StringComparer.Ordinal).SequenceEqual(expectedFields.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw Invalid($"{field} contains missing, duplicate, or unknown fields.");
        }
    }

    private static void RequireKind(JsonElement element, JsonValueKind expected, string field)
    {
        if (element.ValueKind != expected)
        {
            throw Invalid($"{field} must be {expected}.");
        }
    }

    private static HashSet<string> GetCurrencyCodes()
    {
        var codes = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Select(culture => new RegionInfo(culture.Name).ISOCurrencySymbol)
            .ToHashSet(StringComparer.Ordinal);
        codes.UnionWith(["XAU", "XAG", "XPT", "XPD", "XDR", "XTS", "XXX"]);
        return codes;
    }

    private static JsonException Invalid(string message) => new(message);
}
