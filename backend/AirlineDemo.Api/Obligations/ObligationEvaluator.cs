using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.SourceSystems;

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
    string Body)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObligationSourceCitation? Citation { get; init; }
}

public sealed record ObligationEvaluation(
    ObligationState State,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<NoticeDraft> Drafts,
    NoticeDraft? NewDraft)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObligationSourceCitation? Citation { get; init; }
}

public sealed record ObligationSourceCitation(string SourceId, string RecordId, int RecordVersion);

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
    private const string SourceBasisFormat = "source-record-pins-v1";
    private static readonly JsonSerializerOptions PinnedSourceJson = new(JsonSerializerDefaults.Web);

    private sealed record SourcePinDocument(string Format, IReadOnlyList<SourceBasisPin> Pins);
    private sealed class PinnedContractsClauseMissingException() : Exception { }
    private sealed class PinnedContractsConflictException() : Exception { }

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
        var inWindow = DateOnly.FromDateTime(asOf.UtcDateTime) is var day
            && day >= obligation.NoticeWindowStartUtc && day <= obligation.NoticeWindowEndUtc;
        // Only the most recent current draft can survive, so duplicate current drafts in the history collapse to one.
        var lastCurrentIndex = -1;
        for (var i = 0; i < drafts.Count; i++)
            if (drafts[i].Status == NoticeDraftStatus.Current) lastCurrentIndex = i;
        var updated = drafts.Select((d, i) => d.Status == NoticeDraftStatus.Current
            && (i != lastCurrentIndex
                || d.BasisVersion != obligation.CurrentBasisVersion
                || state != ObligationState.AtRisk
                || !inWindow
                || !d.EvidenceRequirementIds.SequenceEqual(missingIds))
                ? d with { Status = NoticeDraftStatus.Superseded }
                : d).ToList();

        NoticeDraft? newDraft = null;
        var hasCurrent = updated.Any(d => d.Status == NoticeDraftStatus.Current);
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

    /// <summary>Evaluates an obligation against its committed contracts clause, never a live source read.</summary>
    public static ObligationEvaluation EvaluateFromPinnedContracts(
        Obligation obligation,
        string caseId,
        int currentBasisVersion,
        string? basisContent,
        IReadOnlyDictionary<string, string> sourceModes,
        IReadOnlyList<NoticeDraft> drafts,
        INoticeSender? sender = null)
    {
        ArgumentNullException.ThrowIfNull(obligation);
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        ArgumentNullException.ThrowIfNull(sourceModes);
        ArgumentNullException.ThrowIfNull(drafts);

        var mode = sourceModes.GetValueOrDefault("contracts", "available");
        if (mode != "available")
        {
            var reason = mode switch
            {
                "unavailable" => "source_unavailable",
                "stale" => "source_stale",
                "conflicting" => "source_conflict",
                _ => "source_conflict",
            };
            return Blocked(reason, drafts);
        }

        if (currentBasisVersion < 1 || string.IsNullOrWhiteSpace(basisContent))
            return Blocked("source_basis_missing", drafts);

        SourceRecord<ContractsPayload> clause;
        try
        {
            clause = ReadPinnedClause(basisContent, caseId);
        }
        catch (PinnedContractsClauseMissingException)
        {
            return Blocked("contracts_record_missing", drafts);
        }
        catch (PinnedContractsConflictException)
        {
            return Blocked("source_conflict", drafts);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return Blocked("source_basis_invalid", drafts);
        }

        if (clause.Stale)
            return Blocked("source_stale", drafts);

        var fields = clause.Payload.Fields;
        if (fields is null)
            return Blocked("contracts_clause_invalid", drafts);
        if (!HasText(fields, "clauseId") || !HasText(fields, "obligation") || !HasText(fields, "noticeWindowDays"))
            return Blocked("contracts_clause_invalid", drafts);
        if (!fields.TryGetValue("noticeWindowStart", out var startText)
            || !DateOnly.TryParseExact(startText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            return Blocked("notice_window_start_missing_or_invalid", drafts);
        if (!fields.TryGetValue("noticeWindowEnd", out var endText)
            || !DateOnly.TryParseExact(endText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            return Blocked("notice_window_end_missing_or_invalid", drafts);
        if (end < start || !int.TryParse(fields["noticeWindowDays"], NumberStyles.None, CultureInfo.InvariantCulture, out var windowDays)
            || windowDays < 1)
            return Blocked("contracts_clause_invalid", drafts);

        var citation = new ObligationSourceCitation(clause.SourceId, clause.RecordId, clause.RecordVersion);
        var scenarioAsOf = new DateTimeOffset(clause.AsOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var sourceObligation = obligation with
        {
            CurrentBasisVersion = currentBasisVersion,
            NoticeWindowStartUtc = start,
            NoticeWindowEndUtc = end,
        };
        var evaluation = Evaluate(sourceObligation, scenarioAsOf, drafts);
        var citedDraft = evaluation.NewDraft is null ? null : evaluation.NewDraft with { Citation = citation };
        var citedDrafts = citedDraft is null
            ? evaluation.Drafts
            : evaluation.Drafts.Select(draft => draft.Id == citedDraft.Id ? citedDraft : draft).ToArray();

        // The sender is an observation seam only; evaluating a draft never sends a notice.
        _ = sender;
        return evaluation with { Drafts = citedDrafts, NewDraft = citedDraft, Citation = citation };
    }

    private static ObligationEvaluation Blocked(string reason, IReadOnlyList<NoticeDraft> drafts) =>
        new(ObligationState.Blocked, [reason], drafts, null);

    private static SourceRecord<ContractsPayload> ReadPinnedClause(string basisContent, string caseId)
    {
        var basis = JsonSerializer.Deserialize<SourcePinDocument>(basisContent, PinnedSourceJson);
        if (basis is null || basis.Format != SourceBasisFormat || basis.Pins is null || basis.Pins.Count == 0)
            throw new InvalidDataException("Committed source basis is invalid.");

        var clauses = new List<SourceRecord<ContractsPayload>>();
        foreach (var pin in basis.Pins)
        {
            if (pin is null) throw new InvalidDataException("Source basis contains a null pin.");
            if (pin.SourceId != "contracts") continue;
            if (pin.RecordVersion < 1 || string.IsNullOrWhiteSpace(pin.RecordId) || string.IsNullOrWhiteSpace(pin.Content)
                || !string.Equals(pin.ContentDigest, Digest(pin.Content), StringComparison.Ordinal))
                throw new InvalidDataException("Contracts source pin is invalid.");

            var record = JsonSerializer.Deserialize<SourceRecord<ContractsPayload>>(pin.Content, PinnedSourceJson);
            if (record is null || record.SourceId != pin.SourceId || record.RecordId != pin.RecordId
                || record.RecordVersion != pin.RecordVersion || record.Validate(caseId).Count > 0)
                throw new InvalidDataException("Contracts source pin does not match its envelope.");
            if (record.Kind == "obligation_clause") clauses.Add(record);
        }

        return clauses.Count switch
        {
            0 => throw new PinnedContractsClauseMissingException(),
            1 => clauses[0],
            _ => throw new PinnedContractsConflictException(),
        };
    }

    private static bool HasText(IReadOnlyDictionary<string, string> fields, string name) =>
        fields.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value);

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

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
