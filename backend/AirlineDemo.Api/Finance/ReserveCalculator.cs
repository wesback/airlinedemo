using System.Collections.Frozen;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.SourceSystems;
using Microsoft.AspNetCore.Http;

namespace AirlineDemo.Api.Finance;

public enum ReserveDraftStatus
{
    Blocked,
    Discrepancy,
    ReadyForReview
}

public enum FinanceDecisionKind
{
    Approve,
    Return
}

public sealed record UsageEntry(string ContractLine, decimal? Units, decimal? ReportedAmount = null);

public sealed record UsageReport(string ReportId, string Period, IReadOnlyList<UsageEntry> Entries, decimal? ReportedTotal = null);

public sealed record RateEntry(string ContractLine, string Period, decimal UnitRate);

public sealed record RateReport(string ReportId, string Period, IReadOnlyList<RateEntry> Entries);

public sealed record ReserveLine(string ContractLine, decimal Units, decimal UnitRate, decimal Amount, decimal? ReportedAmount)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SourceRecordCitation? UtilizationSource { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SourceRecordCitation? RateSource { get; init; }
}

public sealed record BlockReason(string Code, string Detail);

public sealed record ReserveDraft(
    string DraftId,
    int Version,
    string Period,
    ReserveDraftStatus Status,
    string? UsageReportId,
    string? RateReportId,
    IReadOnlyList<ReserveLine> Lines,
    decimal? PayableTotal,
    IReadOnlyList<BlockReason> Blockers,
    IReadOnlyList<string> Discrepancies,
    string CalculationBasis);

public sealed record FinanceReserveLine(
    string RecordId,
    string Kind,
    string Currency,
    decimal Billed,
    decimal Collected,
    decimal Underbilled,
    decimal Overdue,
    decimal Reimbursable)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SourceRecordCitation? Source { get; init; }
}

public sealed record SourceRecordCitation(string SourceId, string RecordId, int RecordVersion);
public sealed record SourceFedReserveDraft(string DraftId, int Version, string Period, ReserveDraftStatus Status,
    string? UsageReportId, string? RateReportId, IReadOnlyList<ReserveLine> Lines, decimal? PayableTotal,
    IReadOnlyList<BlockReason> Blockers, IReadOnlyList<string> Discrepancies, string CalculationBasis,
    IReadOnlyList<FinanceReserveLine> FinanceLines, decimal? FinanceTotal);

public sealed record FinanceReserveDraft(
    string DraftId,
    int Version,
    string Period,
    ReserveDraftStatus Status,
    IReadOnlyList<FinanceReserveLine> Lines,
    decimal? Total,
    IReadOnlyList<BlockReason> Blockers,
    string CalculationBasis);

public sealed record FinanceReviewRecord(
    string DraftId,
    int DraftVersion,
    FinanceDecisionKind Decision,
    string ActorId,
    string Reason,
    DateTimeOffset DecidedAt,
    string CalculationBasis,
    decimal? PayableTotal,
    bool Simulated,
    bool InvoiceIssued);

public static class ReserveCalculator
{
    public const string CalculationBasis =
        "Units x unit rate per contract line; each line rounded to cents (midpoint away from zero) before summing; total = sum of rounded lines.";
    public const string FinanceCalculationBasis =
        "Finance lines report billed, collected, underbilled, overdue and reimbursable amounts; total = sum of underbilled amounts. This draft makes no profit, revenue, or cash-recognition claim.";

    private static readonly Regex PeriodPattern = new(@"^\d{4}-(0[1-9]|1[0-2])$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Drafts returned by <see cref="Calculate"/>, by reference, with a snapshot of their full contents taken at issue time.
    /// Hand-built or copied (<c>with</c>) drafts are not members; drafts whose lines or fields were edited afterwards no longer match the snapshot.
    /// </summary>
    private static readonly ConditionalWeakTable<ReserveDraft, string> IssuedDrafts = new();

    private sealed record PinnedRecord(SourceRecordCitation Citation, string Kind, DateOnly AsOf, bool Stale,
        IReadOnlyDictionary<string, string> Fields);
    private sealed record SourcePinDocument(string Format, IReadOnlyList<SourceBasisPin> Pins);
    private sealed record ReserveRate(PinnedRecord Record, string Basis, decimal Rate);

    private static readonly JsonSerializerOptions PinnedSourceJson = new(JsonSerializerDefaults.Web);

    public const string SourceFedCalculationBasis = CalculationBasis + " " + FinanceCalculationBasis;

    public static FinanceReserveDraft CalculateFinanceRecords(
        string draftId, int version, string period, IEnumerable<SourceRecord<FinancePayload>> records)
    {
        ValidateNonBlank(draftId, nameof(draftId));
        if (version < 1)
        {
            throw new ArgumentException("Version must be at least 1.", nameof(version));
        }
        ValidatePeriod(period, nameof(period));
        ArgumentNullException.ThrowIfNull(records);

        var selected = records.Where(record => record.Kind is "maintenance_reserve" or "reserve_reimbursement").ToArray();
        var blockers = new List<BlockReason>();
        foreach (var record in selected)
        {
            var problems = FinanceRecordValidator.Validate(record);
            if (problems.Count > 0)
            {
                blockers.Add(new BlockReason("finance-record-invalid", $"Record {record.RecordId}: {string.Join(" ", problems)}"));
            }
        }
        if (blockers.Count > 0)
        {
            return new FinanceReserveDraft(draftId, version, period, ReserveDraftStatus.Blocked, [], null, blockers, FinanceCalculationBasis);
        }

        var year = int.Parse(period[..4], CultureInfo.InvariantCulture);
        var month = int.Parse(period[5..], CultureInfo.InvariantCulture);
        var lastDay = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var lines = selected.Select(record =>
        {
            var fields = record.Payload.Fields;
            var billed = decimal.Parse(fields["recordedAmount"], CultureInfo.InvariantCulture);
            var collected = decimal.Parse(fields["collectedAmount"], CultureInfo.InvariantCulture);
            var underbilled = Math.Max(billed - collected, 0m);
            var dueDate = DateOnly.ParseExact(fields["dueDate"], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var reimbursable = record.Kind == "reserve_reimbursement"
                && fields["status"] is ("approved" or "paid") ? billed : 0m;
            return new FinanceReserveLine(record.RecordId, record.Kind, fields["currency"], billed, collected,
                underbilled, dueDate < lastDay ? underbilled : 0m, reimbursable);
        }).ToArray();
        return new FinanceReserveDraft(draftId, version, period, ReserveDraftStatus.ReadyForReview,
            lines, lines.Sum(line => line.Underbilled), [], FinanceCalculationBasis);
    }

    /// <summary>Calculates a reserve draft only from source records committed in a case basis.</summary>
    public static SourceFedReserveDraft CalculateFromPinnedSources(string draftId, int version, string period,
        string caseId, string? basisContent, IReadOnlyDictionary<string, string> sourceModes)
    {
        ValidateNonBlank(draftId, nameof(draftId));
        if (version < 1) throw new ArgumentException("Version must be at least 1.", nameof(version));
        ValidatePeriod(period, nameof(period));
        ValidateNonBlank(caseId, nameof(caseId));
        ArgumentNullException.ThrowIfNull(sourceModes);

        var blockedSource = new[] { "technical", "finance" }
            .FirstOrDefault(sourceId => sourceModes.GetValueOrDefault(sourceId, "available") != "available");
        if (blockedSource is not null)
        {
            var mode = sourceModes[blockedSource];
            var reason = mode switch { "unavailable" => "source_unavailable", "stale" => "source_stale",
                "conflicting" => "source_conflict", _ => "source_mode_invalid" };
            return SourceFedBlocked(draftId, version, period, reason, $"Required source '{blockedSource}' is {mode}.", []);
        }

        if (string.IsNullOrWhiteSpace(basisContent))
            return SourceFedBlocked(draftId, version, period, "source-basis-missing", "No committed source-record basis is available.", []);

        IReadOnlyList<PinnedRecord> pins;
        try
        {
            pins = ParsePinnedRecords(basisContent, caseId);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or OverflowException)
        { return SourceFedBlocked(draftId, version, period, "source-basis-invalid", "The committed source-record basis is invalid.", []); }

        var periodStart = DateOnly.ParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var periodEnd = new DateOnly(periodStart.Year, periodStart.Month, DateTime.DaysInMonth(periodStart.Year, periodStart.Month));
        var financeRecords = pins.Where(pin => pin.Citation.SourceId == "finance").ToArray();
        var start = periodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = periodEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var utilizationCandidates = pins.Where(record => record.Citation.SourceId == "technical" && record.Kind == "utilization"
            && record.Fields.GetValueOrDefault("periodStart") == start && record.Fields.GetValueOrDefault("periodEnd") == end).ToArray();
        if (utilizationCandidates.Length != 1)
        {
            var missing = utilizationCandidates.Length == 0;
            return SourceFedBlocked(draftId, version, period, missing ? "utilization-missing" : "utilization-ambiguous",
                missing ? $"No technical utilization record covers {period}." : $"More than one technical utilization record covers {period}.", []);
        }

        var utilization = utilizationCandidates[0];
        if (utilization.Stale)
            return SourceFedBlocked(draftId, version, period, "source_stale", "Required source 'technical' has stale utilization data.", []);

        var rateCandidates = new List<ReserveRate>();
        foreach (var record in financeRecords.Where(record => record.Kind == "maintenance_reserve"))
        {
            if (!RateAppliesToPeriod(record, period, periodEnd)) continue;

            if (!record.Fields.TryGetValue("rateBasis", out var basis)
                || basis is not ("per_flight_hour" or "per_cycle"))
                return SourceFedBlocked(draftId, version, period, "finance-record-invalid",
                    $"Record {record.Citation.RecordId} has an invalid or missing rateBasis.", []);
            if (!record.Fields.TryGetValue("rate", out var rateText)
                || !TryDecimal(rateText, out var rate))
                return SourceFedBlocked(draftId, version, period, "finance-record-invalid",
                    $"Record {record.Citation.RecordId} has an invalid or missing rate.", []);
            if (record.Stale)
                return SourceFedBlocked(draftId, version, period, "source_stale", "Required source 'finance' has stale maintenance-reserve data.", []);
            rateCandidates.Add(new ReserveRate(record, basis, rate));
        }
        rateCandidates = rateCandidates.OrderBy(candidate => candidate.Basis == "per_flight_hour" ? 0 : 1).ToList();

        if (rateCandidates.Count == 0)
            return SourceFedBlocked(draftId, version, period, "rate-report-missing", $"No finance maintenance-reserve rate applies to {period}.", []);
        if (rateCandidates.GroupBy(candidate => candidate.Basis, StringComparer.Ordinal).Any(group => group.Count() > 1))
            return SourceFedBlocked(draftId, version, period, "rate-coverage-overlap", $"Overlapping finance maintenance-reserve rates apply to {period}.", []);

        var usageEntries = new List<UsageEntry>();
        var rateEntries = new List<RateEntry>();
        foreach (var rate in rateCandidates)
        {
            var field = rate.Basis == "per_flight_hour" ? "hours" : "cycles";
            if (!utilization.Fields.TryGetValue(field, out var unitsText) || !TryDecimal(unitsText, out var units))
                return SourceFedBlocked(draftId, version, period, "usage-missing", $"Required technical utilization field '{field}' is missing or invalid.", []);
            usageEntries.Add(new UsageEntry(rate.Basis, units));
            rateEntries.Add(new RateEntry(rate.Basis, period, rate.Rate));
        }

        var reserveCalculation = Calculate(
            draftId,
            version,
            period,
            new UsageReport(utilization.Citation.RecordId, period, usageEntries),
            new RateReport(string.Join(",", rateCandidates.Select(candidate => candidate.Record.Citation.RecordId)), period, rateEntries));
        if (reserveCalculation.Status == ReserveDraftStatus.Blocked)
        {
            return SourceFedBlocked(draftId, version, period,
                reserveCalculation.Blockers.FirstOrDefault()?.Code ?? "reserve-input-invalid",
                reserveCalculation.Blockers.FirstOrDefault()?.Detail ?? "Reserve inputs are incomplete.", []);
        }

        var financeAmountRecords = new List<SourceRecord<FinancePayload>>();
        var financeCitations = new Dictionary<string, SourceRecordCitation>(StringComparer.Ordinal);
        foreach (var record in financeRecords.Where(record =>
                     record.Kind is "maintenance_reserve" or "reserve_reimbursement"))
        {
            if (record.Kind == "maintenance_reserve")
            {
                var hasAccountingFields = new[] { "recordedAmount", "collectedAmount", "currency", "dueDate" }
                    .Any(record.Fields.ContainsKey);
                if (!hasAccountingFields || record.Fields.GetValueOrDefault("period") != period) continue;
            }
            else if (record.Fields.TryGetValue("period", out var reimbursementPeriod) && reimbursementPeriod != period) continue;

            var financeRecord = new SourceRecord<FinancePayload> { SourceId = record.Citation.SourceId,
                RecordId = record.Citation.RecordId, RecordVersion = record.Citation.RecordVersion, AsOf = record.AsOf,
                CaseId = caseId, Kind = record.Kind, Stale = record.Stale, Payload = new FinancePayload(record.Fields), Synthetic = true };
            if (record.Stale)
                return SourceFedBlocked(draftId, version, period, "source_stale", "Required source 'finance' has stale reserve or reimbursement data.", []);
            financeAmountRecords.Add(financeRecord);
            financeCitations.Add(record.Citation.RecordId, record.Citation);
        }

        var financeCalculation = CalculateFinanceRecords(draftId, version, period, financeAmountRecords
            .OrderBy(record => record.Kind == "maintenance_reserve" ? 0 : 1)
            .ThenBy(record => record.RecordId, StringComparer.Ordinal));
        if (financeCalculation.Status == ReserveDraftStatus.Blocked)
        {
            return SourceFedBlocked(draftId, version, period,
                financeCalculation.Blockers.FirstOrDefault()?.Code ?? "finance-record-invalid",
                financeCalculation.Blockers.FirstOrDefault()?.Detail ?? "Finance records are incomplete.", []);
        }

        var lines = reserveCalculation.Lines.Select(line => line with
        {
            UtilizationSource = utilization.Citation,
            RateSource = rateCandidates.Single(candidate => candidate.Basis == line.ContractLine).Record.Citation,
        }).ToArray();
        var financeLines = financeCalculation.Lines
            .Select(line => line with { Source = financeCitations[line.RecordId] }).ToArray();

        return new SourceFedReserveDraft(draftId, version, period, reserveCalculation.Status, utilization.Citation.RecordId,
            string.Join(",", rateCandidates.Select(rate => rate.Record.Citation.RecordId)), lines, reserveCalculation.PayableTotal,
            [], reserveCalculation.Discrepancies, SourceFedCalculationBasis, financeLines, financeCalculation.Total);
    }

    public static bool IsSourceBasisContent(string? content)
    {
        try
        {
            using var document = JsonDocument.Parse(content ?? string.Empty);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("format", out var format) && format.GetString() == "source-record-pins-v1";
        }
        catch (JsonException) { return false; }
    }

    private static SourceFedReserveDraft SourceFedBlocked(string draftId, int version, string period, string code, string detail,
        IReadOnlyList<string> discrepancies) =>
        new(draftId, version, period, ReserveDraftStatus.Blocked, null, null, [], null, [new BlockReason(code, detail)],
            discrepancies, SourceFedCalculationBasis, [], null);

    private static IReadOnlyList<PinnedRecord> ParsePinnedRecords(string basisContent, string caseId)
    {
        var basis = JsonSerializer.Deserialize<SourcePinDocument>(basisContent, PinnedSourceJson);
        if (basis is null || basis.Format != "source-record-pins-v1" || basis.Pins is null || basis.Pins.Count == 0)
            throw new InvalidDataException("Source basis pin document is invalid.");

        return basis.Pins.Where(pin => pin is not null && (pin.SourceId is "technical" or "finance"))
            .Select(pin => pin.SourceId == "technical"
                ? ParsePinnedRecord<TechnicalPayload>(pin, caseId)
                : ParsePinnedRecord<FinancePayload>(pin, caseId)).ToArray();
    }

    private static PinnedRecord ParsePinnedRecord<TPayload>(SourceBasisPin pin, string caseId) where TPayload : class
    {
        if (pin.RecordVersion < 1 || string.IsNullOrWhiteSpace(pin.RecordId) || string.IsNullOrWhiteSpace(pin.Content)
            || !string.Equals(pin.ContentDigest, Digest(pin.Content), StringComparison.Ordinal))
            throw new InvalidDataException("Source basis pin identity or content digest is invalid.");

        var record = JsonSerializer.Deserialize<SourceRecord<TPayload>>(pin.Content, PinnedSourceJson);
        var fields = record?.Payload switch
        {
            TechnicalPayload technical => technical.Fields,
            FinancePayload finance => finance.Fields,
            _ => null,
        };
        if (record is null || record.SourceId != pin.SourceId || record.RecordId != pin.RecordId
            || record.RecordVersion != pin.RecordVersion || record.Validate(caseId).Count > 0 || fields is null)
            throw new InvalidDataException("Source basis pin does not match its source-record envelope.");

        return new PinnedRecord(new SourceRecordCitation(record.SourceId, record.RecordId, record.RecordVersion),
            record.Kind, record.AsOf, record.Stale, fields);
    }

    private static bool RateAppliesToPeriod(PinnedRecord record, string period, DateOnly periodEnd) =>
        record.Fields.TryGetValue("period", out var recordPeriod) ? recordPeriod == period : record.AsOf <= periodEnd;

    private static bool TryDecimal(string? value, out decimal parsed) => decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out parsed) && parsed >= 0m;

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static ReserveDraft Calculate(string draftId, int version, string period, UsageReport? usage, RateReport? rates)
    {
        ValidateNonBlank(draftId, nameof(draftId));
        if (version < 1)
        {
            throw new ArgumentException("Version must be at least 1.", nameof(version));
        }
        ValidatePeriod(period, nameof(period));
        if (usage is not null)
        {
            ValidateUsage(usage);
        }
        if (rates is not null)
        {
            ValidateRates(rates);
        }

        var blockers = new List<BlockReason>();
        if (usage is null)
        {
            blockers.Add(new BlockReason("usage-report-missing", "No utilization report was supplied."));
        }
        else if (usage.Period != period)
        {
            blockers.Add(new BlockReason("usage-report-wrong-period", $"Report {usage.ReportId} covers {usage.Period}, expected {period}."));
        }

        if (rates is null)
        {
            blockers.Add(new BlockReason("rate-report-missing", "No rate report was supplied."));
        }
        else if (rates.Period != period)
        {
            blockers.Add(new BlockReason("rate-report-wrong-period", $"Report {rates.ReportId} covers {rates.Period}, expected {period}."));
        }

        if (usage is not null)
        {
            foreach (var entry in usage.Entries)
            {
                if (entry.Units is null)
                {
                    blockers.Add(new BlockReason("usage-missing", $"Required usage missing for {entry.ContractLine}."));
                    continue;
                }
                if (rates is null)
                {
                    continue;
                }
                var matches = rates.Entries.Count(r => r.ContractLine == entry.ContractLine && r.Period == period);
                if (matches == 0)
                {
                    blockers.Add(new BlockReason("rate-coverage-missing", $"No rate for {entry.ContractLine} in {period}."));
                }
                else if (matches > 1)
                {
                    blockers.Add(new BlockReason("rate-coverage-overlap", $"Overlapping rates for {entry.ContractLine} in {period}."));
                }
            }
        }

        if (usage is not null && rates is not null)
        {
            // Every contract line with a rate for the period is required; an omitted usage line must not read as zero.
            var usageLines = usage.Entries.Select(e => e.ContractLine).ToHashSet(StringComparer.Ordinal);
            foreach (var line in rates.Entries.Where(r => r.Period == period).Select(r => r.ContractLine).Distinct(StringComparer.Ordinal))
            {
                if (!usageLines.Contains(line))
                {
                    blockers.Add(new BlockReason("usage-line-missing", $"No usage reported for required contract line {line} in {period}."));
                }
            }
        }

        if (usage is null || rates is null || blockers.Count > 0)
        {
            return Issue(new ReserveDraft(draftId, version, period, ReserveDraftStatus.Blocked,
                usage?.ReportId, rates?.ReportId, [], null, blockers, [], CalculationBasis));
        }

        var lines = new List<ReserveLine>();
        var discrepancies = new List<string>();
        foreach (var entry in usage.Entries)
        {
            var rate = rates.Entries.Single(r => r.ContractLine == entry.ContractLine && r.Period == period);
            var units = entry.Units!.Value;
            var amount = Cents(units * rate.UnitRate);
            if (entry.ReportedAmount is decimal reported && reported != amount)
            {
                discrepancies.Add($"{entry.ContractLine}: reported {reported:0.00}, calculated {amount:0.00}.");
            }
            lines.Add(new ReserveLine(entry.ContractLine, units, rate.UnitRate, amount, entry.ReportedAmount));
        }

        var total = lines.Sum(l => l.Amount);
        if (usage.ReportedTotal is decimal reportedTotal && reportedTotal != total)
        {
            discrepancies.Add($"Total: reported {reportedTotal:0.00}, calculated {total:0.00}.");
        }

        var status = discrepancies.Count > 0 ? ReserveDraftStatus.Discrepancy : ReserveDraftStatus.ReadyForReview;
        return Issue(new ReserveDraft(draftId, version, period, status, usage.ReportId, rates.ReportId,
            lines, total, [], discrepancies, CalculationBasis));
    }

    /// <summary>
    /// Records a simulated finance decision. The actor is the user of the demo session identified by the
    /// request cookie, resolved through the <see cref="DemoSessionStore"/> in the request services; no caller-supplied
    /// identity is read. Authority is then checked against the server-held roster.
    /// </summary>
    public static FinanceReviewRecord Review(ReserveDraft draft, FinanceDecisionKind decision, HttpContext http,
        string reason, DateTimeOffset decidedAt)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(http);
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unknown finance decision.");
        }
        var sessions = http.RequestServices?.GetService(typeof(DemoSessionStore)) as DemoSessionStore;
        var session = sessions is null ? null : DemoAuthEndpoints.CurrentSession(http, sessions);
        var actorId = session?.User.Id;
        if (string.IsNullOrWhiteSpace(actorId) || !AuthorizedReviewerIds.Contains(actorId))
        {
            throw new UnauthorizedAccessException("Only an authenticated finance reviewer may record a finance decision.");
        }
        ValidateNonBlank(reason, nameof(reason));
        RequireCalculatorDraft(draft);
        if (decision == FinanceDecisionKind.Approve && draft.Status != ReserveDraftStatus.ReadyForReview)
        {
            throw new InvalidOperationException($"Approval refused: draft {draft.DraftId} v{draft.Version} is {draft.Status}.");
        }
        if (decision == FinanceDecisionKind.Approve && !IsConsistentCalculation(draft))
        {
            throw new InvalidOperationException($"Approval refused: draft {draft.DraftId} v{draft.Version} does not match its calculation.");
        }

        return new FinanceReviewRecord(draft.DraftId, draft.Version, decision, actorId, reason.Trim(),
            decidedAt, draft.CalculationBasis, draft.PayableTotal, Simulated: true, InvoiceIssued: false);
    }

    /// <summary>Server-held finance reviewer roster; immutable, so callers cannot add reviewers at runtime.</summary>
    private static readonly FrozenSet<string> AuthorizedReviewerIds = new[] { "fin-1" }.ToFrozenSet(StringComparer.Ordinal);

    private static ReserveDraft Issue(ReserveDraft draft)
    {
        IssuedDrafts.AddOrUpdate(draft, Snapshot(draft));
        return draft;
    }

    /// <summary>
    /// Refuses any draft that was not returned by <see cref="Calculate"/> unchanged, so fabricated or edited drafts
    /// (including edits made through the exposed mutable line list) cannot be recorded.
    /// </summary>
    private static void RequireCalculatorDraft(ReserveDraft draft)
    {
        if (!IssuedDrafts.TryGetValue(draft, out var issuedSnapshot) || !Enum.IsDefined(draft.Status)
            || issuedSnapshot != Snapshot(draft))
        {
            throw new InvalidOperationException("Draft was not produced unchanged by ReserveCalculator.Calculate; provenance cannot be verified.");
        }
    }

    private static string Snapshot(ReserveDraft draft) => JsonSerializer.Serialize(draft);

    private static decimal Cents(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Re-derives every line amount and the payable total from the draft's own units and rates, so a draft whose
    /// fields were edited or built by hand after calculation cannot be approved.
    /// </summary>
    private static bool IsConsistentCalculation(ReserveDraft draft)
    {
        if (draft.Blockers.Count > 0 || draft.Discrepancies.Count > 0 || draft.Lines.Count == 0)
        {
            return false;
        }
        if (draft.CalculationBasis != CalculationBasis || draft.PayableTotal is not decimal payable)
        {
            return false;
        }
        decimal total = 0m;
        foreach (var line in draft.Lines)
        {
            if (line.Units < 0 || line.UnitRate < 0 || line.Amount != Cents(line.Units * line.UnitRate))
            {
                return false;
            }
            if (line.ReportedAmount is decimal reported && reported != line.Amount)
            {
                return false;
            }
            total += line.Amount;
        }
        return payable == total;
    }

    private static void ValidateUsage(UsageReport usage)
    {
        ValidateNonBlank(usage.ReportId, nameof(usage));
        ValidatePeriod(usage.Period, nameof(usage));
        ArgumentNullException.ThrowIfNull(usage.Entries, nameof(usage));
        if (usage.ReportedTotal is < 0)
        {
            throw new ArgumentException("Reported total cannot be negative.", nameof(usage));
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in usage.Entries)
        {
            ArgumentNullException.ThrowIfNull(entry, nameof(usage));
            ValidateNonBlank(entry.ContractLine, nameof(usage));
            if (!seen.Add(entry.ContractLine))
            {
                throw new ArgumentException($"Duplicate usage line {entry.ContractLine}.", nameof(usage));
            }
            if (entry.Units is < 0 || entry.ReportedAmount is < 0)
            {
                throw new ArgumentException($"Negative usage or amount on {entry.ContractLine}.", nameof(usage));
            }
        }
    }

    private static void ValidateRates(RateReport rates)
    {
        ValidateNonBlank(rates.ReportId, nameof(rates));
        ValidatePeriod(rates.Period, nameof(rates));
        ArgumentNullException.ThrowIfNull(rates.Entries, nameof(rates));
        foreach (var entry in rates.Entries)
        {
            ArgumentNullException.ThrowIfNull(entry, nameof(rates));
            ValidateNonBlank(entry.ContractLine, nameof(rates));
            ValidatePeriod(entry.Period, nameof(rates));
            if (entry.UnitRate < 0)
            {
                throw new ArgumentException($"Negative rate on {entry.ContractLine}.", nameof(rates));
            }
        }
    }

    private static void ValidateNonBlank(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A required value is missing.", paramName);
        }
    }

    private static void ValidatePeriod(string? period, string paramName)
    {
        if (period is null || !PeriodPattern.IsMatch(period))
        {
            throw new ArgumentException($"Malformed period '{period}'; expected yyyy-MM.", paramName);
        }
    }
}
