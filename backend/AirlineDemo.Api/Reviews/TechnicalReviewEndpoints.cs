using System.Security.Cryptography;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Diagnostics;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Reviews;

public sealed record TechnicalReviewCommand(long ExpectedRevision, int ExpectedBasisVersion, string? Decision, string? Reason);

/// <summary>A decision reconstructed from business audit. <see cref="Current"/> is false once a later basis exists.</summary>
public sealed record TechnicalReviewDecision(
    long Sequence,
    long Revision,
    int BasisVersion,
    string BasisDigest,
    string Actor,
    string Decision,
    string Outcome,
    string Reason,
    string TraceId,
    DateTimeOffset At,
    bool Current);

public enum TechnicalReviewOutcome { Recorded, Forbidden, NotFound, Stale, Invalid }

public sealed record TechnicalReviewResult(TechnicalReviewOutcome Outcome, string? Error = null, TechnicalReviewDecision? Decision = null);

internal sealed record DecisionDetail(string Decision, string Reason, int BasisVersion, string BasisDigest, string TraceId,
    string Outcome, string BasisId, string PolicyVersion, string Role, string AuditEventId);

/// <summary>
/// Authorization and basis binding for technical-review decisions. Decisions are stored only as
/// append-only audit events on the case, so a later basis never rewrites a prior decision.
/// </summary>
public static class TechnicalReviewPolicy
{
    public const string DecisionAction = "technical-review.decided";
    public const string Version = "technical-review-v1";
    public const int MaxReasonLength = 2000;

    public static TechnicalReviewResult Decide(ICaseStateStore store, IEnumerable<string> caseScopes, string role, string actorId,
        string caseId, TechnicalReviewCommand command, string traceId)
    {
        if (role != CaseEndpoints.ReviewerRole) return new(TechnicalReviewOutcome.Forbidden, "Role not permitted.");

        var scope = ScopeFor(store, caseScopes, caseId);
        if (scope is null) return new(TechnicalReviewOutcome.NotFound);

        if (command.Decision is not ("accept" or "reject"))
            return new(TechnicalReviewOutcome.Invalid, "Decision must be accept or reject.");

        var reason = command.Reason?.Trim() ?? string.Empty;
        if (reason.Length == 0) return new(TechnicalReviewOutcome.Invalid, "A reason is required.");
        if (reason.Length > MaxReasonLength) return new(TechnicalReviewOutcome.Invalid, "Reason is too long.");

        var state = store.Read(scope);
        if (state.Basis.Count == 0) return new(TechnicalReviewOutcome.Invalid, "Case has no evidence basis to review.");

        var basis = state.Basis[^1];
        if (command.ExpectedBasisVersion != basis.Version) return new(TechnicalReviewOutcome.Stale);

        var detail = new DecisionDetail(command.Decision, reason, basis.Version, basis.BasisDigest, traceId,
            command.Decision == "accept" ? "approved" : "rejected", "basis-v" + basis.Version, Version,
            role, "evt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
        CommitResult committed;
        try
        {
            committed = store.Commit(scope, command.ExpectedRevision,
                new CaseCommit(null, null, actorId, DecisionAction, JsonSerializer.Serialize(detail)));
        }
        catch (RevisionConflictException)
        {
            return new(TechnicalReviewOutcome.Stale);
        }

        // The state returned by this commit ends with the decision's own audit event, even if other activity follows.
        return new(TechnicalReviewOutcome.Recorded, Decision: ToDecision(committed.State.Audit[^1], detail, committed.State));
    }

    /// <summary>Every recorded decision for the case, marked current only when it was made against the latest basis.</summary>
    public static IReadOnlyList<TechnicalReviewDecision> Decisions(CaseState state) =>
        state.Audit.Where(a => a.Action == DecisionAction)
            .Select(a => ToDecision(a, JsonSerializer.Deserialize<DecisionDetail>(a.Detail)!, state))
            .ToList();

    /// <summary>
    /// True only when the latest current-basis decision is an acceptance. A later rejection on the same basis
    /// revokes an earlier acceptance; decisions on superseded bases never authorize.
    /// </summary>
    public static bool AuthorizesRequest(CaseState state) =>
        Decisions(state).LastOrDefault(d => d.Current) is { Decision: "accept" };

    public static CaseScope? ScopeFor(ICaseStateStore store, IEnumerable<string> caseScopes, string caseId)
    {
        if (!caseScopes.Contains(caseId)) return null;
        var owner = store.OwnerOf(caseId);
        return owner is null ? null : store.AuthorizeCase(caseId, owner);
    }

    private static TechnicalReviewDecision ToDecision(AuditEvent audit, DecisionDetail detail, CaseState state)
    {
        var currentVersion = state.Basis.Count;
        var currentDigest = currentVersion == 0 ? null : state.Basis[^1].BasisDigest;
        return new TechnicalReviewDecision(audit.Sequence, audit.Revision, detail.BasisVersion, detail.BasisDigest,
            audit.Actor, detail.Decision, detail.Outcome, detail.Reason, detail.TraceId, audit.At,
            Current: detail.BasisVersion == currentVersion && detail.BasisDigest == currentDigest);
    }
}

public static class TechnicalReviewEndpoints
{
    public static IEndpointRouteBuilder MapTechnicalReviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cases/{caseId}/technical-reviews", ListDecisions);
        app.MapPost("/api/cases/{caseId}/technical-reviews", RecordDecision);
        return app;
    }

    private static IResult ListDecisions(string caseId, HttpContext http, DemoSessionStore sessions, ICaseStateStore store, IConfiguration config, ILogger<DemoSessionStore> logger)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();

        var scope = TechnicalReviewPolicy.ScopeFor(store, session.User.CaseScopes, caseId);
        if (scope is null)
        {
            EmitScopeFailure(logger, config);
            return Results.NotFound();
        }

        var state = store.Read(scope);
        return Results.Ok(new
        {
            caseId,
            basisVersion = state.Basis.Count,
            authorizesRequest = TechnicalReviewPolicy.AuthorizesRequest(state),
            decisions = TechnicalReviewPolicy.Decisions(state),
        });
    }

    static IResult RecordDecision(string caseId, TechnicalReviewCommand body, HttpContext http, DemoSessionStore sessions, ICaseStateStore store, IConfiguration config, ILogger<DemoSessionStore> logger)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null)
        {
            EmitScopeFailure(logger, config);
            return DemoAuthEndpoints.Unauthenticated();
        }
        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session)) return DemoAuthEndpoints.AntiForgeryFailed();

        var traceId = "trace-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var result = TechnicalReviewPolicy.Decide(store, session.User.CaseScopes, session.User.Role, session.User.Id, caseId, body, traceId);

        switch (result.Outcome)
        {
            case TechnicalReviewOutcome.Recorded:
                EmitTrace(logger, config, result.Decision!, session.User.Id);
                return Results.Ok(result.Decision);
            case TechnicalReviewOutcome.Forbidden:
                EmitScopeFailure(logger, config);
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status403Forbidden);
            case TechnicalReviewOutcome.NotFound:
                EmitScopeFailure(logger, config);
                return Results.NotFound();
            case TechnicalReviewOutcome.Stale:
                return Results.Json(new { error = "Case revision or evidence basis is stale." }, statusCode: StatusCodes.Status409Conflict);
            default:
                return Results.BadRequest(new { error = result.Error });
        }
    }

    private static void EmitTrace(ILogger logger, IConfiguration config, TechnicalReviewDecision decision, string actorId)
    {
        var accepted = decision.Decision == "accept";
        var trace = new TelemetryRedaction.InvestigationTraceRecord(
            decision.TraceId,
            "basis-v" + decision.BasisVersion,
            // The decision endpoint runs no model call, so no deployment is reported.
            "not-invoked",
            Array.Empty<string>(),
            Array.Empty<string>(),
            accepted ? "return-ready" : "return-not-ready",
            actorId,
            decision.Outcome);
        TelemetryRedaction.EmitInvestigationTrace(logger, trace,
            TelemetryRedaction.ServiceVersion, config["Telemetry:Environment"] ?? "demo", CaseEndpoints.ActorKey, "review.decided");
    }

    private static void EmitScopeFailure(ILogger logger, IConfiguration config) =>
        TelemetryRedaction.EmitSecurityEvent(logger, TelemetryRedaction.SecurityEvents.AuthScopeFailure,
            TelemetryRedaction.ServiceVersion, config["Telemetry:Environment"] ?? "demo");
}
