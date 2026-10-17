using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.SourceSystems;
public static class SourceReadEndpoints
{
    private const int MaxListReadRecords = 101;
    private sealed record ReadOutcome(object? Value, int Count, string? ErrorCode);
    public static IEndpointRouteBuilder MapSourceReadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/cases/{caseId}/sources/{sourceId}/records", List);
        app.MapGet("/api/v1/cases/{caseId}/sources/{sourceId}/records/{recordId}", Get);
        return app;
    }
    private static Task<IResult> List(string caseId, string sourceId, HttpContext http, DemoSessionStore sessions,
        ICaseStateStore cases, SourceReadBudget budget, TimeProvider time) =>
        Read(caseId, sourceId, null, http, sessions, cases, budget, time);

    private static Task<IResult> Get(string caseId, string sourceId, string recordId, HttpContext http,
        DemoSessionStore sessions, ICaseStateStore cases, SourceReadBudget budget, TimeProvider time) =>
        Read(caseId, sourceId, recordId, http, sessions, cases, budget, time);
    private static async Task<IResult> Read(string caseId, string sourceId, string? recordId, HttpContext http,
        DemoSessionStore sessions, ICaseStateStore cases, SourceReadBudget budget, TimeProvider time)
    {
        var receivedAt = time.GetUtcNow();
        var remaining = receivedAt + SourceReadBudget.ReadDeadline - time.GetUtcNow();
        using var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, deadline.Token);
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return Error(StatusCodes.Status401Unauthorized, "unauthenticated");

        if (TechnicalReviewPolicy.ScopeFor(cases, session.User.CaseScopes, caseId) is null) return Results.NotFound();
        if (!Allowed(session.User.Role, sourceId)) return Error(StatusCodes.Status403Forbidden, "forbidden");
        if (http.Request.Query.Count != 0) return Error(StatusCodes.Status400BadRequest, "invalid_request");

        Guid? leaseId;
        try { leaseId = await budget.TryAcquireAsync(session.Id, caseId, receivedAt, linked.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !http.RequestAborted.IsCancellationRequested)
        { return Error(StatusCodes.Status504GatewayTimeout, "source_read_timeout"); }
        if (leaseId is null) return Error(StatusCodes.Status429TooManyRequests, "source_read_limit");

        try
        {
            try
            {
                var outcome = recordId is null
                    ? await ListSource(http.RequestServices, sourceId, caseId, linked.Token)
                    : await GetSource(http.RequestServices, sourceId, caseId, recordId, linked.Token);
                if (deadline.IsCancellationRequested) return Error(StatusCodes.Status504GatewayTimeout, "source_read_timeout");
                if (outcome.ErrorCode is not null)
                    return Error(outcome.ErrorCode switch
                    {
                        "record_not_found" => StatusCodes.Status404NotFound,
                        "unknown_source" => StatusCodes.Status422UnprocessableEntity,
                        "source_case_mismatch" => StatusCodes.Status502BadGateway,
                        _ => StatusCodes.Status503ServiceUnavailable,
                    }, outcome.ErrorCode);
                if (recordId is null && outcome.Count > 100) return Error(StatusCodes.Status422UnprocessableEntity, "source_record_limit");
                if (recordId is null)
                    return Results.Ok(new { contractVersion = 1, caseId, sourceId, records = outcome.Value });
                return Results.Ok(new { contractVersion = 1, caseId, sourceId, record = outcome.Value });
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !http.RequestAborted.IsCancellationRequested)
            {
                return Error(StatusCodes.Status504GatewayTimeout, "source_read_timeout");
            }
        }
        finally
        {
            await budget.ReleaseAsync(leaseId.Value, time.GetUtcNow());
        }
    }
    private static bool Allowed(string role, string sourceId) => role switch
    {
        "technical-reviewer" => sourceId is "technical" or "maintenance" or "records",
        "finance-reviewer" => sourceId is "technical" or "finance",
        "legal-reviewer" => sourceId is "contracts" or "records",
        _ => false,
    };
    private static Task<ReadOutcome> ListSource(IServiceProvider services, string sourceId, string caseId, CancellationToken cancellationToken) =>
        sourceId switch
        {
            "technical" => List(services.GetRequiredService<ISourceSystem<TechnicalPayload>>(), caseId, cancellationToken),
            "maintenance" => List(services.GetRequiredService<ISourceSystem<MaintenancePayload>>(), caseId, cancellationToken),
            "finance" => List(services.GetRequiredService<ISourceSystem<FinancePayload>>(), caseId, cancellationToken),
            "records" => List(services.GetRequiredService<ISourceSystem<RecordsPayload>>(), caseId, cancellationToken),
            "contracts" => List(services.GetRequiredService<ISourceSystem<ContractsPayload>>(), caseId, cancellationToken),
            _ => Task.FromResult(new ReadOutcome(null, 0, "unknown_source")),
        };
    private static Task<ReadOutcome> GetSource(IServiceProvider services, string sourceId, string caseId, string recordId,
        CancellationToken cancellationToken) =>
        sourceId switch
        {
            "technical" => Get(services.GetRequiredService<ISourceSystem<TechnicalPayload>>(), caseId, recordId, cancellationToken),
            "maintenance" => Get(services.GetRequiredService<ISourceSystem<MaintenancePayload>>(), caseId, recordId, cancellationToken),
            "finance" => Get(services.GetRequiredService<ISourceSystem<FinancePayload>>(), caseId, recordId, cancellationToken),
            "records" => Get(services.GetRequiredService<ISourceSystem<RecordsPayload>>(), caseId, recordId, cancellationToken),
            "contracts" => Get(services.GetRequiredService<ISourceSystem<ContractsPayload>>(), caseId, recordId, cancellationToken),
            _ => Task.FromResult(new ReadOutcome(null, 0, "unknown_source")),
        };
    private static async Task<ReadOutcome> List<T>(ISourceSystem<T> source, string caseId, CancellationToken cancellationToken) where T : class
    {
        var result = await source.ListAsync(caseId, cancellationToken, MaxListReadRecords);
        return result switch
        {
            SourceReadSucceeded<IReadOnlyList<SourceRecord<T>>> success =>
                success.Value.Any(record => !string.Equals(record.CaseId, caseId, StringComparison.Ordinal))
                    ? new(null, 0, "source_case_mismatch")
                    : new(success.Value, success.Value.Count, null),
            SourceUnavailable<IReadOnlyList<SourceRecord<T>>> => new(null, 0, "source_unavailable"),
            _ => new(null, 0, "unknown_source"),
        };
    }
    private static async Task<ReadOutcome> Get<T>(ISourceSystem<T> source, string caseId, string recordId, CancellationToken cancellationToken) where T : class
    {
        var result = await source.GetAsync(caseId, recordId, cancellationToken);
        return result switch
        {
            SourceReadSucceeded<SourceRecord<T>> success =>
                string.Equals(success.Value.CaseId, caseId, StringComparison.Ordinal)
                    ? new(success.Value, 1, null)
                    : new(null, 0, "source_case_mismatch"),
            SourceRecordNotFound<SourceRecord<T>> => new(null, 0, "record_not_found"),
            SourceUnavailable<SourceRecord<T>> => new(null, 0, "source_unavailable"),
            _ => new(null, 0, "unknown_source"),
        };
    }
    private static IResult Error(int status, string code) =>
        Results.Json(new { contractVersion = 1, error = new { code } }, statusCode: status);
}
