using AirlineDemo.Api.Dashboard;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;

namespace AirlineDemo.Api.Auth;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cases/{caseId}/dashboard", GetDashboard);
        app.MapGet("/api/cases/{caseId}/dashboard/traces/{runId}/{investigationId}", GetTrace);
        app.MapGet("/api/dashboard/readiness-summary", GetReadinessSummary);
        return app;
    }

    private static IResult GetReadinessSummary(
        int? contractVersion,
        string? caseId,
        HttpContext http,
        DemoSessionStore sessions,
        ICaseStateStore store,
        EvidencePackageStore packages)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (session.User.Role is not (EvidenceEndpoints.PresenterRole or CapabilityProjection.ReviewerRole))
            return Results.Json(new { error = "Role not permitted." }, statusCode: StatusCodes.Status403Forbidden);
        if (caseId is not null && caseId != ReadinessSummaryProjection.FixtureCaseId) return Results.NotFound();
        var scope = TechnicalReviewPolicy.ScopeFor(store, session.User.CaseScopes, ReadinessSummaryProjection.FixtureCaseId);
        if (scope is null) return Results.NotFound();
        if (contractVersion != ReadinessSummaryProjection.ContractVersion)
            return Results.Json(new { error = "Unsupported contract version." }, statusCode: StatusCodes.Status400BadRequest);

        var state = store.Read(scope);
        var packageProjections = (state.Traces ?? [])
            .Select(trace => packages.Find(scope.CaseId, trace.RunId))
            .ToArray();
        var summary = ReadinessSummaryProjection.Build(state, packageProjections);
        return summary is null
            ? Results.Json(new { contractVersion = ReadinessSummaryProjection.ContractVersion, status = "unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(summary);
    }

    private static IResult GetDashboard(
        string caseId,
        HttpContext http,
        DemoSessionStore sessions,
        ICaseStateStore store,
        TimeProvider time)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();

        var scope = TechnicalReviewPolicy.ScopeFor(store, session.User.CaseScopes, caseId);
        if (scope is null) return Results.NotFound();

        var state = store.Read(scope);
        return Results.Ok(CapabilityProjection.Build(caseId, session.User.Role, state, time.GetUtcNow()));
    }

    private static IResult GetTrace(
        string caseId,
        string runId,
        string investigationId,
        HttpContext http,
        DemoSessionStore sessions,
        ICaseStateStore store)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();

        var scope = TechnicalReviewPolicy.ScopeFor(store, session.User.CaseScopes, caseId);
        if (scope is null) return Results.NotFound();

        var trace = store.FindTrace(scope, caseId, runId, investigationId);
        return trace is null ? Results.NotFound() : Results.Ok(trace);
    }
}
