using AirlineDemo.Api.Admission;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Runs;

public sealed record DemoRunResetRequest(string NewRunId, string? FixtureId, string? ApplicationVersion);
public sealed record BlockedVariantResetRequest(string? CommandId, long? ExpectedRevision, string? Variant);

public static class DemoRunResetEndpoints
{
    public static IEndpointRouteBuilder MapDemoRunReset(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/runs/{runId}/reset", ResetRun);
        app.MapPost("/api/v1/cases/{caseId}/reset-blocked-variant", ResetBlockedVariant);
        return app;
    }

    private static async Task<IResult> ResetBlockedVariant(
        string caseId,
        HttpContext http,
        DemoSessionStore sessions,
        CasePackRequestLimiter limiter,
        ICaseStateStore store,
        CasePackBlockedVariantReset reset)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return ResetError(401, "unauthenticated", "Not signed in.");
        if (!await limiter.TryRecordAttemptAsync(session.User.Id, CasePackRequestLimiter.ResetEndpoint, http.RequestAborted))
            return ResetError(429, "rate_limit_exceeded", "rate_limit_exceeded");
        if (!string.Equals(session.User.Role, "presenter", StringComparison.Ordinal))
            return ResetError(403, "forbidden", "Only a presenter can reset the blocked variant.");
        if (!session.User.CaseScopes.Contains(caseId, StringComparer.Ordinal))
            return ResetError(404, "not_found", "Case not found.");
        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session))
            return ResetError(403, "anti_forgery_failed", "Anti-forgery validation failed.");

        BlockedVariantResetRequest? body;
        if (!http.Request.HasJsonContentType())
            return ResetError(400, "validation_error", "Reset command fields are invalid.");
        try
        {
            body = await http.Request.ReadFromJsonAsync<BlockedVariantResetRequest>(cancellationToken: http.RequestAborted);
        }
        catch (System.Text.Json.JsonException)
        {
            return ResetError(400, "validation_error", "Reset command fields are invalid.");
        }

        if (body is null || body.ExpectedRevision is null || body.Variant is null)
            return ResetError(400, "validation_error", "Reset command fields are invalid.");

        try
        {
            var scope = store.AuthorizeCase(caseId, session.User.Id);
            var result = reset.Reset(scope, session.User.Id, body.CommandId ?? string.Empty,
                body.ExpectedRevision.Value, body.Variant);
            return Results.Json(new
            {
                contractVersion = result.Receipt.ContractVersion,
                result.Receipt.CommandId,
                result.Receipt.CaseId,
                result.Receipt.PriorRevision,
                result.Receipt.NewRevision,
                result.Receipt.NewRunId,
                result.Receipt.AuditEventId,
            }, statusCode: 200);
        }
        catch (CaseAccessDeniedException)
        {
            return ResetError(404, "not_found", "Case not found.");
        }
        catch (CasePackBlockedVariantResetException exception)
        {
            return ResetError(exception.StatusCode, exception.Code, exception.Message);
        }
        catch (IOException)
        {
            return ResetError(503, "audit_write_failed", "The reset could not be recorded.");
        }
    }

    private static IResult ResetError(int statusCode, string code, string message) =>
        Results.Json(new { contractVersion = 1, error = new { code, message } }, statusCode: statusCode);

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
