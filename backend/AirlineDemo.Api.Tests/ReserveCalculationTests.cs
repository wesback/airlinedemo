using AirlineDemo.Api.Finance;

namespace AirlineDemo.Api.Tests;

public class ReserveCalculationTests
{
    private const string Period = "2026-09";
    private static readonly FinanceActor Reviewer = new("fin-1");
    private static readonly IReadOnlySet<string> Roster = new HashSet<string>(StringComparer.Ordinal) { "fin-1" };
    private static readonly DateTimeOffset At = new(2026, 10, 13, 1, 0, 0, TimeSpan.Zero);

    private static UsageReport Usage(params UsageEntry[] entries) => new("USE-2026-09", Period, entries);

    private static RateReport Rates(params RateEntry[] entries) => new("RATE-2026-09", Period, entries);

    private static RateReport StandardRates() =>
        Rates(new RateEntry("FH-A", Period, 0.10m), new RateEntry("CYC-B", Period, 12.345m));

    private static ReserveDraft Calc(UsageReport? usage, RateReport? rates, int version = 1) =>
        ReserveCalculator.Calculate("DRAFT-1", version, Period, usage, rates);

    [Fact]
    public void Lines_round_before_totals_with_midpoints_away_from_zero()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 1.25m), new UsageEntry("FH-B", 1.25m)),
            Rates(new RateEntry("FH-A", Period, 0.10m), new RateEntry("FH-B", Period, 0.10m)));

        Assert.Equal(ReserveDraftStatus.ReadyForReview, draft.Status);
        Assert.Equal(new[] { 0.13m, 0.13m }, draft.Lines.Select(l => l.Amount));
        Assert.Equal(0.26m, draft.PayableTotal);
    }

    [Fact]
    public void Hand_calculated_line_and_total_match()
    {
        var draft = Calc(Usage(new UsageEntry("CYC-B", 1m), new UsageEntry("FH-A", 5m)), StandardRates());

        Assert.Equal(ReserveDraftStatus.ReadyForReview, draft.Status);
        Assert.Equal(12.35m, draft.Lines[0].Amount);
        Assert.Equal(0.50m, draft.Lines[1].Amount);
        Assert.Equal(12.85m, draft.PayableTotal);
    }

    [Fact]
    public void Mismatched_line_is_discrepancy_not_approved_and_usage_is_unchanged()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m, 0.10m)), StandardRates());

        Assert.Equal(ReserveDraftStatus.Discrepancy, draft.Status);
        Assert.Equal(2m, draft.Lines[0].Units);
        Assert.Equal(0.10m, draft.Lines[0].ReportedAmount);
        Assert.Equal(0.20m, draft.Lines[0].Amount);
        Assert.Single(draft.Discrepancies);
        Assert.Throws<InvalidOperationException>(() =>
            ReserveCalculator.Review(draft, FinanceDecisionKind.Approve, Reviewer, Roster, "ok", At));
    }

    [Fact]
    public void Mismatched_total_is_discrepancy_not_approved()
    {
        var draft = Calc(new UsageReport("USE-2026-09", Period, [new UsageEntry("FH-A", 2m)], ReportedTotal: 99.99m),
            StandardRates());

        Assert.Equal(ReserveDraftStatus.Discrepancy, draft.Status);
        Assert.Contains(draft.Discrepancies, d => d.StartsWith("Total:", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() =>
            ReserveCalculator.Review(draft, FinanceDecisionKind.Approve, Reviewer, Roster, "ok", At));
    }

    [Fact]
    public void Discrepancy_keeps_source_reports_period_rates_and_basis()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m, 0.10m)), StandardRates());

        Assert.Equal("USE-2026-09", draft.UsageReportId);
        Assert.Equal("RATE-2026-09", draft.RateReportId);
        Assert.Equal(Period, draft.Period);
        Assert.Equal(0.10m, draft.Lines[0].UnitRate);
        Assert.Equal(ReserveCalculator.CalculationBasis, draft.CalculationBasis);
        Assert.Empty(draft.Blockers);
    }

    [Fact]
    public void Missing_usage_report_is_blocked_with_no_charge_lines_or_total()
    {
        var draft = Calc(null, StandardRates());

        Assert.Equal(ReserveDraftStatus.Blocked, draft.Status);
        Assert.Contains(draft.Blockers, b => b.Code == "usage-report-missing");
        Assert.Empty(draft.Lines);
        Assert.Null(draft.PayableTotal);
        Assert.Null(draft.UsageReportId);
        Assert.Throws<InvalidOperationException>(() =>
            ReserveCalculator.Review(draft, FinanceDecisionKind.Approve, Reviewer, Roster, "ok", At));
    }

    [Fact]
    public void Wrong_period_usage_is_blocked()
    {
        var draft = Calc(new UsageReport("USE-2026-08", "2026-08", [new UsageEntry("FH-A", 2m)]), StandardRates());

        Assert.Equal(ReserveDraftStatus.Blocked, draft.Status);
        Assert.Contains(draft.Blockers, b => b.Code == "usage-report-wrong-period");
        Assert.Empty(draft.Lines);
        Assert.Null(draft.PayableTotal);
    }

    [Fact]
    public void Missing_required_usage_is_blocked_not_treated_as_zero()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", null)), StandardRates());

        Assert.Equal(ReserveDraftStatus.Blocked, draft.Status);
        Assert.Contains(draft.Blockers, b => b.Code == "usage-missing");
        Assert.Empty(draft.Lines);
        Assert.Null(draft.PayableTotal);
    }

    [Fact]
    public void Missing_rate_coverage_is_blocked()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m)), Rates(new RateEntry("CYC-B", Period, 12.345m)));

        Assert.Equal(ReserveDraftStatus.Blocked, draft.Status);
        Assert.Contains(draft.Blockers, b => b.Code == "rate-coverage-missing");
        Assert.Empty(draft.Lines);
        Assert.Null(draft.PayableTotal);
    }

    [Fact]
    public void Overlapping_rate_coverage_is_blocked()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m)),
            Rates(new RateEntry("FH-A", Period, 0.10m), new RateEntry("FH-A", Period, 0.12m)));

        Assert.Equal(ReserveDraftStatus.Blocked, draft.Status);
        Assert.Contains(draft.Blockers, b => b.Code == "rate-coverage-overlap");
        Assert.Empty(draft.Lines);
        Assert.Null(draft.PayableTotal);
    }

    [Fact]
    public void Malformed_inputs_fail_validation()
    {
        Assert.Throws<ArgumentException>(() =>
            ReserveCalculator.Calculate("DRAFT-1", 1, "2026-13", Usage(), StandardRates()));
        Assert.Throws<ArgumentException>(() => Calc(Usage(new UsageEntry("FH-A", -1m)), StandardRates()));
        Assert.Throws<ArgumentException>(() =>
            Calc(Usage(new UsageEntry("FH-A", 1m), new UsageEntry("FH-A", 2m)), StandardRates()));
        Assert.Throws<ArgumentException>(() => ReserveCalculator.Calculate(" ", 1, Period, Usage(), StandardRates()));
        Assert.Throws<ArgumentException>(() => ReserveCalculator.Calculate("DRAFT-1", 0, Period, Usage(), StandardRates()));
    }

    [Fact]
    public void Unauthorized_actor_cannot_record_decision()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m)), StandardRates());
        var presenter = new FinanceActor("pres-1");

        Assert.Throws<UnauthorizedAccessException>(() =>
            ReserveCalculator.Review(draft, FinanceDecisionKind.Approve, presenter, Roster, "ok", At));
        Assert.Throws<UnauthorizedAccessException>(() =>
            ReserveCalculator.Review(draft, FinanceDecisionKind.Return, presenter, Roster, "ok", At));
    }

    [Fact]
    public void Actor_not_on_server_roster_is_refused_even_when_claiming_reviewer_identity()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m)), StandardRates());
        var claimant = new FinanceActor("fin-2");

        Assert.Throws<UnauthorizedAccessException>(() =>
            ReserveCalculator.Review(draft, FinanceDecisionKind.Approve, claimant, Roster, "ok", At));
    }

    [Fact]
    public void Undefined_decision_value_is_rejected()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m)), StandardRates());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReserveCalculator.Review(draft, (FinanceDecisionKind)99, Reviewer, Roster, "ok", At));
    }

    [Fact]
    public void Approve_records_simulated_decision_with_provenance_and_no_invoice()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m)), StandardRates());

        var record = ReserveCalculator.Review(draft, FinanceDecisionKind.Approve, Reviewer, Roster, " reviewed ", At);

        Assert.Equal(FinanceDecisionKind.Approve, record.Decision);
        Assert.Equal("fin-1", record.ActorId);
        Assert.Equal("reviewed", record.Reason);
        Assert.Equal(At, record.DecidedAt);
        Assert.Equal(ReserveCalculator.CalculationBasis, record.CalculationBasis);
        Assert.Equal(0.20m, record.PayableTotal);
        Assert.Equal(1, record.DraftVersion);
        Assert.True(record.Simulated);
        Assert.False(record.InvoiceIssued);
    }

    [Fact]
    public void Return_records_decision_for_blocked_draft_without_payable_total()
    {
        var draft = Calc(null, StandardRates());

        var record = ReserveCalculator.Review(draft, FinanceDecisionKind.Return, Reviewer, Roster, "need usage", At);

        Assert.Equal(FinanceDecisionKind.Return, record.Decision);
        Assert.Null(record.PayableTotal);
        Assert.False(record.InvoiceIssued);
    }

    [Fact]
    public void Correction_creates_new_version_without_inherited_approval()
    {
        var first = Calc(Usage(new UsageEntry("FH-A", 2m, 0.10m)), StandardRates());
        var corrected = Calc(Usage(new UsageEntry("FH-A", 2m, 0.20m)), StandardRates(), version: 2);

        Assert.Equal(ReserveDraftStatus.Discrepancy, first.Status);
        Assert.Equal(ReserveDraftStatus.ReadyForReview, corrected.Status);
        Assert.Equal(2, corrected.Version);

        var record = ReserveCalculator.Review(corrected, FinanceDecisionKind.Approve, Reviewer, Roster, "corrected", At);
        Assert.Equal(2, record.DraftVersion);
        Assert.Throws<InvalidOperationException>(() =>
            ReserveCalculator.Review(first, FinanceDecisionKind.Approve, Reviewer, Roster, "stale", At));
    }
}
