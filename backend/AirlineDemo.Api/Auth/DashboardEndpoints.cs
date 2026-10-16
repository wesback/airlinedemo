using AirlineDemo.Api.Dashboard;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;

namespace AirlineDemo.Api.Auth;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cases/{caseId}/dashboard", GetDashboard);
        app.MapGet("/api/cases/{caseId}/dashboard/traces/{runId}/{investigationId}", GetTrace);
        return app;
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
