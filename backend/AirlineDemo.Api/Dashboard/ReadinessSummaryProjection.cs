using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Evidence;

namespace AirlineDemo.Api.Dashboard;

public sealed record ReadinessSummary(int ContractVersion, string Countdown, int RecordsGapsFound, int RoutineRequestsSent, int ItemsHeldForInternalReview, int DocumentsNotProcessed, string Summary);

public static class ReadinessSummaryProjection
{
    public const int ContractVersion = 1;
    public const string FixtureCaseId = "case-001";

    private static readonly DateOnly ScenarioDate = new(2025, 1, 10), ReturnDate = new(2025, 10, 10);

    public static ReadinessSummary? Build(CaseState state, IReadOnlyList<EvidencePackageRecord?> packages)
    {
        if (state.CaseId != FixtureCaseId || state.Assessments is null || state.RequestReceipts is null || state.WorkItems is null
            || state.Traces is null || state.Traces.Count == 0 || packages.Count != state.Traces.Count || packages.Any(package => package is null)) return null;
        if (state.Assessments.Any(item => item.CaseId != state.CaseId) || state.RequestReceipts.Any(item => item.CaseId != state.CaseId)
            || state.WorkItems.Any(item => item.CaseId != state.CaseId) || state.Traces.Any(trace => trace.CaseId != state.CaseId)
            || state.Traces.Select(trace => trace.RunId).Distinct(StringComparer.Ordinal).Count() != state.Traces.Count
            || packages.Where((package, index) => package is null || package.CaseId != state.CaseId || package.PackageId != state.Traces[index].RunId
                || package.Documents is null || package.Documents.Count == 0).Any()) return null;

        var reviewItems = state.WorkItems.Where(item => item.CapabilityId == AssessmentExecutionService.ReviewCapability
            && item.Owner == CapabilityProjection.ReviewerRole).ToArray();
        var reviews = state.Assessments.Where(item => item.Outcome == AssessmentExecutionService.Review).ToArray();
        if (reviewItems.Any(item => !reviews.Any(review => review.WorkItemId == item.ItemId))
            || reviews.Any(review => review.WorkItemId is null || !reviewItems.Any(item => item.ItemId == review.WorkItemId))) return null;

        var requests = state.Assessments.Where(item => item.Outcome == AssessmentExecutionService.Request).ToArray();
        if (requests.Any(item => string.IsNullOrWhiteSpace(item.RequestId) || item.Claims.Count(claim => claim.Kind == ClaimKind.MissingRequirement.ToString()) != 1)
            || requests.Select(item => item.RequestId).Distinct(StringComparer.Ordinal).Count() != requests.Length) return null;

        var receipts = state.RequestReceipts;
        if (receipts.Select(item => item.RequestId).Distinct(StringComparer.Ordinal).Count() != receipts.Count
            || !requests.Select(item => item.RequestId).ToHashSet(StringComparer.Ordinal).SetEquals(receipts.Select(item => item.RequestId))) return null;

        var documents = packages.SelectMany(package => package!.Documents).ToArray();
        if (packages.Any(package => {
            var accepted = package!.Documents.Count(document => document.Status == "accepted");
            var expectedState = accepted == package.Documents.Count ? "complete" : accepted == 0 ? "failed" : "partial";
            return package.State != expectedState;
        }) || documents.Any(document => document.Status is not ("accepted" or "rejected-retryable" or "rejected-permanent")))
            return null;
        var projectedAt = new DateTimeOffset(ScenarioDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var reviewerQueue = CapabilityProjection.Build(state.CaseId, CapabilityProjection.ReviewerRole, state, projectedAt).Queue;
        var gaps = requests.Length;
        var sent = receipts.Count;
        var held = reviewerQueue.Count(item => item.CapabilityId == AssessmentExecutionService.ReviewCapability);
        var unprocessed = documents.Count(document => document.Status != "accepted");
        var months = (ReturnDate.Year - ScenarioDate.Year) * 12 + ReturnDate.Month - ScenarioDate.Month;
        if (ReturnDate.Day < ScenarioDate.Day) months--;
        var countdown = $"Return in {months} months";
        var summary = $"{countdown}. {gaps} records gap found, {sent} routine request sent, "
            + $"{held} item held for internal review, {unprocessed} documents not processed.";
        return new ReadinessSummary(ContractVersion, countdown, gaps, sent, held, unprocessed, summary);
    }
}
