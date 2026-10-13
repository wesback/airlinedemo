using System.Collections.Frozen;
using System.Text.RegularExpressions;
using AirlineDemo.Api.Auth;
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

public sealed record ReserveLine(string ContractLine, decimal Units, decimal UnitRate, decimal Amount, decimal? ReportedAmount);

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

    private static readonly Regex PeriodPattern = new(@"^\d{4}-(0[1-9]|1[0-2])$", RegexOptions.CultureInvariant);

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
            return new ReserveDraft(draftId, version, period, ReserveDraftStatus.Blocked,
                usage?.ReportId, rates?.ReportId, [], null, blockers, [], CalculationBasis);
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
        return new ReserveDraft(draftId, version, period, status, usage.ReportId, rates.ReportId,
            lines, total, [], discrepancies, CalculationBasis);
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
