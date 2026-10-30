using System.Text.Json;
using AirlineDemo.Api.Dashboard;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Finance;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;
using Microsoft.Extensions.Configuration;

namespace AirlineDemo.Api.Auth;

/// <summary>
/// Opt-in demo seeding (<c>Demo:SeedJourney=true</c>) that moves the three case-pack aircraft to different
/// return-readiness stages, so the journey view and the 3D hangar have evidence and history to show.
/// Only cases that are still empty (revision 0) are touched; existing work is never changed.
/// </summary>
public static class DemoJourneySeeder
{
    private const string Owner = "presenter";
    private const string Reviewer = "reviewer";
    private const string FinanceReviewer = "fin-1";
    private const string RunId = "demo-run-1";

    private sealed record Plan(string CaseId, string Prefix, int Stage, string[] Documents);

    // Stage 1: awaiting technical review. Stage 2: awaiting finance. Stage 3: awaiting legal.
    private static readonly Plan[] Plans =
    [
        new("cp-case-c", "case-c/", 1, ["aircraft-summary.txt", "records-index.txt", "engine-serial-record.txt", "maintenance-log.txt"]),
        new("cp-case-b", "case-b/", 2, ["aircraft-summary.txt", "records-index.txt", "engine-serial-record.txt", "identity-conflict.txt"]),
        new("cp-case-a", "case-a/", 3, ["aircraft-summary.txt", "records-index.txt", "engine-serial-record.txt", "llp-history-gap.txt"]),
    ];

    public static void Seed(IConfiguration configuration, ICaseStateStore store, EvidencePackageStore? packages = null)
    {
        if (!bool.TryParse(configuration["Demo:SeedJourney"], out var enabled) || !enabled) return;
        foreach (var plan in Plans)
        {
            // The package store is in memory, so the accepted receipt is registered on every start; without it the
            // scene projection treats the cited documents as unprocessed and drops their witnesses.
            packages?.Submit($"demo-package-{plan.CaseId}", plan.CaseId, () => AcceptedPackage(plan));
            if (store.OwnerOf(plan.CaseId) != Owner) continue;
            var scope = store.AuthorizeCase(plan.CaseId, Owner);
            if (store.Read(scope).Revision != 0) continue;
            try
            {
                SeedCase(store, scope, plan);
            }
            catch (RevisionConflictException)
            {
                // Another process advanced the case first; leave its state as it is.
            }
        }
    }

    public static async Task SeedSourceEvidenceAsync(IConfiguration configuration, ICaseStateStore store,
        Func<string, Task<GatheredSourceBasis>> gather)
    {
        const string caseId = "case-001";
        if (!bool.TryParse(configuration["Demo:SeedJourney"], out var enabled) || !enabled
            || store.OwnerOf(caseId) != Owner) return;
        var scope = store.AuthorizeCase(caseId, Owner);
        if (store.Read(scope).Revision != 0) return;
        var sourceBasis = await gather(caseId);
        try
        {
            store.Commit(scope, 0, new CaseCommit(CaseStateStore.CreateBasisVersion(1, sourceBasis.Content, ""),
                null, "demo-source-seeder", "source-basis.gathered",
                "Signed synthetic source records pinned for the separate source-system demonstration."));
        }
        catch (RevisionConflictException)
        {
            // An intervening operation wins; existing evidence and decisions must not be rewritten.
        }
    }

    private static void SeedCase(ICaseStateStore store, CaseScope scope, Plan plan)
    {
        var basis = CaseStateStore.CreateBasisVersion(1, $"Return evidence package for {plan.CaseId}", string.Empty);
        var witnesses = plan.Documents;

        Journey(store, scope, plan, 1, basis, "package-received", Owner,
            "Return records package received from the outgoing lessee and parsed.", CapabilityLabels.RulesBased, witnesses[..2]);
        Journey(store, scope, plan, 2, null, "evidence-reviewed", "records-agent",
            "AI agent cross-checked engine identity and component records against the records index.", CapabilityLabels.AiAgent, witnesses[1..3]);
        Journey(store, scope, plan, 3, null, "findings-raised", "records-agent",
            "AI agent raised findings for a records gap and routed them to technical review.", CapabilityLabels.AiAgent, witnesses[2..]);

        var state = store.Read(scope);
        store.CreateWorkItem(scope, state.Revision,
            new WorkItemDraft($"review-{plan.CaseId}", AssessmentExecutionService.ReviewCapability, "open", AssessmentExecutionService.ReviewOwner),
            AssessmentExecutionService.Actor);
        if (plan.Stage < 2) return;

        state = store.Read(scope);
        TechnicalReviewPolicy.Decide(store, [plan.CaseId], CaseEndpoints.ReviewerRole, Reviewer, plan.CaseId,
            new TechnicalReviewCommand(state.Revision, 1, "accept", "Findings verified against the source records."), "seed-trace");
        state = store.Read(scope);
        store.ChangeWorkItemState(scope, state.Revision, $"review-{plan.CaseId}", "closed", Reviewer);
        Journey(store, scope, plan, 4, null, "technical-review-decided", "technical-reviewer",
            "Technical reviewer accepted the AI findings.", CapabilityLabels.Simulated, witnesses[..1]);
        if (plan.Stage < 3) return;

        state = store.Read(scope);
        store.Commit(scope, state.Revision, new CaseCommit(null, null, FinanceReviewer, FinanceEndpoints.ReserveDecisionAction,
            JsonSerializer.Serialize(new { decision = "approve", reason = "Reserve lines match the utilisation report." })));
        Journey(store, scope, plan, 5, null, "reserve-decided", "finance-reviewer",
            "Finance reviewer approved the maintenance reserve worksheet.", CapabilityLabels.Simulated, witnesses[..1]);
    }

    private static EvidencePackageOutcome AcceptedPackage(Plan plan)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "aircraft-case-pack", plan.Prefix.TrimEnd('/'));
        var files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.txt").Select(Path.GetFileName).OfType<string>() : [];
        var documents = files.Concat(plan.Documents).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(file => new EvidenceDocumentResult(file, "accepted", "accepted", 1, null)).ToArray();
        return new EvidencePackageOutcome("complete", documents);
    }

    private static void Journey(ICaseStateStore store, CaseScope scope, Plan plan, int index, BasisVersion? basis,
        string eventType, string actorRole, string summary, string label, string[] documents)
    {
        var state = store.Read(scope);
        store.Commit(scope, state.Revision, new CaseCommit(basis, null, actorRole, "journey", summary,
            JourneyEvent: new CaseJourneyEventDraft($"{plan.CaseId}-event-{index}", RunId, plan.CaseId, eventType,
                "basis-v1", 1, actorRole, summary, label,
                documents.Select((document, position) => new CaseJourneyWitness(
                    $"w-{plan.CaseId}-{index}-{position + 1}", plan.Prefix + document, 1, 1, "basis-v1", 1)).ToArray())));
    }
}
