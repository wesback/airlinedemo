using System.Globalization;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;

namespace AirlineDemo.Api.Dashboard;

public sealed record CaseReadinessSummary(int MonthsUntilReturn, int GapsFound, int RequestsSent, int HeldForReview, int NotProcessed, string Sentence);

public static class CaseReadinessSummaryProjection
{
    private static readonly DateOnly ScenarioNow = new(2025, 1, 10);
    private static readonly DateOnly ReturnDate = new(2025, 10, 10);

    public static CaseReadinessSummary Build(CaseState state, IReadOnlyList<EvidencePackageRecord> packages)
    {
        var gaps = (state.Assessments ?? [])
            .Where(assessment => assessment.Outcome == "Request")
            .Sum(assessment => assessment.Claims.Count(claim => claim.Kind == "MissingRequirement"));
        var requests = state.RequestReceipts?.Count ?? 0;
        var held = (state.WorkItems ?? []).Count(item =>
            item.State == "open" && item.Owner == CaseEndpoints.ReviewerRole);
        var notProcessed = packages.SelectMany(package => package.Documents)
            .Count(document => document.Status != "accepted");
        var months = WholeMonthsUntil(ScenarioNow, ReturnDate);
        var sentence = $"Return in {months} months. {Count(gaps, "records gap", "records gaps")} found, " +
            $"{Count(requests, "routine request", "routine requests")} sent, " +
            $"{Count(held, "item", "items")} held for internal review, " +
            $"{Count(notProcessed, "document", "documents")} not processed.";
        return new CaseReadinessSummary(months, gaps, requests, held, notProcessed, sentence);
    }

    public static int WholeMonthsUntil(DateOnly scenarioNow, DateOnly returnDate)
    {
        var months = (returnDate.Year - scenarioNow.Year) * 12 + returnDate.Month - scenarioNow.Month;
        if (returnDate.Day < scenarioNow.Day) months--;
        return Math.Max(0, months);
    }

    private static string Count(int count, string singular, string plural) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";
}

public static class ReadinessSummaryEndpoints
{
    public static IEndpointRouteBuilder MapReadinessSummaryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cases/{caseId}/readiness", GetReadinessSummary);
        return app;
    }

    private static IResult GetReadinessSummary(
        string caseId,
        HttpContext http,
        DemoSessionStore sessions,
        ICaseStateStore cases,
        EvidencePackageStore packages)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();

        var scope = TechnicalReviewPolicy.ScopeFor(cases, session.User.CaseScopes, caseId);
        if (scope is null) return Results.NotFound();

        var state = cases.Read(scope);
        return Results.Ok(CaseReadinessSummaryProjection.Build(state, packages.ListForCase(caseId)));
    }
}
