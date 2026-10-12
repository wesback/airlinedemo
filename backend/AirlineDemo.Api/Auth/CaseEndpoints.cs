namespace AirlineDemo.Api.Auth;

public sealed record ReviewRequest(int ExpectedRevision, string? Decision);

public static class CaseEndpoints
{
    public const string ReviewerRole = "technical-reviewer";

    public static IEndpointRouteBuilder MapCaseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cases/{caseId}", GetCase);
        app.MapPost("/api/cases/{caseId}/reviews", RecordReview);
        return app;
    }

    private static IResult GetCase(string caseId, HttpContext http, DemoSessionStore sessions, CaseStore cases)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!session.User.CaseScopes.Contains(caseId)) return Results.NotFound();

        var record = cases.Find(caseId);
        return record is null ? Results.NotFound() : Results.Ok(record);
    }

    private static IResult RecordReview(string caseId, ReviewRequest body, HttpContext http, DemoSessionStore sessions, CaseStore cases)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session)) return DemoAuthEndpoints.AntiForgeryFailed();
        if (session.User.Role != ReviewerRole)
            return Results.Json(new { error = "Role not permitted." }, statusCode: StatusCodes.Status403Forbidden);
        if (!session.User.CaseScopes.Contains(caseId)) return Results.NotFound();
        if (body.Decision is not ("accept" or "reject"))
            return Results.BadRequest(new { error = "Decision must be accept or reject." });

        var outcome = cases.RecordReview(caseId, body.ExpectedRevision, body.Decision, out var updated);
        return outcome switch
        {
            ReviewOutcome.Recorded => Results.Ok(updated),
            ReviewOutcome.Stale => Results.Json(new { error = "Case revision is stale." }, statusCode: StatusCodes.Status409Conflict),
            _ => Results.NotFound(),
        };
    }
}
