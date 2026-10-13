using System.Diagnostics;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Reviews;

public sealed record TechnicalReviewCommand(
    long ExpectedRevision,
    string? Decision,
    string? Reason,
    int? BasisVersion,
    string? BasisDigest);

/// <summary>A business decision as recorded in the audit. <see cref="Current"/> is false once a later basis exists.</summary>
public sealed record TechnicalReviewDecision(
    long Sequence,
    string ReviewerId,
    string Outcome,
    string Reason,
    int BasisVersion,
    string BasisDigest,
    DateTimeOffset At,
    string? TraceId,
    bool Current);

public sealed record TechnicalReviewView(
    string CaseId,
    long Revision,
    int? CurrentBasisVersion,
    string? CurrentBasisDigest,
    string Disposition,
    bool AuthorizesAcceptance,
    IReadOnlyList<TechnicalReviewDecision> Decisions);

/// <summary>Stored as the audit event detail. Holds decision metadata only, never document or prompt content.</summary>
internal sealed record DecisionDetail(string Outcome, string Reason, int BasisVersion, string BasisDigest, string? TraceId);

public static class TechnicalReviewEndpoints
{
    public const string DecisionAction = "technical-review-decision";
    public const int MaxReasonLength = 500;

    public static IEndpointRouteBuilder MapTechnicalReviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cases/{caseId}/technical-review", GetReview);
        app.MapPost("/api/cases/{caseId}/technical-review/decisions", RecordDecision);
        return app;
    }

    private static IResult GetReview(string caseId, HttpContext http, DemoSessionStore sessions, CaseStateStore cases)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!session.User.CaseScopes.Contains(caseId)) return Results.NotFound();

        try
        {
            var scope = cases.AuthorizeExistingCase(caseId);
            return Results.Ok(Project(cases.Read(scope)));
        }
        catch (CaseAccessDeniedException)
        {
            return Results.NotFound();
        }
    }

    private static IResult RecordDecision(string caseId, TechnicalReviewCommand body, HttpContext http, DemoSessionStore sessions, CaseStateStore cases)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session)) return DemoAuthEndpoints.AntiForgeryFailed();
        if (session.User.Role != CaseEndpoints.ReviewerRole) return Problem(StatusCodes.Status403Forbidden, "Role not permitted.");
        if (!session.User.CaseScopes.Contains(caseId)) return Results.NotFound();
        if (body.Decision is not ("accept" or "reject")) return Problem(StatusCodes.Status400BadRequest, "Decision must be accept or reject.");

        var reason = body.Reason?.Trim();
        if (string.IsNullOrEmpty(reason)) return Problem(StatusCodes.Status400BadRequest, "A reason is required.");
        if (reason.Length > MaxReasonLength) return Problem(StatusCodes.Status400BadRequest, $"Reason must be {MaxReasonLength} characters or fewer.");

        try
        {
            var scope = cases.AuthorizeExistingCase(caseId);
            var latest = cases.Read(scope).Basis.LastOrDefault();
            if (latest is null || latest.Version != body.BasisVersion || latest.BasisDigest != body.BasisDigest)
                return Problem(StatusCodes.Status409Conflict, "Evidence basis is not current; no decision was recorded.");

            // Commit re-checks the revision under the file lock, so a basis change after this read is rejected there.
            var detail = JsonSerializer.Serialize(new DecisionDetail(
                body.Decision,
                reason,
                latest.Version,
                latest.BasisDigest,
                Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier));
            var result = cases.Commit(scope, body.ExpectedRevision, new CaseCommit(null, null, session.User.Id, DecisionAction, detail));
            return Results.Ok(Project(result.State));
        }
        catch (CaseAccessDeniedException)
        {
            return Results.NotFound();
        }
        catch (RevisionConflictException)
        {
            return Problem(StatusCodes.Status409Conflict, "Case revision is stale; no decision was recorded.");
        }
    }

    private static TechnicalReviewView Project(CaseState state)
    {
        var latest = state.Basis.Count == 0 ? null : state.Basis[^1];
        var decisions = state.Audit
            .Where(e => e.Action == DecisionAction)
            .Select(e =>
            {
                var d = JsonSerializer.Deserialize<DecisionDetail>(e.Detail)!;
                var current = latest is not null && d.BasisVersion == latest.Version && d.BasisDigest == latest.BasisDigest;
                return new TechnicalReviewDecision(e.Sequence, e.Actor, d.Outcome, d.Reason, d.BasisVersion, d.BasisDigest, e.At, d.TraceId, current);
            })
            .ToList();

        var last = decisions.LastOrDefault();
        var currentDecision = last is { Current: true } ? last : null;
        var disposition = currentDecision?.Outcome switch
        {
            "accept" => "accepted",
            "reject" => "rejected",
            _ => "awaiting-review",
        };

        return new TechnicalReviewView(
            state.CaseId,
            state.Revision,
            latest?.Version,
            latest?.BasisDigest,
            disposition,
            currentDecision?.Outcome == "accept",
            decisions);
    }

    private static IResult Problem(int status, string message) => Results.Json(new { error = message }, statusCode: status);
}
