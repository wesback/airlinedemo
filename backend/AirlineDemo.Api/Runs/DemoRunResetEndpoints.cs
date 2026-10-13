using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Runs;

public sealed record DemoRunResetRequest(string NewRunId);

public static class DemoRunResetEndpoints
{
    public static IEndpointRouteBuilder MapDemoRunReset(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/runs/{runId}/reset", ResetRun);
        return app;
    }

    private static IResult ResetRun(string runId, DemoRunResetRequest body, HttpContext http, DemoSessionStore sessions, DemoRunReset reset)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session)) return DemoAuthEndpoints.AntiForgeryFailed();
        if (!session.User.CaseScopes.Contains(runId)) return Results.NotFound();

        try
        {
            return Results.Ok(reset.Reset(runId, session.User.Id, body.NewRunId));
        }
        catch (CaseAccessDeniedException)
        {
            return Results.NotFound();
        }
        catch (DemoRunResetException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
