using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Finance;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

/// <summary>
/// Criterion-to-test map (issue #146):
/// AC1 rounding: Lines_round_before_totals_with_midpoints_away_from_zero, Hand_calculated_line_and_total_match,
///     Mismatched_line_is_discrepancy_not_approved_and_usage_is_unchanged, Mismatched_total_is_discrepancy_not_approved.
/// AC2 discrepancy provenance: Mismatched_line_is_discrepancy_not_approved_and_usage_is_unchanged,
///     Discrepancy_keeps_source_reports_period_rates_and_basis.
/// AC3 blocked inputs: Missing_usage_report_is_blocked_with_no_charge_lines_or_total, Wrong_period_usage_is_blocked,
///     Missing_required_usage_is_blocked_not_treated_as_zero, Missing_rate_coverage_is_blocked,
///     Overlapping_rate_coverage_is_blocked, Omitted_required_usage_line_is_blocked_not_treated_as_zero,
///     Missing_rate_report_is_blocked_with_no_charge_lines_or_total_and_refuses_approval,
///     Wrong_period_rate_report_is_blocked_with_no_charge_lines_or_total_and_refuses_approval.
/// AC4 validation and authorization: Malformed_inputs_fail_validation, Null_entries_in_reports_fail_validation_instead_of_throwing_NullReference,
///     Unauthorized_actor_cannot_record_decision, Actor_not_on_server_roster_is_refused_even_when_claiming_reviewer_identity,
///     Request_without_session_cannot_name_itself_reviewer, Forged_session_cookie_naming_reviewer_is_refused,
///     Caller_supplied_identity_cannot_elevate_a_non_reviewer_session, Undefined_decision_value_is_rejected,
///     Correction_creates_new_version_without_inherited_approval.
/// AC4 simulated decision, provenance, no invoice: Approve_records_simulated_decision_with_provenance_and_no_invoice,
///     Return_records_decision_for_blocked_draft_without_payable_total.
/// AC5 (suite fails when a guarded behaviour is removed): every test above.
/// </summary>
public class ReserveCalculationTests
{
    private const string Period = "2026-09";
    private static HttpContext Reviewer => Actor("fin-1");

    /// <summary>A request carrying a real demo session cookie issued by DemoSessionStore for the user.</summary>
    private static HttpContext Actor(string userId)
    {
        var store = new DemoSessionStore(TimeProvider.System);
        var session = store.Create(new DemoUser(userId, userId, "finance", []));
        return RequestWith(store, session.Id);
    }

    private static HttpContext RequestWith(DemoSessionStore store, string? sessionId)
    {
        var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        if (sessionId is not null)
        {
            http.Request.Headers.Cookie = $"{DemoAuthEndpoints.CookieName}={sessionId}";
        }
        return http;
    }
    private static readonly DateTimeOffset At = new(2026, 10, 13, 1, 0, 0, TimeSpan.Zero);

    private static UsageReport Usage(params UsageEntry[] entries) => new("USE-2026-09", Period, entries);

    private static RateReport Rates(params RateEntry[] entries) => new("RATE-2026-09", Period, entries);

    private static RateReport StandardRates() =>
        Rates(new RateEntry("FH-A", Period, 0.10m), new RateEntry("CYC-B", Period, 12.345m));

    private static RateReport FhRates() => Rates(new RateEntry("FH-A", Period, 0.10m));

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
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m, 0.10m)), FhRates());

        Assert.Equal(ReserveDraftStatus.Discrepancy, draft.Status);
        Assert.Equal(2m, draft.Lines[0].Units);
        Assert.Equal(0.10m, draft.Lines[0].ReportedAmount);
        Assert.Equal(0.20m, draft.Lines[0].Amount);
        Assert.Single(draft.Discrepancies);
        Assert.Throws<InvalidOperationException>(() =>
            ReserveCalculator.Review(draft, FinanceDecisionKind.Approve, Reviewer, "ok", At));
    }

    [Fact]
    public void Mismatched_total_is_discrepancy_not_approved()
    {
        var draft = Calc(new UsageReport("USE-2026-09", Period, [new UsageEntry("FH-A", 2m)], ReportedTotal: 99.99m),
            FhRates());

        Assert.Equal(ReserveDraftStatus.Discrepancy, draft.Status);
        Assert.Contains(draft.Discrepancies, d => d.StartsWith("Total:", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() =>
            ReserveCalculator.Review(draft, FinanceDecisionKind.Approve, Reviewer, "ok", At));
    }

    [Fact]
    public void Discrepancy_keeps_source_reports_period_rates_and_basis()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m, 0.10m)), FhRates());

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
            ReserveCalculator.Review(draft, FinanceDecisionKind.Approve, Reviewer, "ok", At));
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
    public void Omitted_required_usage_line_is_blocked_not_treated_as_zero()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m)), StandardRates());

        Assert.Equal(ReserveDraftStatus.Blocked, draft.Status);
        Assert.Contains(draft.Blockers, b => b.Code == "usage-line-missing" && b.Detail.Contains("CYC-B"));
        Assert.Empty(draft.Lines);
        Assert.Null(draft.PayableTotal);
        Assert.Throws<InvalidOperationException>(() =>
            ReserveCalculator.Review(draft, FinanceDecisionKind.Approve, Reviewer, "ok", At));
    }

    [Fact]
    public void Missing_rate_report_is_blocked_with_no_charge_lines_or_total_and_refuses_approval()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m)), null);

        Assert.Equal(ReserveDraftStatus.Blocked, draft.Status);
        Assert.Contains(draft.Blockers, b => b.Code == "rate-report-missing");
        Assert.Null(draft.RateReportId);
        Assert.Empty(draft.Lines);
        Assert.Null(draft.PayableTotal);
        Assert.Throws<InvalidOperationException>(() =>
            ReserveCalculator.Review(draft, FinanceDecisionKind.Approve, Reviewer, "ok", At));
    }

    [Fact]
    public void Wrong_period_rate_report_is_blocked_with_no_charge_lines_or_total_and_refuses_approval()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m)),
            new RateReport("RATE-2026-08", "2026-08", [new RateEntry("FH-A", "2026-08", 0.10m)]));

        Assert.Equal(ReserveDraftStatus.Blocked, draft.Status);
        Assert.Contains(draft.Blockers, b => b.Code == "rate-report-wrong-period");
        Assert.Equal("RATE-2026-08", draft.RateReportId);
        Assert.Empty(draft.Lines);
        Assert.Null(draft.PayableTotal);
        Assert.Throws<InvalidOperationException>(() =>
            ReserveCalculator.Review(draft, FinanceDecisionKind.Approve, Reviewer, "ok", At));
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
    public void Null_entries_in_reports_fail_validation_instead_of_throwing_NullReference()
    {
        Assert.Throws<ArgumentNullException>(() => Calc(Usage((UsageEntry)null!), StandardRates()));
        Assert.Throws<ArgumentNullException>(() => Calc(Usage(), Rates((RateEntry)null!)));
    }

    [Fact]
    public void Unauthorized_actor_cannot_record_decision()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m)), StandardRates());
        var presenter = Actor("pres-1");

        Assert.Throws<UnauthorizedAccessException>(() =>
            ReserveCalculator.Review(draft, FinanceDecisionKind.Approve, presenter, "ok", At));
        Assert.Throws<UnauthorizedAccessException>(() =>
            ReserveCalculator.Review(draft, FinanceDecisionKind.Return, presenter, "ok", At));
    }

    [Fact]
    public void Actor_not_on_server_roster_is_refused_even_when_claiming_reviewer_identity()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m)), StandardRates());
        var claimant = Actor("fin-2");

        Assert.Throws<UnauthorizedAccessException>(() =>
            ReserveCalculator.Review(draft, FinanceDecisionKind.Approve, claimant, "ok", At));
    }

    [Fact]
    public void Request_without_session_cannot_name_itself_reviewer()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m)), StandardRates());
        var http = RequestWith(new DemoSessionStore(TimeProvider.System), null);
        http.Request.Headers["X-User-Id"] = "fin-1";

        Assert.Throws<UnauthorizedAccessException>(() =>
            ReserveCalculator.Review(draft, FinanceDecisionKind.Approve, http, "ok", At));
    }

    [Fact]
    public void Forged_session_cookie_naming_reviewer_is_refused()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m)), StandardRates());
        var http = RequestWith(new DemoSessionStore(TimeProvider.System), "FORGED-fin-1");

        Assert.Throws<UnauthorizedAccessException>(() =>
            ReserveCalculator.Review(draft, FinanceDecisionKind.Approve, http, "ok", At));
    }

    [Fact]
    public void Caller_supplied_identity_cannot_elevate_a_non_reviewer_session()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m)), StandardRates());
        var presenter = Actor("pres-1");
        presenter.Request.Headers["X-User-Id"] = "fin-1";

        Assert.Throws<UnauthorizedAccessException>(() =>
            ReserveCalculator.Review(draft, FinanceDecisionKind.Approve, presenter, "ok", At));
    }

    [Fact]
    public void Undefined_decision_value_is_rejected()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m)), StandardRates());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReserveCalculator.Review(draft, (FinanceDecisionKind)99, Reviewer, "ok", At));
    }

    [Fact]
    public void Approve_records_simulated_decision_with_provenance_and_no_invoice()
    {
        var draft = Calc(Usage(new UsageEntry("FH-A", 2m)), FhRates());

        var record = ReserveCalculator.Review(draft, FinanceDecisionKind.Approve, Reviewer, " reviewed ", At);

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

        var record = ReserveCalculator.Review(draft, FinanceDecisionKind.Return, Reviewer, "need usage", At);

        Assert.Equal(FinanceDecisionKind.Return, record.Decision);
        Assert.True(record.Simulated);
        Assert.Equal("fin-1", record.ActorId);
        Assert.Equal("need usage", record.Reason);
        Assert.Equal(At, record.DecidedAt);
        Assert.Equal(ReserveCalculator.CalculationBasis, record.CalculationBasis);
        Assert.Null(record.PayableTotal);
        Assert.False(record.InvoiceIssued);
    }

    [Fact]
    public void Correction_creates_new_version_without_inherited_approval()
    {
        var first = Calc(Usage(new UsageEntry("FH-A", 2m, 0.20m)), FhRates());
        var firstApproval = ReserveCalculator.Review(first, FinanceDecisionKind.Approve, Reviewer, "v1 ok", At);
        Assert.Equal(1, firstApproval.DraftVersion);
        Assert.Equal(0.20m, firstApproval.PayableTotal);

        var corrected = Calc(Usage(new UsageEntry("FH-A", 3m, 0.30m)), FhRates(), version: 2);
        Assert.Equal(ReserveDraftStatus.ReadyForReview, corrected.Status);
        Assert.Equal(2, corrected.Version);
        Assert.Equal(0.30m, corrected.PayableTotal);

        var returned = ReserveCalculator.Review(corrected, FinanceDecisionKind.Return, Reviewer, "recheck", At);
        Assert.Equal(2, returned.DraftVersion);
        Assert.Equal(FinanceDecisionKind.Return, returned.Decision);
        Assert.False(returned.InvoiceIssued);

        var approval = ReserveCalculator.Review(corrected, FinanceDecisionKind.Approve, Reviewer, "corrected", At);
        Assert.Equal(2, approval.DraftVersion);
        Assert.Equal(0.30m, approval.PayableTotal);
        Assert.Equal(1, firstApproval.DraftVersion);
        Assert.Equal(0.20m, firstApproval.PayableTotal);

        var blockedV3 = Calc(Usage(new UsageEntry("FH-A", 3m, 0.30m)), null, version: 3);
        Assert.Equal(ReserveDraftStatus.Blocked, blockedV3.Status);
        Assert.Throws<InvalidOperationException>(() =>
            ReserveCalculator.Review(blockedV3, FinanceDecisionKind.Approve, Reviewer, "no inherited approval", At));
    }

    [Fact]
    public void Approve_refuses_hand_built_or_tampered_draft()
    {
        var line = new ReserveLine("FH-A", 2m, 0.20m, 0.40m, null);
        var fabricated = new ReserveDraft("draft-x", 1, "2026-03", ReserveDraftStatus.ReadyForReview,
            "U-1", "R-1", [line], 9.99m, [], [], ReserveCalculator.CalculationBasis);
        Assert.Throws<InvalidOperationException>(() =>
            ReserveCalculator.Review(fabricated, FinanceDecisionKind.Approve, Reviewer, "forged total", At));

        var wrongLine = new ReserveLine("FH-A", 2m, 0.20m, 9.99m, null);
        var tamperedLine = new ReserveDraft("draft-y", 1, "2026-03", ReserveDraftStatus.ReadyForReview,
            "U-1", "R-1", [wrongLine], 9.99m, [], [], ReserveCalculator.CalculationBasis);
        Assert.Throws<InvalidOperationException>(() =>
            ReserveCalculator.Review(tamperedLine, FinanceDecisionKind.Approve, Reviewer, "forged line", At));

        var consistent = new ReserveDraft("draft-z", 1, "2026-03", ReserveDraftStatus.ReadyForReview,
            "U-1", "R-1", [line], 0.40m, [], [], ReserveCalculator.CalculationBasis);
        Assert.Throws<InvalidOperationException>(() =>
            ReserveCalculator.Review(consistent, FinanceDecisionKind.Approve, Reviewer, "hand-built but self-consistent", At));

        var copied = Calc(Usage(new UsageEntry("FH-A", 2m)), FhRates()) with { Status = ReserveDraftStatus.ReadyForReview };
        Assert.Throws<InvalidOperationException>(() =>
            ReserveCalculator.Review(copied, FinanceDecisionKind.Approve, Reviewer, "copied draft", At));
    }

    [Fact]
    public void Return_refuses_hand_built_or_copied_draft()
    {
        var line = new ReserveLine("FH-A", 2m, 0.20m, 0.40m, null);
        var handBuilt = new ReserveDraft("draft-r", 1, "2026-03", ReserveDraftStatus.ReadyForReview,
            "U-1", "R-1", [line], 0.40m, [], [], ReserveCalculator.CalculationBasis);
        Assert.Throws<InvalidOperationException>(() =>
            ReserveCalculator.Review(handBuilt, FinanceDecisionKind.Return, Reviewer, "malformed", At));

        var calculated = Calc(Usage(new UsageEntry("FH-A", 2m)), FhRates());
        var copied = calculated with { PayableTotal = 9.99m };
        Assert.Throws<InvalidOperationException>(() =>
            ReserveCalculator.Review(copied, FinanceDecisionKind.Return, Reviewer, "tampered", At));

        var record = ReserveCalculator.Review(calculated, FinanceDecisionKind.Return, Reviewer, "needs rework", At);
        Assert.Equal(FinanceDecisionKind.Return, record.Decision);
        Assert.False(record.InvoiceIssued);
    }
}
