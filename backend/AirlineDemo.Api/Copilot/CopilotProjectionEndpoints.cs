using AirlineDemo.Api.Auth;

namespace AirlineDemo.Api.Copilot;

public static class CopilotProjectionEndpoints
{
    public static IEndpointRouteBuilder MapCopilotProjectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/cases/{caseId}/copilot/summary", (string caseId, HttpContext http, DemoSessionStore sessions, CopilotProjections projections) =>
            ToResult(projections.Summary(DemoAuthEndpoints.CurrentSession(http, sessions), caseId)));
        app.MapGet("/api/v1/cases/{caseId}/copilot/open-points", (string caseId, HttpContext http, DemoSessionStore sessions, CopilotProjections projections) =>
            ToResult(projections.OpenPoints(DemoAuthEndpoints.CurrentSession(http, sessions), caseId)));
        return app;
    }

    internal static IResult ToResult(CopilotProjectionResult result) => result.Outcome switch
    {
        CopilotProjectionOutcome.Ok => Results.Ok(result.Value),
        CopilotProjectionOutcome.Unauthenticated => DemoAuthEndpoints.Unauthenticated(),
        CopilotProjectionOutcome.Forbidden => Error(StatusCodes.Status403Forbidden, "forbidden"),
        CopilotProjectionOutcome.PointLimit => Error(StatusCodes.Status422UnprocessableEntity, "copilot_point_limit"),
        CopilotProjectionOutcome.ContextInvalid => Error(StatusCodes.Status502BadGateway, "copilot_context_invalid"),
        _ => Results.NotFound(),
    };

    private static IResult Error(int statusCode, string code) =>
        Results.Json(new { contractVersion = CopilotProjections.ContractVersion, error = new { code } }, statusCode: statusCode);
}
