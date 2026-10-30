using System.Globalization;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;

namespace AirlineDemo.Api.Decisions;

public static class ComplianceEvidenceEndpoints
{
    public static IEndpointRouteBuilder MapComplianceEvidenceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v{version:int}/cases/{caseId}/compliance-evidence", Get);
        app.MapMethods("/api/v{version:int}/cases/{caseId}/compliance-evidence", ["POST", "PUT", "PATCH", "DELETE"],
            () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        return app;
    }

    private static IResult Get(int version, string caseId, HttpContext http, DemoSessionStore sessions, ICaseStateStore store)
    {
        if (version != 1) return Error(400, "unsupported_contract_version");
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return Error(401, "unauthenticated");
        if (session.User.Role is not ("presenter" or "technical-reviewer" or "finance-reviewer" or "legal-reviewer"))
            return Error(404, "not_found");
        var scope = TechnicalReviewPolicy.ScopeFor(store, session.User.CaseScopes, caseId);
        if (scope is null) return Error(404, "not_found");
        long? decisionId = null;
        if (http.Request.Query.Keys.Any(key => key != "decisionId")) return Error(400, "invalid_request");
        if (http.Request.Query.TryGetValue("decisionId", out var value))
        {
            if (value.Count != 1 || !long.TryParse(value[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1)
                return Error(400, "invalid_request");
            decisionId = id;
        }
        var snapshot = store.Read(scope);
        if (snapshot.CaseId != caseId) return Error(422, "evidence_incomplete");
        if (decisionId.HasValue && !snapshot.Audit.Any(a => a.Sequence == decisionId && a.Action == TechnicalReviewPolicy.DecisionAction))
            return Error(404, "not_found");
        try { return Results.Ok(ComplianceEvidenceService.Build(snapshot, session.User.Role, decisionId)); }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException
            or ArgumentException or IndexOutOfRangeException or FormatException or KeyNotFoundException)
        {
            return Error(422, "evidence_incomplete");
        }
    }

    private static IResult Error(int status, string code) =>
        Results.Json(new { contractVersion = 1, error = new { code } }, statusCode: status);
}
