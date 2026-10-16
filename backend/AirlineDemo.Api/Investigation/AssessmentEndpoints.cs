using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Requests;
using AirlineDemo.Api.Reviews;

namespace AirlineDemo.Api.Investigation;

public sealed record AssessmentSubmission(IReadOnlyList<AssessmentClaimSubmission?>? Claims);

public sealed record AssessmentClaimSubmission(
    string? Kind,
    string? Value,
    string? Component,
    string? SerialNumber,
    IReadOnlyList<AssessmentWitnessSubmission?>? Witnesses);

public sealed record AssessmentWitnessSubmission(string? DocumentId, int Page, string? Quote);

public static class AssessmentEndpoints
{
    public static IEndpointRouteBuilder MapAssessmentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/cases/{caseId}/assessments", CreateAssessment);
        app.MapGet("/api/cases/{caseId}/assessments/{id}", GetAssessment);
        app.MapGet("/api/cases/{caseId}/requests", ListRequests);
        return app;
    }

    private static IResult CreateAssessment(
        string caseId,
        AssessmentSubmission? body,
        HttpContext http,
        DemoSessionStore sessions,
        ICaseStateStore cases,
        AssessmentStore assessments,
        AssessmentExecutionService execution)
    {
        var (denial, access) = Authorize(caseId, http, sessions, cases);
        if (denial is not null) return denial;

        if (body?.Claims is null || body.Claims.Any(claim => claim is null))
            return Results.BadRequest(new { error = "Assessment claims are required." });

        var claims = new List<AssessmentClaim>(body.Claims.Count);
        foreach (var submitted in body.Claims)
        {
            var kindName = submitted!.Kind;
            if (string.IsNullOrWhiteSpace(kindName) ||
                !Enum.GetNames<ClaimKind>().Contains(kindName, StringComparer.Ordinal) ||
                string.IsNullOrWhiteSpace(submitted.Value) ||
                string.IsNullOrWhiteSpace(submitted.Component) ||
                string.IsNullOrWhiteSpace(submitted.SerialNumber))
            {
                return Results.BadRequest(new { error = "Assessment claim fields are invalid." });
            }

            var kind = Enum.Parse<ClaimKind>(kindName, ignoreCase: false);
            var witnesses = new List<DocumentWitness>();
            if (submitted.Witnesses is not null)
            {
                foreach (var witness in submitted.Witnesses)
                {
                    if (witness is null) return Results.BadRequest(new { error = "Assessment witnesses are invalid." });
                    witnesses.Add(new DocumentWitness(witness.DocumentId ?? string.Empty, witness.Page, witness.Quote ?? string.Empty));
                }
            }

            claims.Add(new AssessmentClaim(kind, submitted.Value, submitted.Component, submitted.SerialNumber, witnesses));
        }

        var basis = assessments.CurrentBasis(caseId);
        if (basis is null) return Results.NotFound();

        var record = execution.Assess(access!.Scope, basis, claims);
        return Results.Created($"/api/cases/{caseId}/assessments/{record.AssessmentId}", View(record.AssessmentId, record));
    }

    private static IResult GetAssessment(
        string caseId,
        string id,
        HttpContext http,
        DemoSessionStore sessions,
        ICaseStateStore cases)
    {
        var (denial, access) = Authorize(caseId, http, sessions, cases);
        if (denial is not null) return denial;

        var record = cases.Read(access!.Scope).Assessments?.FirstOrDefault(a => a.AssessmentId == id);
        return record is null ? Results.NotFound() : Results.Ok(View(id, record));
    }

    private static IResult ListRequests(
        string caseId,
        HttpContext http,
        DemoSessionStore sessions,
        ICaseStateStore cases)
    {
        var (denial, access) = Authorize(caseId, http, sessions, cases);
        if (denial is not null) return denial;

        var receipts = (cases.Read(access!.Scope).RequestReceipts ?? [])
            .Select(receipt => new
            {
                receipt.RequestId,
                receipt.CaseId,
                receipt.ReportId,
                receipt.BasisId,
                receipt.BasisVersion,
                receipt.TemplateVersion,
                receipt.Body,
                state = "delivered",
            })
            .ToArray();
        return Results.Ok(receipts);
    }

    private static (IResult? Denial, AuthorizedCase? Access) Authorize(
        string caseId,
        HttpContext http,
        DemoSessionStore sessions,
        ICaseStateStore cases)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return (DemoAuthEndpoints.Unauthenticated(), null);
        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session)) return (DemoAuthEndpoints.AntiForgeryFailed(), null);
        if (session.User.Role is not (EvidenceEndpoints.PresenterRole or CaseEndpoints.ReviewerRole))
            return (Results.Json(new { error = "Role not permitted." }, statusCode: StatusCodes.Status403Forbidden), null);

        var scope = TechnicalReviewPolicy.ScopeFor(cases, session.User.CaseScopes, caseId);
        return scope is null
            ? (Results.NotFound(), null)
            : (null, new AuthorizedCase(session, scope));
    }

    private sealed record AuthorizedCase(DemoSession Session, CaseScope Scope);

    private static object View(string id, AssessmentRecord record) => new
    {
        id,
        record.CaseId,
        record.BasisId,
        record.BasisVersion,
        record.Outcome,
        record.Reason,
        record.Claims,
        record.RequestId,
        record.WorkItemId,
    };
}
