using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Obligations;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;

namespace AirlineDemo.Api.Finance;

public sealed record FinanceDecisionCommand(long ExpectedRevision, string? Decision, string? Reason, string? DraftId = null);

/// <summary>
/// HTTP surface for the synthetic reserve draft and obligation notice drafts, and for the simulated finance and legal
/// decisions on them. Nothing here issues an invoice or sends a notice. Decisions are append-only audit events on the case,
/// and notice drafts are persisted as audit events so a decision can be checked against the current draft.
/// </summary>
public static class FinanceEndpoints
{
    public const string FinanceReviewerRole = "finance-reviewer";
    public const string LegalReviewerRole = "legal-reviewer";
    public const string ReserveDecisionAction = "reserve.decided";
    public const string ObligationDecisionAction = "obligation.decided";
    public const string NoticeDraftAction = "obligation.notice-drafted";
    public const string ObligationId = "obl-001";
    public const string Period = "2026-09";
    public const int MaxReasonLength = 2000;

    private static readonly DateOnly WindowStart = new(2026, 1, 1);
    private static readonly DateOnly WindowEnd = new(2030, 12, 31);
    private static readonly DateTimeOffset Due = new(2030, 12, 31, 0, 0, 0, TimeSpan.Zero);

    public static IEndpointRouteBuilder MapFinanceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cases/{caseId}/reserve", GetReserve);
        app.MapPost("/api/cases/{caseId}/reserve/decisions", DecideReserve);
        app.MapGet("/api/cases/{caseId}/obligations", GetObligations);
        app.MapPost("/api/cases/{caseId}/obligations/{id}/decisions", DecideObligation);
        return app;
    }

    private static IResult GetReserve(string caseId, HttpContext http, DemoSessionStore sessions, ICaseStateStore store)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();

        var scope = TechnicalReviewPolicy.ScopeFor(store, session.User.CaseScopes, caseId);
        if (scope is null) return Results.NotFound();

        var state = store.Read(scope);
        var draft = SyntheticReserve();
        return Results.Ok(new
        {
            caseId,
            revision = state.Revision,
            draftId = draft.DraftId,
            version = draft.Version,
            period = draft.Period,
            status = draft.Status.ToString(),
            usageReportId = draft.UsageReportId,
            rateReportId = draft.RateReportId,
            lines = draft.Lines,
            payableTotal = draft.PayableTotal,
            blockers = draft.Blockers,
            discrepancies = draft.Discrepancies,
            calculationBasis = draft.CalculationBasis,
        });
    }

    private static IResult GetObligations(string caseId, HttpContext http, DemoSessionStore sessions, ICaseStateStore store, TimeProvider time)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();

        var scope = TechnicalReviewPolicy.ScopeFor(store, session.User.CaseScopes, caseId);
        if (scope is null) return Results.NotFound();

        var state = store.Read(scope);
        var now = time.GetUtcNow();
        var evaluation = Evaluate(state, now);
        if (evaluation.NewDraft is { } newDraft)
        {
            try
            {
                state = store.Commit(scope, state.Revision,
                    new CaseCommit(null, null, session.User.Id, NoticeDraftAction, JsonSerializer.Serialize(newDraft))).State;
            }
            catch (RevisionConflictException)
            {
                return Results.Json(new { error = "Case revision is stale." }, statusCode: StatusCodes.Status409Conflict);
            }
            evaluation = Evaluate(state, now);
        }

        return Results.Ok(new
        {
            caseId,
            revision = state.Revision,
            obligationId = ObligationId,
            state = evaluation.State.ToString(),
            reasons = evaluation.Reasons,
            drafts = evaluation.Drafts.Select(d => new
            {
                d.Id,
                d.ObligationId,
                d.BasisVersion,
                d.EvidenceRequirementIds,
                d.CreatedUtc,
                status = d.Status.ToString(),
                d.Body,
            }),
        });
    }

    private static IResult DecideReserve(string caseId, FinanceDecisionCommand body, HttpContext http, DemoSessionStore sessions, ICaseStateStore store, TimeProvider time)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session)) return DemoAuthEndpoints.AntiForgeryFailed();
        if (session.User.Role != FinanceReviewerRole) return RoleNotPermitted();

        var scope = TechnicalReviewPolicy.ScopeFor(store, session.User.CaseScopes, caseId);
        if (scope is null) return Results.NotFound();

        var kind = body.Decision switch
        {
            "approve" => FinanceDecisionKind.Approve,
            "return" => FinanceDecisionKind.Return,
            _ => (FinanceDecisionKind?)null,
        };
        if (kind is null) return BadRequest("Decision must be approve or return.");
        var reason = NormalizeReason(body.Reason);
        if (reason is null) return BadRequest("A reason is required and must be at most 2000 characters.");

        FinanceReviewRecord record;
        try
        {
            record = ReserveCalculator.Review(SyntheticReserve(), kind.Value, http, reason, time.GetUtcNow());
        }
        catch (UnauthorizedAccessException)
        {
            return RoleNotPermitted();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
        }

        try
        {
            var committed = store.Commit(scope, body.ExpectedRevision,
                new CaseCommit(null, null, session.User.Id, ReserveDecisionAction, JsonSerializer.Serialize(record)));
            return Results.Ok(new
            {
                caseId,
                simulated = record.Simulated,
                invoiceIssued = record.InvoiceIssued,
                decision = record,
                revision = committed.State.Revision,
            });
        }
        catch (RevisionConflictException)
        {
            return Stale();
        }
    }

    private static IResult DecideObligation(string caseId, string id, FinanceDecisionCommand body, HttpContext http, DemoSessionStore sessions, ICaseStateStore store, TimeProvider time)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session)) return DemoAuthEndpoints.AntiForgeryFailed();
        if (session.User.Role != LegalReviewerRole) return RoleNotPermitted();

        var scope = TechnicalReviewPolicy.ScopeFor(store, session.User.CaseScopes, caseId);
        if (scope is null || id != ObligationId) return Results.NotFound();

        if (body.Decision is not ("signed-off" or "rejected")) return BadRequest("Decision must be signed-off or rejected.");
        var reason = NormalizeReason(body.Reason);
        if (reason is null) return BadRequest("A reason is required and must be at most 2000 characters.");

        var state = store.Read(scope);
        var now = time.GetUtcNow();
        var evaluation = Evaluate(state, now);
        // A draft that the evaluator would create is not yet persisted, so it cannot be decided until a GET commits it.
        if (evaluation.NewDraft is not null || evaluation.Drafts.LastOrDefault(d => d.Status == NoticeDraftStatus.Current) is not { } current)
        {
            return Results.Json(new { error = "No current notice draft exists for this obligation." }, statusCode: StatusCodes.Status409Conflict);
        }
        if (body.DraftId is not null && body.DraftId != current.Id)
        {
            return Results.Json(new { error = "Notice draft is superseded or not current." }, statusCode: StatusCodes.Status409Conflict);
        }

        var record = ObligationEvaluator.RecordLegalDecision(current, session.User.Id, body.Decision, reason, now);
        try
        {
            var committed = store.Commit(scope, body.ExpectedRevision,
                new CaseCommit(null, null, session.User.Id, ObligationDecisionAction, JsonSerializer.Serialize(record)));
            return Results.Ok(new
            {
                caseId,
                obligationId = id,
                simulated = record.Simulated,
                noticeSent = false,
                decision = record,
                revision = committed.State.Revision,
            });
        }
        catch (RevisionConflictException)
        {
            return Stale();
        }
    }

    private static NoticeDraftEvaluation Evaluate(CaseState state, DateTimeOffset now)
    {
        var history = state.Audit
            .Where(a => a.Action == NoticeDraftAction)
            .Select(a => JsonSerializer.Deserialize<NoticeDraft>(a.Detail)!)
            .ToList();
        var evaluation = ObligationEvaluator.Evaluate(SyntheticObligation(state.Basis.Count), now, history);
        return new NoticeDraftEvaluation(evaluation.State, evaluation.Reasons, evaluation.Drafts, evaluation.NewDraft);
    }

    private static Obligation SyntheticObligation(int basisVersion) => new(
        ObligationId,
        basisVersion,
        WindowStart,
        WindowEnd,
        Due,
        [new LinkedRequirement("REQ-NOTICE-1", RequirementEvidence.Missing, null)],
        null);

    private static ReserveDraft SyntheticReserve()
    {
        var usage = new UsageReport("USE-2026-09", Period, [new UsageEntry("FH-A", 120m), new UsageEntry("FH-B", 3m)]);
        var rates = new RateReport("RATE-2026-09", Period, [new RateEntry("FH-A", Period, 12.25m), new RateEntry("FH-B", Period, 10m)]);
        return ReserveCalculator.Calculate("reserve-2026-09", 1, Period, usage, rates);
    }

    private static string? NormalizeReason(string? reason)
    {
        var trimmed = reason?.Trim() ?? string.Empty;
        return trimmed.Length is > 0 and <= MaxReasonLength ? trimmed : null;
    }

    private static IResult RoleNotPermitted() =>
        Results.Json(new { error = "Role not permitted." }, statusCode: StatusCodes.Status403Forbidden);

    private static IResult BadRequest(string error) => Results.BadRequest(new { error });

    private static IResult Stale() =>
        Results.Json(new { error = "Case revision is stale; no change was made." }, statusCode: StatusCodes.Status409Conflict);

    private sealed record NoticeDraftEvaluation(
        ObligationState State,
        IReadOnlyList<string> Reasons,
        IReadOnlyList<NoticeDraft> Drafts,
        NoticeDraft? NewDraft);
}
