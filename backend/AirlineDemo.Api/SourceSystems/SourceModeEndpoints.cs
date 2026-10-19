using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AirlineDemo.Api.SourceSystems;

public sealed record SourceModeRequest(string? CommandId, long? ExpectedRevision, string? Mode);

public static class SourceModeEndpoints
{
    public const string PresenterRole = "presenter";
    public const int MaxCommandIdLength = 64;

    public static IEndpointRouteBuilder MapSourceModeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/cases/{caseId}/sources/{sourceId}/mode", SetMode);
        return app;
    }

    // Actor and time come from the session and server clock; any actor, time or reason in the body is ignored.
    private static async Task<IResult> SetMode(string caseId, string sourceId, HttpContext http,
        DemoSessionStore sessions, ICaseStateStore cases, SourceModeStore modes, ILogger<SourceModeStore> logger,
        IHostEnvironment environment)
    {
        var diagnosticStartedAt = Stopwatch.GetTimestamp();
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "Not signed in.");
        if (!string.Equals(session.User.Role, PresenterRole, StringComparison.Ordinal))
        {
            return Error(StatusCodes.Status403Forbidden, "forbidden", "Only a presenter can change source modes.");
        }

        var scope = TechnicalReviewPolicy.ScopeFor(cases, session.User.CaseScopes, caseId);
        if (scope is null) return Error(StatusCodes.Status404NotFound, "not_found", "Case not found.");

        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session))
        {
            return Error(StatusCodes.Status403Forbidden, "anti_forgery_failed", "Anti-forgery validation failed.");
        }

        SourceModeRequest? body;
        if (http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == false || http.Request.ContentLength == 0)
        {
            body = null;
        }
        else if (!http.Request.HasJsonContentType())
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "Request body must be a valid source-mode command.");
        }
        else
        {
            try
            {
                body = await http.Request.ReadFromJsonAsync<SourceModeRequest>(cancellationToken: http.RequestAborted);
            }
            catch (JsonException)
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_request", "Request body must be a valid source-mode command.");
            }
        }

        if (body is null || string.IsNullOrWhiteSpace(body.CommandId) || body.ExpectedRevision is null || body.Mode is null)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "commandId, expectedRevision and mode are required.");
        }

        if (body.CommandId.Length > MaxCommandIdLength)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", $"commandId must be at most {MaxCommandIdLength} characters.");
        }

        if (!SourceModes.Sources.Contains(sourceId, StringComparer.Ordinal))
        {
            return Error(StatusCodes.Status422UnprocessableEntity, "unknown_source", "Source is not one of the five case sources.");
        }

        if (!SourceModes.Modes.Contains(body.Mode, StringComparer.Ordinal))
        {
            return Error(StatusCodes.Status422UnprocessableEntity, "invalid_mode", "Mode must be available, unavailable, stale or conflicting.");
        }

        SourceModeResult result;
        try
        {
            result = modes.Set(scope, session.User.Id, body.CommandId, body.ExpectedRevision.Value, sourceId, body.Mode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DbUpdateException)
        {
            SourceSystemDiagnostics.AuditWriteFailed(logger, environment.EnvironmentName, sourceId, caseId,
                SourceSystemDiagnostics.ElapsedMilliseconds(diagnosticStartedAt));
            return Error(StatusCodes.Status503ServiceUnavailable, "audit_write_failed", "The source mode could not be recorded.");
        }

        if (result.Outcome == SourceModeOutcome.Recorded)
            SourceSystemDiagnostics.ModeChangeAccepted(logger, environment.EnvironmentName, sourceId, caseId,
                SourceSystemDiagnostics.ElapsedMilliseconds(diagnosticStartedAt));
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
        Results.Json(new { contractVersion = 1, error = new { code, message } }, statusCode: status);
}
