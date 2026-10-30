using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Finance;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;

namespace AirlineDemo.Api.Decisions;

public sealed record ReleaseCommand(long ExpectedRevision, string? Note);

/// <summary>
/// The final, human release gate: the transition lead (the demo presenter) releases an aircraft to its next lessee once
/// the technical, finance and legal gates are passed. The release is simulated: it appends one audit event and never
/// contacts a lessee or an external system.
/// </summary>
public static class ReleaseEndpoints
{
    public const string ReleaseAction = "aircraft.released";
    public const string ReleaseRole = "presenter";
    public const int MaxNoteLength = 2000;

    public static IEndpointRouteBuilder MapReleaseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/cases/{caseId}/release", Release);
        return app;
    }

    /// <summary>The gates that still block release; empty when the aircraft may be released.</summary>
    public static IReadOnlyList<string> Blockers(CaseState state)
    {
        var blockers = new List<string>();
        if (!TechnicalReviewPolicy.AuthorizesRequest(state)) blockers.Add("technical_review_not_accepted");
        if (LatestDecision(state, FinanceEndpoints.ReserveDecisionAction) != "approve") blockers.Add("reserve_not_approved");
        if (LatestDecision(state, FinanceEndpoints.LegalDecisionAction) != "signed-off") blockers.Add("obligations_not_signed_off");
        return blockers;
    }

    public static bool IsReleased(CaseState state) => state.Audit.Any(item => item.Action == ReleaseAction);

    private static IResult Release(string caseId, ReleaseCommand body, HttpContext http, DemoSessionStore sessions, ICaseStateStore store)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session)) return DemoAuthEndpoints.AntiForgeryFailed();
        if (session.User.Role != ReleaseRole) return Results.Json(new { error = "Role not permitted." }, statusCode: StatusCodes.Status403Forbidden);
        var scope = TechnicalReviewPolicy.ScopeFor(store, session.User.CaseScopes, caseId);
        if (scope is null) return Results.NotFound();
        var note = body.Note is null || body.Note.Length > MaxNoteLength || body.Note.Trim().Length == 0 ? null : body.Note.Trim();
        if (note is null) return Results.BadRequest(new { error = "A release note of 1 to 2000 characters is required." });

        var state = store.Read(scope);
        if (state.Revision != body.ExpectedRevision) return Stale();
        if (IsReleased(state)) return Results.Json(new { error = "Aircraft is already released." }, statusCode: StatusCodes.Status409Conflict);
        var blockers = Blockers(state);
        if (blockers.Count > 0)
            return Results.Json(new { error = "Release is blocked until every gate is passed.", blockers }, statusCode: StatusCodes.Status409Conflict);

        CommitResult committed;
        try
        {
            // Bound to the revision the gates were checked on, so no decision can change in between.
            committed = store.Commit(scope, state.Revision, new CaseCommit(null, null, session.User.Id, ReleaseAction,
                JsonSerializer.Serialize(new { decision = "released", note })));
        }
        catch (RevisionConflictException)
        {
            return Stale();
        }

        return Results.Ok(new
        {
            caseId,
            decision = "released",
            note,
            revision = committed.State.Revision,
            auditSequence = committed.State.Audit[^1].Sequence,
            simulated = true,
        });
    }

    private static string? LatestDecision(CaseState state, string action)
    {
        var audit = state.Audit.LastOrDefault(item => item.Action == action);
        if (audit is null) return null;
        try
        {
            using var json = JsonDocument.Parse(audit.Detail);
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && (root.TryGetProperty("decision", out var value) || root.TryGetProperty("Decision", out value))
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IResult Stale() => Results.Json(new { error = "Case revision is stale." }, statusCode: StatusCodes.Status409Conflict);
}
