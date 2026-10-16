using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;

namespace AirlineDemo.Api.SourceSystems;

public sealed record SourceModeRequest(string? CommandId, long? ExpectedRevision, string? Mode);

public static class SourceModeEndpoints
{
    public const string PresenterRole = "presenter";

    public static IEndpointRouteBuilder MapSourceModeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/cases/{caseId}/sources/{sourceId}/mode", SetMode);
        return app;
    }

    // Actor and time come from the session and server clock; any actor, time or reason in the body is ignored.
    private static IResult SetMode(string caseId, string sourceId, SourceModeRequest? body, HttpContext http,
        DemoSessionStore sessions, ICaseStateStore cases, SourceModeStore modes)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!string.Equals(session.User.Role, PresenterRole, StringComparison.Ordinal))
        {
            return Error(StatusCodes.Status403Forbidden, "forbidden", "Only a presenter can change source modes.");
        }

        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session)) return DemoAuthEndpoints.AntiForgeryFailed();

        var scope = TechnicalReviewPolicy.ScopeFor(cases, session.User.CaseScopes, caseId);
        if (scope is null) return Results.NotFound();

        if (body is null || string.IsNullOrWhiteSpace(body.CommandId) || body.ExpectedRevision is null || body.Mode is null)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "commandId, expectedRevision and mode are required.");
        }

        var result = modes.Set(scope, session.User.Id, body.CommandId, body.ExpectedRevision.Value, sourceId, body.Mode);
        return result.Outcome switch
        {
            SourceModeOutcome.Recorded or SourceModeOutcome.Replayed => Results.Ok(result.Receipt),
            SourceModeOutcome.CommandIdConflict => Error(StatusCodes.Status409Conflict, "command_id_conflict", "This command ID was already used for a different command."),
            SourceModeOutcome.StaleRevision => Error(StatusCodes.Status409Conflict, "stale_revision", "The case revision has changed; reload and retry."),
            SourceModeOutcome.UnknownSource => Error(StatusCodes.Status422UnprocessableEntity, "unknown_source", "Source is not one of the five case sources."),
            SourceModeOutcome.InvalidMode => Error(StatusCodes.Status422UnprocessableEntity, "invalid_mode", "Mode must be available, unavailable, stale or conflicting."),
            _ => Error(StatusCodes.Status422UnprocessableEntity, "mode_unchanged", "The source already has this mode."),
        };
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: status);
}
