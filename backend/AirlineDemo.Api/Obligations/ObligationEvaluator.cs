namespace AirlineDemo.Api.Obligations;

public enum ObligationState
{
    Blocked,
    NeedsReview,
    AtRisk,
    Evidenced,
    Accepted,
}

public enum RequirementEvidence
{
    Missing,
    Satisfied,
    Ambiguous,
}

public enum NoticeDraftStatus
{
    Current,
    Superseded,
}

public sealed record LinkedRequirement(string Id, RequirementEvidence Evidence, int? BasisVersion);

public sealed record TechnicalReview(string ReviewerId, bool Authorized, int BasisVersion);

public sealed record Obligation(
    string Id,
    int CurrentBasisVersion,
    DateOnly NoticeWindowStartUtc,
    DateOnly NoticeWindowEndUtc,
    DateTimeOffset DueUtc,
    IReadOnlyList<LinkedRequirement> Requirements,
    TechnicalReview? Review);

public sealed record NoticeDraft(
    string Id,
    string ObligationId,
    int BasisVersion,
    IReadOnlyList<string> EvidenceRequirementIds,
    DateTimeOffset CreatedUtc,
    NoticeDraftStatus Status,
    string Body);

public sealed record ObligationEvaluation(
    ObligationState State,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<NoticeDraft> Drafts,
    NoticeDraft? NewDraft);

public sealed record LegalReviewRecord(
    string DraftId,
    string Actor,
    string Decision,
    string Reason,
    DateTimeOffset DecidedAtUtc,
    int BasisVersion,
    bool Simulated);

// Delivery seam. Recording a legal decision must never call it; tests use it to prove that nothing is sent.
public interface INoticeSender
{
    void Send(NoticeDraft draft);
}

public static class ObligationEvaluator
{
    private static readonly ObligationState[] Precedence =
    [
        ObligationState.Blocked,
        ObligationState.NeedsReview,
        ObligationState.AtRisk,
        ObligationState.Evidenced,
        ObligationState.Accepted,
    ];

    // `drafts` is the full history for the obligation; superseded drafts are kept, never removed.
    public static ObligationEvaluation Evaluate(Obligation obligation, DateTimeOffset asOfUtc, IReadOnlyList<NoticeDraft> drafts)
    {
        var asOf = asOfUtc.ToUniversalTime();
        var reqs = obligation.Requirements;
        var reasons = new List<string>();

        var ambiguous = reqs.Where(r => r.Evidence == RequirementEvidence.Ambiguous).ToList();
        var stale = reqs.Where(r => r.Evidence == RequirementEvidence.Satisfied && r.BasisVersion != obligation.CurrentBasisVersion).ToList();
        var missing = reqs.Where(r => r.Evidence == RequirementEvidence.Missing).ToList();
        var overdue = missing.Count > 0 && asOf > obligation.DueUtc;
        var currentSatisfied = reqs.Count > 0 && reqs.All(r =>
            r.Evidence == RequirementEvidence.Satisfied && r.BasisVersion == obligation.CurrentBasisVersion);
        var reviewAuthorized = obligation.Review is { Authorized: true } review
            && review.BasisVersion == obligation.CurrentBasisVersion;

        foreach (var r in ambiguous) reasons.Add($"{r.Id}: ambiguous evidence");
        foreach (var r in stale) reasons.Add($"{r.Id}: evidence is not on current basis v{obligation.CurrentBasisVersion}");
        foreach (var r in missing) reasons.Add($"{r.Id}: evidence missing");
        if (overdue) reasons.Add($"due {obligation.DueUtc:O} passed with requirements unresolved");
        if (reqs.Count == 0) reasons.Add("no linked requirements");
        if (currentSatisfied && !reviewAuthorized) reasons.Add("awaiting authorized technical review on current basis");

        var state = Precedence.First(candidate => candidate switch
        {
            ObligationState.Blocked => ambiguous.Count > 0 || reqs.Count == 0,
            ObligationState.NeedsReview => stale.Count > 0 || overdue,
            ObligationState.AtRisk => missing.Count > 0,
            ObligationState.Evidenced => currentSatisfied && !reviewAuthorized,
            ObligationState.Accepted => currentSatisfied && reviewAuthorized,
            _ => false,
        });

        // Drafts become non-current when the basis moves, the state leaves at-risk, or the missing requirements change; history is preserved.
        var missingIds = missing.Select(r => r.Id).ToList();
        // Only the most recent current draft can survive, so duplicate current drafts in the history collapse to one.
        var lastCurrentIndex = -1;
        for (var i = 0; i < drafts.Count; i++)
            if (drafts[i].Status == NoticeDraftStatus.Current) lastCurrentIndex = i;
        var updated = drafts.Select((d, i) => d.Status == NoticeDraftStatus.Current
            && (i != lastCurrentIndex
                || d.BasisVersion != obligation.CurrentBasisVersion
                || state != ObligationState.AtRisk
                || !d.EvidenceRequirementIds.SequenceEqual(missingIds))
                ? d with { Status = NoticeDraftStatus.Superseded }
                : d).ToList();

        NoticeDraft? newDraft = null;
        var hasCurrent = updated.Any(d => d.Status == NoticeDraftStatus.Current);
        var inWindow = DateOnly.FromDateTime(asOf.UtcDateTime) is var day
            && day >= obligation.NoticeWindowStartUtc && day <= obligation.NoticeWindowEndUtc;
        if (state == ObligationState.AtRisk && inWindow && !overdue && !hasCurrent)
        {
            newDraft = new NoticeDraft(
                Id: $"{obligation.Id}-draft-{updated.Count + 1}",
                ObligationId: obligation.Id,
                BasisVersion: obligation.CurrentBasisVersion,
                EvidenceRequirementIds: missingIds,
                CreatedUtc: asOf,
                Status: NoticeDraftStatus.Current,
                Body: $"Synthetic notice draft for {obligation.Id}: missing {string.Join(", ", missingIds)} on basis v{obligation.CurrentBasisVersion}.");
            updated.Add(newDraft);
        }

        return new ObligationEvaluation(state, reasons, updated, newDraft);
    }

    // Records a simulated legal decision only. The sender is never invoked, and the obligation state is not changed.
    public static LegalReviewRecord RecordLegalDecision(
        NoticeDraft draft, string actor, string decision, string reason, DateTimeOffset decidedAtUtc, INoticeSender? sender = null)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Actor is required.", nameof(actor));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Reason is required.", nameof(reason));
        if (decision is not ("signed-off" or "rejected")) throw new ArgumentException("Decision must be signed-off or rejected.", nameof(decision));
        if (draft.Status != NoticeDraftStatus.Current) throw new InvalidOperationException("Only a current draft can receive a legal decision.");

        return new LegalReviewRecord(draft.Id, actor, decision, reason, decidedAtUtc.ToUniversalTime(), draft.BasisVersion, Simulated: true);
    }
}
