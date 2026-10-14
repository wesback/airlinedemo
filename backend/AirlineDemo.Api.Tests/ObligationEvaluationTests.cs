using AirlineDemo.Api.Obligations;

namespace AirlineDemo.Api.Tests;

public sealed class ObligationEvaluationTests
{
    private static readonly DateOnly WindowStart = new(2026, 10, 10);
    private static readonly DateOnly WindowEnd = new(2026, 10, 14);
    private static readonly DateTimeOffset Due = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    private static Obligation Make(
        IReadOnlyList<LinkedRequirement> reqs,
        TechnicalReview? review = null,
        int basis = 2) =>
        new("OBL-1", basis, WindowStart, WindowEnd, Due, reqs, review);

    private static LinkedRequirement Missing(string id = "REQ-1") => new(id, RequirementEvidence.Missing, null);
    private static LinkedRequirement Satisfied(string id = "REQ-1", int basis = 2) => new(id, RequirementEvidence.Satisfied, basis);
    private static LinkedRequirement Ambiguous(string id = "REQ-1") => new(id, RequirementEvidence.Ambiguous, 2);

    private static DateTimeOffset At(int day, int hour = 12) => new(2026, 10, day, hour, 0, 0, TimeSpan.Zero);

    private static readonly TechnicalReview Authorized = new("reviewer-1", true, 2);

    [Fact]
    public void Ambiguous_evidence_blocks_even_when_requirements_are_missing()
    {
        var result = ObligationEvaluator.Evaluate(Make([Ambiguous("REQ-1"), Missing("REQ-2")]), At(12), []);
        Assert.Equal(ObligationState.Blocked, result.State);
        Assert.Contains(result.Reasons, r => r.Contains("REQ-1") && r.Contains("ambiguous"));
        Assert.Contains(result.Reasons, r => r.Contains("REQ-2") && r.Contains("missing"));
    }

    [Fact]
    public void Stale_basis_evidence_needs_review_over_at_risk()
    {
        var result = ObligationEvaluator.Evaluate(Make([Satisfied("REQ-1", basis: 1), Missing("REQ-2")]), At(12), []);
        Assert.Equal(ObligationState.NeedsReview, result.State);
    }

    [Fact]
    public void Missing_evidence_in_window_is_at_risk_with_one_draft()
    {
        var result = ObligationEvaluator.Evaluate(Make([Missing()]), At(12), []);
        Assert.Equal(ObligationState.AtRisk, result.State);
        Assert.NotNull(result.NewDraft);
        Assert.Equal(NoticeDraftStatus.Current, result.NewDraft!.Status);
        Assert.Equal(2, result.NewDraft.BasisVersion);
        Assert.Contains("REQ-1", result.NewDraft.EvidenceRequirementIds);
        Assert.Single(result.Drafts);
    }

    [Fact]
    public void Current_evidence_without_authorized_review_is_evidenced_not_accepted()
    {
        var result = ObligationEvaluator.Evaluate(Make([Satisfied()]), At(12), []);
        Assert.Equal(ObligationState.Evidenced, result.State);
        Assert.Null(result.NewDraft);
    }

    [Fact]
    public void Authorized_review_on_current_basis_accepts()
    {
        var result = ObligationEvaluator.Evaluate(Make([Satisfied()], Authorized), At(12), []);
        Assert.Equal(ObligationState.Accepted, result.State);
        Assert.Null(result.NewDraft);
    }

    [Fact]
    public void Unauthorized_review_does_not_accept()
    {
        var review = new TechnicalReview("reviewer-x", false, 2);
        var result = ObligationEvaluator.Evaluate(Make([Satisfied()], review), At(12), []);
        Assert.Equal(ObligationState.Evidenced, result.State);
    }

    [Fact]
    public void Review_on_stale_basis_does_not_accept()
    {
        var review = new TechnicalReview("reviewer-1", true, 1);
        var result = ObligationEvaluator.Evaluate(Make([Satisfied()], review), At(12), []);
        Assert.Equal(ObligationState.Evidenced, result.State);
    }

    [Fact]
    public void Ambiguous_evidence_with_authorized_review_still_blocks()
    {
        var result = ObligationEvaluator.Evaluate(Make([Ambiguous()], Authorized), At(12), []);
        Assert.Equal(ObligationState.Blocked, result.State);
    }

    [Fact]
    public void Pre_window_at_risk_creates_no_draft()
    {
        var result = ObligationEvaluator.Evaluate(Make([Missing()]), At(9, 23), []);
        Assert.Equal(ObligationState.AtRisk, result.State);
        Assert.Null(result.NewDraft);
        Assert.Empty(result.Drafts);
    }

    [Fact]
    public void Overdue_unresolved_routes_to_review_and_creates_no_draft()
    {
        var result = ObligationEvaluator.Evaluate(Make([Missing()]), At(20, 1), []);
        Assert.Equal(ObligationState.NeedsReview, result.State);
        Assert.Null(result.NewDraft);
        Assert.Contains(result.Reasons, r => r.Contains("passed"));
    }

    [Fact]
    public void Ambiguous_in_window_creates_no_draft()
    {
        var result = ObligationEvaluator.Evaluate(Make([Ambiguous(), Missing("REQ-2")]), At(12), []);
        Assert.Equal(ObligationState.Blocked, result.State);
        Assert.Null(result.NewDraft);
    }

    [Fact]
    public void First_day_of_window_at_midnight_UTC_creates_draft()
    {
        var result = ObligationEvaluator.Evaluate(Make([Missing()]), new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero), []);
        Assert.NotNull(result.NewDraft);
    }

    [Fact]
    public void Last_day_of_window_until_end_of_day_UTC_creates_draft()
    {
        var result = ObligationEvaluator.Evaluate(Make([Missing()]), new DateTimeOffset(2026, 10, 14, 23, 59, 59, TimeSpan.Zero), []);
        Assert.NotNull(result.NewDraft);
    }

    [Fact]
    public void Day_after_window_creates_no_draft()
    {
        var result = ObligationEvaluator.Evaluate(Make([Missing()]), new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero), []);
        Assert.Null(result.NewDraft);
    }

    [Fact]
    public void Window_is_evaluated_in_UTC_not_local_offset()
    {
        // 2026-10-09 23:30 at UTC-2 is 2026-10-10 01:30 UTC, so it is inside the window.
        var localOffset = new DateTimeOffset(2026, 10, 9, 23, 30, 0, TimeSpan.FromHours(-2));
        Assert.NotNull(ObligationEvaluator.Evaluate(Make([Missing()]), localOffset, []).NewDraft);

        // 2026-10-14 23:30 at UTC+2 is 2026-10-14 21:30 UTC, still inside; 2026-10-15 01:00 at UTC+2 is 2026-10-14 23:00 UTC.
        var justInside = new DateTimeOffset(2026, 10, 15, 1, 0, 0, TimeSpan.FromHours(2));
        Assert.NotNull(ObligationEvaluator.Evaluate(Make([Missing()]), justInside, []).NewDraft);

        // 2026-10-15 00:30 at UTC+2 is 2026-10-14 22:30 UTC, but 2026-10-14 23:30 at UTC-2 is 2026-10-15 01:30 UTC (outside).
        var outside = new DateTimeOffset(2026, 10, 14, 23, 30, 0, TimeSpan.FromHours(-2));
        Assert.Null(ObligationEvaluator.Evaluate(Make([Missing()]), outside, []).NewDraft);
    }

    [Fact]
    public void Existing_current_draft_is_not_duplicated()
    {
        var first = ObligationEvaluator.Evaluate(Make([Missing()]), At(12), []);
        var second = ObligationEvaluator.Evaluate(Make([Missing()]), At(13), first.Drafts);
        Assert.Null(second.NewDraft);
        Assert.Single(second.Drafts.Where(d => d.Status == NoticeDraftStatus.Current));
    }

    [Fact]
    public void New_evidence_supersedes_obsolete_draft_and_preserves_history()
    {
        var first = ObligationEvaluator.Evaluate(Make([Missing()], basis: 2), At(12), []);
        var original = first.Drafts.Single();

        // Basis moves to v3 and the requirement is now satisfied on v3: the old draft is obsolete.
        var updatedObligation = new Obligation("OBL-1", 3, WindowStart, WindowEnd, Due, [Satisfied("REQ-1", 3)], null);
        var second = ObligationEvaluator.Evaluate(updatedObligation, At(13), first.Drafts);

        var kept = Assert.Single(second.Drafts);
        Assert.Equal(original.Id, kept.Id);
        Assert.Equal(NoticeDraftStatus.Superseded, kept.Status);
        Assert.Equal(original.Body, kept.Body);
        Assert.Null(second.NewDraft);
    }

    [Fact]
    public void Draft_on_stale_basis_is_superseded_and_replaced_when_still_at_risk()
    {
        var first = ObligationEvaluator.Evaluate(Make([Missing()], basis: 2), At(12), []);
        var moved = new Obligation("OBL-1", 3, WindowStart, WindowEnd, Due, [Missing()], null);
        var second = ObligationEvaluator.Evaluate(moved, At(13), first.Drafts);

        Assert.Equal(NoticeDraftStatus.Superseded, second.Drafts.Single(d => d.BasisVersion == 2).Status);
        Assert.NotNull(second.NewDraft);
        Assert.Equal(3, second.NewDraft!.BasisVersion);
        Assert.Equal(2, second.Drafts.Count);
        Assert.Single(second.Drafts.Where(d => d.Status == NoticeDraftStatus.Current));
    }

    [Fact]
    public void Draft_is_superseded_when_obligation_leaves_at_risk()
    {
        var first = ObligationEvaluator.Evaluate(Make([Missing()]), At(12), []);
        var resolved = ObligationEvaluator.Evaluate(Make([Satisfied()]), At(13), first.Drafts);

        Assert.Equal(ObligationState.Evidenced, resolved.State);
        Assert.Equal(NoticeDraftStatus.Superseded, resolved.Drafts.Single().Status);
        Assert.Null(resolved.NewDraft);
    }

    [Fact]
    public void Legal_sign_off_records_simulated_decision_without_changing_state()
    {
        var first = ObligationEvaluator.Evaluate(Make([Missing()]), At(12), []);
        var draft = first.NewDraft!;

        var record = ObligationEvaluator.RecordLegalDecision(draft, "legal-1", "signed-off", "Synthetic check passed", At(13));

        Assert.True(record.Simulated);
        Assert.Equal("legal-1", record.Actor);
        Assert.Equal("signed-off", record.Decision);
        Assert.Equal("Synthetic check passed", record.Reason);
        Assert.Equal(At(13), record.DecidedAtUtc);
        Assert.Equal(draft.BasisVersion, record.BasisVersion);
        Assert.Equal(ObligationState.AtRisk, ObligationEvaluator.Evaluate(Make([Missing()]), At(13), first.Drafts).State);
    }

    private sealed class RecordingNoticeSender : INoticeSender
    {
        public List<NoticeDraft> Sent { get; } = [];

        public void Send(NoticeDraft draft) => Sent.Add(draft);
    }

    [Fact]
    public void Legal_decision_sends_nothing_and_leaves_draft_history_unchanged()
    {
        var first = ObligationEvaluator.Evaluate(Make([Missing()]), At(12), []);
        var draft = first.NewDraft!;
        var sender = new RecordingNoticeSender();

        var record = ObligationEvaluator.RecordLegalDecision(draft, "legal-1", "signed-off", "Synthetic check passed", At(13), sender);
        var afterDecision = ObligationEvaluator.Evaluate(Make([Missing()]), At(13), first.Drafts);

        Assert.True(record.Simulated);
        Assert.Empty(sender.Sent);
        Assert.Equal(draft, first.NewDraft);
        Assert.Equal(first.Drafts, afterDecision.Drafts);
        Assert.Single(afterDecision.Drafts);
        Assert.Equal(NoticeDraftStatus.Current, afterDecision.Drafts.Single().Status);
        Assert.Null(afterDecision.NewDraft);
    }

    [Fact]
    public void Partially_resolved_at_risk_obligation_supersedes_obsolete_draft_and_replaces_it()
    {
        var first = ObligationEvaluator.Evaluate(Make([Missing("REQ-1"), Missing("REQ-2")]), At(12), []);
        var original = first.NewDraft!;
        Assert.Equal(["REQ-1", "REQ-2"], original.EvidenceRequirementIds);

        // REQ-1 is now evidenced on the current basis; REQ-2 is still missing, so the obligation stays at risk.
        var partial = ObligationEvaluator.Evaluate(Make([Satisfied("REQ-1"), Missing("REQ-2")]), At(13), first.Drafts);

        Assert.Equal(ObligationState.AtRisk, partial.State);
        Assert.Equal(2, partial.Drafts.Count);
        Assert.Equal(NoticeDraftStatus.Superseded, partial.Drafts.Single(d => d.Id == original.Id).Status);
        Assert.Equal(original.Body, partial.Drafts.Single(d => d.Id == original.Id).Body);
        Assert.NotNull(partial.NewDraft);
        Assert.Equal(["REQ-2"], partial.NewDraft!.EvidenceRequirementIds);
        Assert.Single(partial.Drafts.Where(d => d.Status == NoticeDraftStatus.Current));
    }

    [Fact]
    public void Legal_rejection_is_recorded_and_requires_actor_and_reason()
    {
        var draft = ObligationEvaluator.Evaluate(Make([Missing()]), At(12), []).NewDraft!;

        var record = ObligationEvaluator.RecordLegalDecision(draft, "legal-1", "rejected", "Wording unclear", At(13));
        Assert.Equal("rejected", record.Decision);

        Assert.Throws<ArgumentException>(() => ObligationEvaluator.RecordLegalDecision(draft, " ", "rejected", "x", At(13)));
        Assert.Throws<ArgumentException>(() => ObligationEvaluator.RecordLegalDecision(draft, "legal-1", "rejected", " ", At(13)));
        Assert.Throws<ArgumentException>(() => ObligationEvaluator.RecordLegalDecision(draft, "legal-1", "sent", "x", At(13)));
    }

    [Fact]
    public void Legal_decision_on_superseded_draft_is_refused()
    {
        var first = ObligationEvaluator.Evaluate(Make([Missing()]), At(12), []);
        var resolved = ObligationEvaluator.Evaluate(Make([Satisfied()]), At(13), first.Drafts);
        Assert.Throws<InvalidOperationException>(() =>
            ObligationEvaluator.RecordLegalDecision(resolved.Drafts.Single(), "legal-1", "signed-off", "x", At(14)));
    }
}
