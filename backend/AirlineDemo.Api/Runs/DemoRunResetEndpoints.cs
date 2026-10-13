using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Runs;

public sealed record DemoRunResetRequest(string NewRunId, string? FixtureId, string? ApplicationVersion);

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
            var result = reset.Reset(runId, session.User.Id, body.NewRunId, body.FixtureId, body.ApplicationVersion);
            sessions.GrantScope(session.Id, result.NewRunId);
            return Results.Ok(result);
        }
        catch (CaseAccessDeniedException)
        {
            return Results.NotFound();
        }
        catch (DemoRunResetException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException)
        {
            return Results.Conflict(new { error = "A run with that id already exists." });
        }
    }
}
