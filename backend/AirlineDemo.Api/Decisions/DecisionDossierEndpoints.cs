using System.Globalization;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;

namespace AirlineDemo.Api.Decisions;

public static class DecisionDossierEndpoints
{
    public static IEndpointRouteBuilder MapDecisionDossierEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v{version:int}/cases/{caseId}/decisions/{decisionId}/dossier", Get)
            .WithName("GetDecisionDossier").WithSummary("Export one recorded technical-review decision dossier");
        return app;
    }

    private static IResult Get(int version, string caseId, string decisionId, HttpContext http,
        DemoSessionStore sessions, ICaseStateStore cases, IDecisionDossierService dossiers)
    {
        if (version != 1) return Error(StatusCodes.Status400BadRequest, "unsupported_contract_version");
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return Error(StatusCodes.Status401Unauthorized, "unauthenticated");
        if (session.User.Role != CaseEndpoints.ReviewerRole) return NotFound();
        if (!long.TryParse(decisionId, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
            return NotFound();

        var scope = TechnicalReviewPolicy.ScopeFor(cases, session.User.CaseScopes, caseId);
        if (scope is null) return NotFound();
        var result = dossiers.Export(scope, caseId, sequence, session.User.Role);
        return result.Kind switch
        {
            DossierResultKind.Success => Results.Ok(result.Dossier),
            DossierResultKind.Incomplete => Error(StatusCodes.Status422UnprocessableEntity, "dossier_incomplete", "Dossier is incomplete."),
            _ => NotFound(),
        };
    }

    private static IResult NotFound() =>
        Error(StatusCodes.Status404NotFound, "not_found", "Not found.");

    private static IResult Error(int status, string code, string? message = null) =>
        message is null
            ? Results.Json(new { contractVersion = 1, error = new { code } }, statusCode: status)
            : Results.Json(new { contractVersion = 1, error = new { code, message } }, statusCode: status);
}
