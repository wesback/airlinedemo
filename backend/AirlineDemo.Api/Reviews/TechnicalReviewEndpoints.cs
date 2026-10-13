using System.Security.Cryptography;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Diagnostics;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Reviews;

public sealed record TechnicalDecisionRequest(long ExpectedRevision, int BasisVersion, string? Decision, string? Reason);

public sealed record TechnicalDecision(
    string Outcome,
    string Reason,
    int BasisVersion,
    string TraceId,
    string Actor,
    long Sequence,
    DateTimeOffset At,
    bool IsCurrent);

internal sealed record TechnicalDecisionDetail(string Outcome, string Reason, int BasisVersion, string TraceId);

public static class TechnicalReviewEndpoints
{
    public const string DecisionAction = "technical-decision";
    public const int MaxReasonLength = 500;

    // Per-process key: actor pseudonyms correlate within a run and are not reversible from traces.
    private static readonly byte[] ActorKey = RandomNumberGenerator.GetBytes(32);

    public static IEndpointRouteBuilder MapTechnicalReviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cases/{caseId}/technical-review", GetDecision);
        app.MapPost("/api/cases/{caseId}/technical-review/decisions", RecordDecision);
        return app;
    }

    /// <summary>
    /// The latest technical decision in the business audit. It is current only while it was made against
    /// the latest basis version; a later basis leaves it recorded but non-current.
    /// </summary>
    public static TechnicalDecision? CurrentDecision(CaseState state)
    {
        for (var i = state.Audit.Count - 1; i >= 0; i--)
        {
            var entry = state.Audit[i];
            if (entry.Action != DecisionAction)
            {
                continue;
            }

            var detail = JsonSerializer.Deserialize<TechnicalDecisionDetail>(entry.Detail)
                ?? throw new InvalidDataException("Technical decision audit entry is unreadable.");
            var isCurrent = state.Basis.Count > 0 && detail.BasisVersion == state.Basis.Count;
            return new TechnicalDecision(detail.Outcome, detail.Reason, detail.BasisVersion, detail.TraceId,
                entry.Actor, entry.Sequence, entry.At, isCurrent);
        }

        return null;
    }

    /// <summary>Only a current accept authorizes a request or acceptance.</summary>
    public static bool AuthorizesAcceptance(CaseState state) =>
        CurrentDecision(state) is { IsCurrent: true, Outcome: "accept" };

    private static IResult GetDecision(string caseId, HttpContext http, DemoSessionStore sessions, CaseStateStore cases, IConfiguration config, ILogger<DemoSessionStore> logger)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null)
        {
            EmitScopeFailure(logger, config);
            return DemoAuthEndpoints.Unauthenticated();
        }

        var scope = TryScope(cases, session, caseId);
        if (scope is null)
        {
            EmitScopeFailure(logger, config);
            return Results.NotFound();
        }

        var state = cases.Read(scope);
        return Results.Ok(new { state.Revision, basisVersion = state.Basis.Count, decision = CurrentDecision(state) });
    }

    private static IResult RecordDecision(string caseId, TechnicalDecisionRequest body, HttpContext http, DemoSessionStore sessions, CaseStateStore cases, IConfiguration config, ILogger<DemoSessionStore> logger)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null)
        {
            EmitScopeFailure(logger, config);
            return DemoAuthEndpoints.Unauthenticated();
        }

        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session)) return DemoAuthEndpoints.AntiForgeryFailed();

        if (session.User.Role != CaseEndpoints.ReviewerRole)
        {
            EmitScopeFailure(logger, config);
            return Results.Json(new { error = "Role not permitted." }, statusCode: StatusCodes.Status403Forbidden);
        }

        var scope = TryScope(cases, session, caseId);
        if (scope is null)
        {
            EmitScopeFailure(logger, config);
            return Results.NotFound();
        }

        if (body.Decision is not ("accept" or "reject"))
            return Results.BadRequest(new { error = "Decision must be accept or reject." });

        var reason = body.Reason?.Trim() ?? string.Empty;
        if (reason.Length == 0 || reason.Length > MaxReasonLength)
            return Results.BadRequest(new { error = $"A reason of 1 to {MaxReasonLength} characters is required." });

        var state = cases.Read(scope);
        if (state.Basis.Count == 0 || body.BasisVersion != state.Basis.Count)
            return Results.Json(new { error = "Decision basis is stale." }, statusCode: StatusCodes.Status409Conflict);

        var traceId = "trace-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var detail = JsonSerializer.Serialize(new TechnicalDecisionDetail(body.Decision, reason, body.BasisVersion, traceId));

        CommitResult result;
        try
        {
            result = cases.Commit(scope, body.ExpectedRevision, new CaseCommit(null, null, session.User.Id, DecisionAction, detail));
        }
        catch (RevisionConflictException)
        {
            return Results.Json(new { error = "Case revision is stale." }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (CaseAccessDeniedException)
        {
            return Results.NotFound();
        }

        var accepted = body.Decision == "accept";
        var trace = new TelemetryRedaction.InvestigationTraceRecord(
            traceId,
            "basis-v" + body.BasisVersion,
            // The review endpoint runs no model call, so no deployment is reported.
            "not-invoked",
            Array.Empty<string>(),
            Array.Empty<string>(),
            accepted ? "return-ready" : "return-not-ready",
            session.User.Id,
            accepted ? "approved" : "rejected");
        TelemetryRedaction.EmitInvestigationTrace(logger, trace,
            TelemetryRedaction.ServiceVersion, config["Telemetry:Environment"] ?? "demo", ActorKey, "review.completed");

        return Results.Ok(CurrentDecision(result.State));
    }

    private static CaseScope? TryScope(CaseStateStore cases, DemoSession session, string caseId)
    {
        try
        {
            return cases.AuthorizeSessionCase(caseId, session.User.CaseScopes);
        }
        catch (CaseAccessDeniedException)
        {
            return null;
        }
    }

    private static void EmitScopeFailure(ILogger<DemoSessionStore> logger, IConfiguration config) =>
        TelemetryRedaction.EmitSecurityEvent(logger, TelemetryRedaction.SecurityEvents.AuthScopeFailure,
            TelemetryRedaction.ServiceVersion, config["Telemetry:Environment"] ?? "demo");
}
