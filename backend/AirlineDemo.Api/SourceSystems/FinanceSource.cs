using System.Globalization;
using System.Text.RegularExpressions;

namespace AirlineDemo.Api.SourceSystems;

// Typed payload for finance records: a kind-specific set of named string fields, read-only.
public sealed record FinancePayload(IReadOnlyDictionary<string, string> Fields);

public static class FinanceRecordValidator
{
    private static readonly string[] CommonFields = ["recordedAmount", "collectedAmount", "currency", "period", "dueDate"];
    private static readonly Regex DecimalPattern = new(@"\A\d+(?:\.\d+)?\z", RegexOptions.CultureInvariant);
    private static readonly Regex CurrencyPattern = new(@"\A[A-Z]{3}\z", RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Validate(SourceRecord<FinancePayload> record)
    {
        var problems = new List<string>(record.Validate(record.CaseId));
        var fields = record.Payload?.Fields;
        if (fields is null)
        {
            problems.Add("Finance payload fields are required.");
            return problems;
        }

        var additional = record.Kind switch
        {
            "rent" or "security_deposit" => Array.Empty<string>(),
            "maintenance_reserve" => new[] { "rateBasis", "rate" },
            "reserve_reimbursement" => new[] { "claimId", "status" },
            _ => null,
        };
        if (additional is null)
        {
            problems.Add($"Unknown finance kind '{record.Kind}'.");
            return problems;
        }

        var required = CommonFields.Concat(additional).ToArray();
        foreach (var field in required.Where(field => !fields.ContainsKey(field)))
        {
            problems.Add($"Required finance field '{field}' is missing.");
        }
        foreach (var field in fields.Keys.Except(required, StringComparer.Ordinal))
        {
            problems.Add($"Unexpected finance field '{field}'.");
        }

        if (fields.TryGetValue("recordedAmount", out var recorded) && !IsDecimal(recorded)
            || fields.TryGetValue("collectedAmount", out var collected) && !IsDecimal(collected))
        {
            problems.Add("Finance amounts must be non-negative decimal strings.");
        }
        if (fields.TryGetValue("currency", out var currency) && (currency is null || !CurrencyPattern.IsMatch(currency))) problems.Add("Currency must be three uppercase letters.");
        if (fields.TryGetValue("period", out var period)
            && (period is null || !DateOnly.TryParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            problems.Add("Period must use YYYY-MM.");
        if (fields.TryGetValue("dueDate", out var dueDate)
            && (dueDate is null || !DateOnly.TryParseExact(dueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            problems.Add("Due date must use YYYY-MM-DD.");
        if (record.Kind == "maintenance_reserve")
        {
            if (fields.TryGetValue("rateBasis", out var basis) && basis is not ("per_flight_hour" or "per_cycle"))
                problems.Add("Rate basis must be per_flight_hour or per_cycle.");
            if (fields.TryGetValue("rate", out var rate) && !IsDecimal(rate))
                problems.Add("Rate must be a non-negative decimal string.");
        }
        if (record.Kind == "reserve_reimbursement")
        {
            if (fields.TryGetValue("claimId", out var claimId) && string.IsNullOrWhiteSpace(claimId))
                problems.Add("Claim id must be non-empty.");
            if (fields.TryGetValue("status", out var status) && status is not ("claimed" or "approved" or "paid"))
                problems.Add("Reimbursement status must be claimed, approved or paid.");
        }
        return problems;
    }

    private static bool IsDecimal(string? value) =>
        value is not null
        && DecimalPattern.IsMatch(value)
        && decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _);
}

// Synthetic lease and rate source for the core case (PRD FR-002, DD-019).
public sealed class FinanceSource()
    : FixtureSourceSystem<FinancePayload>(
        "finance",
        "AirlineDemo.Api.Fixtures.sources.finance.json",
        CoreManifest,
        fields => new FinancePayload(fields))
{
    // Signed manifest for finance.json: its SHA-256 digest and declared record counts (AC-004).
    // Sources this story does not supply yet are declared with zero records.
    internal static readonly SourceFixtureManifest CoreManifest = new()
    {
        SchemaVersion = SourceFixtureManifest.SupportedSchemaVersion,
        SourceFixtureVersion = SourceFixtureManifest.SupportedSourceFixtureVersion,
        ScenarioDate = "2026-10-01",
        Sha256 = "9858a0828bedbcb9e33e6ced062f2582abfdad452df4dc76b500202d338a8743",
        RecordCounts = new Dictionary<string, int>
        {
            ["technical"] = 0,
            ["maintenance"] = 0,
            ["finance"] = 7,
            ["records"] = 0,
            ["contracts"] = 0,
        },
    };
}
