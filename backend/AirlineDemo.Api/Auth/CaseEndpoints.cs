using System.Security.Cryptography;
using AirlineDemo.Api.Diagnostics;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Auth;

public sealed record ReviewRequest(int ExpectedRevision, string? Decision);

public static class CaseEndpoints
{
    public const string ReviewerRole = "technical-reviewer";

    // Per-process key: actor pseudonyms correlate within a run and are not reversible from traces.
    internal static readonly byte[] ActorKey = RandomNumberGenerator.GetBytes(32);

    public static IEndpointRouteBuilder MapCaseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cases/{caseId}", GetCase);
        app.MapPost("/api/cases/{caseId}/reviews", RecordReview);
        return app;
    }

    private static IResult GetCase(string caseId, HttpContext http, DemoSessionStore sessions, CaseStore cases, IConfiguration config, ILogger<DemoSessionStore> logger)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null)
        {
            EmitScopeFailure(logger, config);
            return DemoAuthEndpoints.Unauthenticated();
        }
        if (!session.User.CaseScopes.Contains(caseId))
        {
            EmitScopeFailure(logger, config);
            return Results.NotFound();
        }

        var record = cases.Find(caseId);
        return record is null ? Results.NotFound() : Results.Ok(record);
    }

    private static void EmitScopeFailure(ILogger<DemoSessionStore> logger, IConfiguration config) =>
        TelemetryRedaction.EmitSecurityEvent(logger, TelemetryRedaction.SecurityEvents.AuthScopeFailure,
            TelemetryRedaction.ServiceVersion, config["Telemetry:Environment"] ?? "demo");

    private static IResult RecordReview(string caseId, ReviewRequest body, HttpContext http, DemoSessionStore sessions, CaseStore cases, IConfiguration config, ILogger<DemoSessionStore> logger)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null)
        {
            EmitScopeFailure(logger, config);
            return DemoAuthEndpoints.Unauthenticated();
        }
        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session)) return DemoAuthEndpoints.AntiForgeryFailed();
        if (session.User.Role != ReviewerRole)
        {
            EmitScopeFailure(logger, config);
            return Results.Json(new { error = "Role not permitted." }, statusCode: StatusCodes.Status403Forbidden);
        }
        if (!session.User.CaseScopes.Contains(caseId))
        {
            EmitScopeFailure(logger, config);
            return Results.NotFound();
        }
        if (body.Decision is not ("accept" or "reject"))
            return Results.BadRequest(new { error = "Decision must be accept or reject." });

        CaseRecord? updated;
        ReviewOutcome outcome;
        try
        {
            outcome = cases.RecordReview(caseId, body.ExpectedRevision, body.Decision, out updated);
        }
        catch (BasisIntegrityException)
        {
            TelemetryRedaction.EmitSecurityEvent(logger, TelemetryRedaction.SecurityEvents.IntegritySignatureFailure,
                TelemetryRedaction.ServiceVersion, config["Telemetry:Environment"] ?? "demo");
            return Results.Json(new { error = "Case integrity check failed." }, statusCode: StatusCodes.Status409Conflict);
        }

        if (outcome == ReviewOutcome.Recorded && updated is not null)
        {
            var accepted = body.Decision == "accept";
            var trace = new TelemetryRedaction.InvestigationTraceRecord(
                "trace-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
                "basis-v" + updated.Revision,
                // The review endpoint runs no model call, so no deployment is reported.
                "not-invoked",
                Array.Empty<string>(),
                Array.Empty<string>(),
                accepted ? "return-ready" : "return-not-ready",
                session.User.Id,
                accepted ? "approved" : "rejected");
            TelemetryRedaction.EmitInvestigationTrace(logger, trace,
                TelemetryRedaction.ServiceVersion, config["Telemetry:Environment"] ?? "demo", ActorKey, "review.completed");
        }

        return outcome switch
        {
            ReviewOutcome.Recorded => Results.Ok(updated),
            ReviewOutcome.Stale => Results.Json(new { error = "Case revision is stale." }, statusCode: StatusCodes.Status409Conflict),
            _ => Results.NotFound(),
        };
    }
}
