using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;

namespace AirlineDemo.Api.Runs;

/// <summary>History for one authorized case, run, and evidence-basis tuple.</summary>
public sealed record CaseHistoryResponse(
    string CaseId,
    string RunId,
    string BasisId,
    IReadOnlyList<CaseJourneyEvent> Events);

public static class CaseHistoryEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapCaseHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cases/{caseId}/history", GetHistory)
            .WithName("GetCaseHistory")
            .WithSummary("Read case history for a run and evidence basis")
            .WithDescription("Returns recorded events and their exact historical witnesses for an authorized case/run/basis tuple.")
            .Produces<CaseHistoryResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);
        return app;
    }

    private static IResult GetHistory(
        string caseId,
        string? runId,
        string? basisId,
        HttpContext http,
        DemoSessionStore sessions,
        ICaseStateStore cases,
        ILogger<Program> logger)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!session.User.CaseScopes.Contains(caseId, StringComparer.Ordinal)) return Results.NotFound();

        try
        {
            var scope = TechnicalReviewPolicy.ScopeFor(cases, session.User.CaseScopes, caseId);
            if (scope is null) return Results.NotFound();
            var state = cases.Read(scope);
            var history = (state.JourneyEvents ?? Array.Empty<CaseJourneyEvent>())
                .Where(item => string.Equals(item.CaseId, caseId, StringComparison.Ordinal))
                .OrderBy(item => item.Sequence)
                .ToArray();

            var selectedRunId = runId ?? history.LastOrDefault()?.RunId;
            if (selectedRunId is null) return Results.NotFound();

            var runHistory = history
                .Where(item => string.Equals(item.RunId, selectedRunId, StringComparison.Ordinal))
                .ToArray();
            var selectedBasisId = basisId
                ?? runHistory.LastOrDefault(item => item.BasisId is not null)?.BasisId;
            if (selectedBasisId is null) return Results.NotFound();

            var selectedEvents = runHistory
                .Where(item => string.Equals(item.BasisId, selectedBasisId, StringComparison.Ordinal))
                .ToArray();
            if (selectedEvents.Length == 0) return Results.NotFound();

            var response = new CaseHistoryResponse(caseId, selectedRunId, selectedBasisId, selectedEvents);
            var body = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
            return Results.Bytes(body, "application/json; charset=utf-8");
        }
        catch (CaseAccessDeniedException)
        {
            return Results.NotFound();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Case history read failed.");
            return Results.Json(new { error = "Case history could not be read." },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
