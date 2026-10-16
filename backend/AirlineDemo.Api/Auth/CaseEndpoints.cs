using System.Security.Cryptography;
using AirlineDemo.Api.Diagnostics;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;

namespace AirlineDemo.Api.Auth;

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

    private static IResult GetCase(string caseId, HttpContext http, DemoSessionStore sessions, ICaseStateStore cases, IConfiguration config, ILogger<DemoSessionStore> logger)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null)
        {
            EmitScopeFailure(logger, config);
            return DemoAuthEndpoints.Unauthenticated();
        }

        var scope = TechnicalReviewPolicy.ScopeFor(cases, session.User.CaseScopes, caseId);
        if (scope is null)
        {
            EmitScopeFailure(logger, config);
            return Results.NotFound();
        }

        return Results.Ok(cases.Read(scope));
    }

    private static void EmitScopeFailure(ILogger<DemoSessionStore> logger, IConfiguration config) =>
        TelemetryRedaction.EmitSecurityEvent(logger, TelemetryRedaction.SecurityEvents.AuthScopeFailure,
            TelemetryRedaction.ServiceVersion, config["Telemetry:Environment"] ?? "demo");

    private static IResult RecordReview(string caseId, TechnicalReviewCommand body, HttpContext http, DemoSessionStore sessions, ICaseStateStore cases, IConfiguration config, ILogger<DemoSessionStore> logger)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null)
        {
            EmitScopeFailure(logger, config);
            return DemoAuthEndpoints.Unauthenticated();
        }
        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session)) return DemoAuthEndpoints.AntiForgeryFailed();

        var traceId = "trace-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        TechnicalReviewResult result;
        try
        {
            result = TechnicalReviewPolicy.Decide(cases, session.User.CaseScopes, session.User.Role, session.User.Id,
                caseId, body, traceId);
        }
        catch (BasisIntegrityException)
        {
            TelemetryRedaction.EmitSecurityEvent(logger, TelemetryRedaction.SecurityEvents.IntegritySignatureFailure,
                TelemetryRedaction.ServiceVersion, config["Telemetry:Environment"] ?? "demo");
            return Results.Json(new { error = "Case integrity check failed." }, statusCode: StatusCodes.Status409Conflict);
        }

        switch (result.Outcome)
        {
            case TechnicalReviewOutcome.Recorded:
                var accepted = result.Decision!.Decision == "accept";
                var trace = new TelemetryRedaction.InvestigationTraceRecord(
                    traceId,
                    "basis-v" + result.Decision.BasisVersion,
                    // The review endpoint runs no model call, so no deployment is reported.
                    "not-invoked",
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    accepted ? "return-ready" : "return-not-ready",
                    session.User.Id,
                    accepted ? "approved" : "rejected");
                TelemetryRedaction.EmitInvestigationTrace(logger, trace,
                    TelemetryRedaction.ServiceVersion, config["Telemetry:Environment"] ?? "demo", ActorKey, "review.completed");
                return Results.Ok(result.Decision);
            case TechnicalReviewOutcome.Forbidden:
                EmitScopeFailure(logger, config);
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status403Forbidden);
            case TechnicalReviewOutcome.Stale:
                return Results.Json(new { error = "Case revision or basis version is stale." }, statusCode: StatusCodes.Status409Conflict);
            case TechnicalReviewOutcome.Invalid:
                return Results.BadRequest(new { error = result.Error });
            default:
                return Results.NotFound();
        }
    }
}
