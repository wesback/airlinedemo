using System.Collections.Concurrent;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Obligations;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;
using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Finance;

/// <summary>Deterministic inputs for one case: finance reports, plus the obligation and its notice drafts.</summary>
public sealed record FinanceInputs(
    UsageReport? Usage,
    RateReport? Rates,
    Obligation? Obligation,
    IReadOnlyList<NoticeDraft> NoticeDrafts);

public sealed class FinanceInputStore
{
    private readonly ConcurrentDictionary<string, FinanceInputs> _inputs = new(StringComparer.Ordinal);

    public void Set(string caseId, FinanceInputs inputs) => _inputs[caseId] = inputs;

    public FinanceInputs? Find(string caseId) => _inputs.TryGetValue(caseId, out var inputs) ? inputs : null;
}

/// <summary>Deterministic demo finance inputs, seeded into the production store for each demo case.</summary>
public static class FinanceDemoData
{
    public static FinanceInputStore CreateStore()
    {
        var store = new FinanceInputStore();
        var inputs = new FinanceInputs(
            new UsageReport("USE-2026-09", "2026-09", [new UsageEntry("FH-A", 100m), new UsageEntry("CYC-B", 4m)]),
            new RateReport("RATE-2026-09", "2026-09", [new RateEntry("FH-A", "2026-09", 0.10m), new RateEntry("CYC-B", "2026-09", 12.345m)]),
            new Obligation(
                "OBL-1",
                2,
                new DateOnly(2026, 10, 10),
                new DateOnly(2026, 10, 14),
                new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero),
                [new LinkedRequirement("REQ-1", RequirementEvidence.Satisfied, 2)],
                new TechnicalReview("reviewer-1", true, 2)),
            []);
        foreach (var caseId in new[] { "case-001", "case-002", "case-003" }) store.Set(caseId, inputs);
        return store;
    }
}

public static class FinanceEndpoints
{
    public const string ReservePeriod = "2026-09";
    public const string FinanceReviewerRole = "finance-reviewer";
    public const string LegalReviewerRole = "legal-reviewer";
    public const string ReserveDecisionAction = "reserve.decided";
    public const string LegalDecisionAction = "obligation.legal-decided";
    public const int MaxReasonLength = 2000;
    private const string ReasonError = "A reason of 1 to 2000 characters is required.";

    public static IEndpointRouteBuilder MapFinanceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cases/{caseId}/reserve", GetReserve);
        app.MapGet("/api/cases/{caseId}/obligations", GetObligations);
        app.MapPost("/api/cases/{caseId}/reserve/decisions", RecordReserveDecision);
        app.MapPost("/api/cases/{caseId}/obligations/{id}/decisions", RecordLegalDecision);
        return app;
    }

    private static IResult GetReserve(string caseId, HttpContext http, DemoSessionStore sessions,
        FinanceInputStore inputs, ICaseStateStore store, SourceModeStore sourceModes)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!session.User.CaseScopes.Contains(caseId)) return Results.NotFound();

        var scope = TechnicalReviewPolicy.ScopeFor(store, session.User.CaseScopes, caseId);
        if (scope is not null && store.Read(scope) is { } state
            && state.Basis.LastOrDefault() is { } basis && ReserveCalculator.IsSourceBasisContent(basis.Content))
            return Results.Ok(ReserveCalculator.CalculateFromPinnedSources($"RSV-{caseId}", 1, ReservePeriod,
                caseId, basis.Content, sourceModes.ModesFor(state)));

        var input = inputs.Find(caseId);
        return Results.Ok(ReserveCalculator.Calculate($"RSV-{caseId}", 1, ReservePeriod, input?.Usage, input?.Rates));
    }

    private static IResult GetObligations(string caseId, HttpContext http, DemoSessionStore sessions, FinanceInputStore inputs, TimeProvider time)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!session.User.CaseScopes.Contains(caseId)) return Results.NotFound();

        var input = inputs.Find(caseId);
        if (input?.Obligation is null) return Results.NotFound();
        return Results.Ok(ObligationEvaluator.Evaluate(input.Obligation, time.GetUtcNow(), input.NoticeDrafts));
    }

    // Simulated decisions: they append one audit event and never issue an invoice or send a notice.
    private static IResult RecordReserveDecision(string caseId, FinanceDecisionCommand body, HttpContext http, DemoSessionStore sessions, ICaseStateStore store)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session)) return DemoAuthEndpoints.AntiForgeryFailed();
        if (session.User.Role != FinanceReviewerRole) return RoleNotPermitted();
        var scope = TechnicalReviewPolicy.ScopeFor(store, session.User.CaseScopes, caseId);
        if (scope is null) return Results.NotFound();
        if (body.Decision is not ("approve" or "return")) return BadRequest("Decision must be approve or return.");
        var reason = NormalizeReason(body.Reason);
        if (reason is null) return BadRequest(ReasonError);

        var committed = TryCommit(store, scope, body.ExpectedRevision, session.User.Id, ReserveDecisionAction, new { decision = body.Decision, reason });
        if (committed is null) return Stale();
        return Results.Ok(new
        {
            caseId,
            decision = body.Decision,
            reason,
            revision = committed.State.Revision,
            auditSequence = committed.State.Audit[^1].Sequence,
            simulated = true,
            invoiceIssued = false,
        });
    }

    private static IResult RecordLegalDecision(string caseId, string id, FinanceDecisionCommand body, HttpContext http, DemoSessionStore sessions, ICaseStateStore store, FinanceInputStore inputs, TimeProvider time)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session)) return DemoAuthEndpoints.AntiForgeryFailed();
        if (session.User.Role != LegalReviewerRole) return RoleNotPermitted();
        var scope = TechnicalReviewPolicy.ScopeFor(store, session.User.CaseScopes, caseId);
        if (scope is null) return Results.NotFound();
        if (body.Decision is not ("signed-off" or "rejected")) return BadRequest("Decision must be signed-off or rejected.");
        var reason = NormalizeReason(body.Reason);
        if (reason is null) return BadRequest(ReasonError);

        var input = inputs.Find(caseId);
        var draft = input?.NoticeDrafts.FirstOrDefault(d => d.ObligationId == id && d.Status == NoticeDraftStatus.Current);
        if (input?.Obligation?.Id != id || draft is null) return Results.NotFound();

        var record = ObligationEvaluator.RecordLegalDecision(draft, session.User.Id, body.Decision, reason, time.GetUtcNow());
        var committed = TryCommit(store, scope, body.ExpectedRevision, session.User.Id, LegalDecisionAction, record);
        if (committed is null) return Stale();
        return Results.Ok(new
        {
            caseId,
            obligationId = id,
            noticeDraftId = draft.Id,
            decision = body.Decision,
            reason,
            revision = committed.State.Revision,
            auditSequence = committed.State.Audit[^1].Sequence,
            simulated = true,
            noticeSent = false,
        });
    }

    /// <summary>Returns null when the expected revision is stale; nothing is committed in that case.</summary>
    private static CommitResult? TryCommit(ICaseStateStore store, CaseScope scope, long expectedRevision, string actorId, string action, object detail)
    {
        try
        {
            return store.Commit(scope, expectedRevision, new CaseCommit(null, null, actorId, action, JsonSerializer.Serialize(detail)));
        }
        catch (RevisionConflictException)
        {
            return null;
        }
    }

    private static string? NormalizeReason(string? reason) =>
        reason is null || reason.Length > MaxReasonLength || reason.Trim().Length == 0 ? null : reason.Trim();

    private static IResult RoleNotPermitted() => Results.Json(new { error = "Role not permitted." }, statusCode: StatusCodes.Status403Forbidden);

    private static IResult BadRequest(string error) => Results.BadRequest(new { error });

    private static IResult Stale() => Results.Json(new { error = "Case revision is stale." }, statusCode: StatusCodes.Status409Conflict);
}

public sealed record FinanceDecisionCommand(long ExpectedRevision, string? Decision, string? Reason);
