using AirlineDemo.Api.Dashboard;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Evidence;

/// <summary>How a records investigation ended, as shown on the case journey.</summary>
public sealed record InvestigationHandover(string Outcome, string Reason, IReadOnlyList<InvestigationClaim> Claims);

/// <summary>
/// Puts a submitted records package on the case journey. Intake pins the accepted documents as a new case evidence
/// basis; after the records agent has run, its outcome and verified findings are recorded and a technical-review work
/// item is opened, so the case waits for a person whatever the agent concluded. Event ids derive from the package id,
/// so a repeated submission adds nothing.
/// </summary>
public static class IntakeJourneyHandover
{
    public const string IntakeActor = "intake";
    public const string AgentActor = "records-agent";
    public const int MaxSummaryLength = 400;
    private const int MaxAttempts = 3;

    public static void RecordIntake(ICaseStateStore store, CaseScope scope, EvidencePackageRecord record)
    {
        var accepted = Accepted(record);
        if (accepted.Count == 0) return;
        var eventId = $"{record.PackageId}-received";
        Commit(store, scope, eventId, state =>
        {
            var version = state.Basis.Count + 1;
            var basis = CaseStateStore.CreateBasisVersion(version, $"Records package {record.PackageId}",
                state.Basis.Count == 0 ? string.Empty : state.Basis[^1].BasisDigest);
            var rejected = record.Documents.Count - accepted.Count;
            var summary = $"Records package received: {accepted.Count} document{(accepted.Count == 1 ? "" : "s")} parsed and pinned as evidence basis version {version}" +
                          (rejected > 0 ? $"; {rejected} refused by intake." : ".");
            return new CaseCommit(basis, null, IntakeActor, "journey", summary,
                JourneyEvent: Event(eventId, record, scope.CaseId, "package-received", version, IntakeActor, summary,
                    CapabilityLabels.RulesBased, accepted.Select(d => (d.FileName, d.Version!.Value, 1))));
        });
    }

    public static void RecordInvestigation(ICaseStateStore store, CaseScope scope, EvidencePackageRecord record, InvestigationHandover handover)
    {
        var accepted = Accepted(record);
        if (accepted.Count == 0) return;
        var versions = accepted.GroupBy(d => d.FileName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(d => d.Version!.Value), StringComparer.Ordinal);

        var reviewed = handover.Outcome == "approved"
            ? $"Records agent read {versions.Count} document{(versions.Count == 1 ? "" : "s")} with read-only tools and returned {handover.Claims.Count} cited finding{(handover.Claims.Count == 1 ? "" : "s")}, each quote checked against its source page."
            : handover.Reason;
        Commit(store, scope, $"{record.PackageId}-agent", state => new CaseCommit(null, null, AgentActor, "journey", reviewed,
            JourneyEvent: Event($"{record.PackageId}-agent", record, scope.CaseId,
                handover.Outcome == "approved" ? "evidence-reviewed" : "investigation-escalated", state.Basis.Count, AgentActor,
                reviewed, CapabilityLabels.AiAgent, versions.Select(pair => (pair.Key, pair.Value, 1)))));

        for (var index = 0; index < handover.Claims.Count; index++)
        {
            var claim = handover.Claims[index];
            var eventId = $"{record.PackageId}-finding-{index + 1}";
            var summary = Shorten(claim.Text);
            var witnesses = claim.Witnesses
                .Where(w => versions.ContainsKey(w.DocumentId))
                .Select(w => (w.DocumentId, versions[w.DocumentId], Math.Max(1, w.Page)))
                .ToArray();
            Commit(store, scope, eventId, state => new CaseCommit(null, null, AgentActor, "journey", summary,
                JourneyEvent: Event(eventId, record, scope.CaseId, "finding-raised", state.Basis.Count, AgentActor, summary,
                    CapabilityLabels.AiAgent, witnesses)));
        }

        var itemId = $"review-{scope.CaseId}";
        for (var attempt = 1; ; attempt++)
        {
            var state = store.Read(scope);
            if ((state.WorkItems ?? []).Any(item => item.ItemId == itemId)) return;
            try
            {
                store.CreateWorkItem(scope, state.Revision,
                    new WorkItemDraft(itemId, AssessmentExecutionService.ReviewCapability, "open", AssessmentExecutionService.ReviewOwner),
                    AgentActor);
                return;
            }
            catch (RevisionConflictException) when (attempt < MaxAttempts)
            {
            }
        }
    }

    private static List<EvidenceDocumentResult> Accepted(EvidencePackageRecord record) =>
        record.Documents.Where(d => d.Status == "accepted" && d.Version is not null).ToList();

    private static void Commit(ICaseStateStore store, CaseScope scope, string eventId, Func<CaseState, CaseCommit> build)
    {
        for (var attempt = 1; ; attempt++)
        {
            var state = store.Read(scope);
            if ((state.JourneyEvents ?? []).Any(e => e.EventId == eventId)) return;
            try
            {
                store.Commit(scope, state.Revision, build(state));
                return;
            }
            catch (RevisionConflictException) when (attempt < MaxAttempts)
            {
            }
        }
    }

    private static CaseJourneyEventDraft Event(string eventId, EvidencePackageRecord record, string caseId, string eventType,
        int basisVersion, string actor, string summary, string label, IEnumerable<(string DocumentId, int Version, int Page)> witnesses)
    {
        var basisId = $"basis-v{basisVersion}";
        return new CaseJourneyEventDraft(eventId, record.PackageId, caseId, eventType, basisId, basisVersion, actor, summary, label,
            witnesses.Select((w, position) => new CaseJourneyWitness($"{eventId}-w{position + 1}", w.DocumentId, w.Version, w.Page,
                basisId, basisVersion)).ToArray());
    }

    private static string Shorten(string text)
    {
        var single = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length <= MaxSummaryLength ? single : single[..(MaxSummaryLength - 1)] + "…";
    }
}
