using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;

namespace AirlineDemo.Api.SourceSystems;

public static class SourceSummaryEndpoints
{
    private const int MaxPayloadBytes = 65_536;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapSourceSummaryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v{version:int}/cases/{caseId}/sources", Get);
        return app;
    }

    private static async Task<IResult> Get(
        int version,
        string caseId,
        HttpContext http,
        DemoSessionStore sessions,
        ICaseStateStore cases,
        SourceModeStore modes,
        SourceSummaryService summaries)
    {
        if (version != 1) return Error(StatusCodes.Status400BadRequest, "unsupported_contract_version");
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return Error(StatusCodes.Status401Unauthorized, "unauthenticated");

        var scope = TechnicalReviewPolicy.ScopeFor(cases, session.User.CaseScopes, caseId);
        if (scope is null) return Results.NotFound();
        var role = session.User.Role;
        if (role is not ("technical-reviewer" or "finance-reviewer" or "legal-reviewer" or "presenter" or "executive"))
            return Error(StatusCodes.Status403Forbidden, "forbidden");

        var state = cases.Read(scope);
        using var deadline = new CancellationTokenSource(SourceReadBudget.ReadDeadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, deadline.Token);
        try
        {
            var result = await summaries.BuildAsync(caseId, role, state, modes.ModesFor(state), linked.Token);
            if (deadline.IsCancellationRequested)
                return Error(StatusCodes.Status504GatewayTimeout, "source_read_timeout");
            if (result.ErrorCode is not null)
                return Error(StatusCodes.Status503ServiceUnavailable, result.ErrorCode);

            var response = new
            {
                contractVersion = 1,
                fixtureVersion = SourceFixtureManifest.SupportedSourceFixtureVersion,
                caseId,
                caseRevision = state.Revision,
                sources = result.Sources,
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
            return bytes.Length > MaxPayloadBytes
                ? Error(StatusCodes.Status422UnprocessableEntity, "source_payload_limit")
                : Results.Bytes(bytes, "application/json");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !http.RequestAborted.IsCancellationRequested)
        {
            return Error(StatusCodes.Status504GatewayTimeout, "source_read_timeout");
        }
    }

    private static IResult Error(int status, string code) =>
        Results.Json(new { contractVersion = 1, error = new { code } }, statusCode: status);
}
