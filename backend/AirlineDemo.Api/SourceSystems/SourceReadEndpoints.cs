using System.Diagnostics;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Diagnostics;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AirlineDemo.Api.SourceSystems;
public static class SourceReadEndpoints
{
    public static IEndpointRouteBuilder MapSourceReadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v{version:int}/cases/{caseId}/sources/{sourceId}/records", List);
        app.MapGet("/api/v{version:int}/cases/{caseId}/sources/{sourceId}/records/{recordId}", Get);
        app.MapMethods("/api/v{version:int}/cases/{caseId}/sources/{sourceId}/records", ["POST"], RejectListMutation);
        app.MapMethods("/api/v{version:int}/cases/{caseId}/sources/{sourceId}/records/{recordId}", ["POST"], RejectGetMutation);
        return app;
    }
    private static IResult RejectListMutation(int version, string caseId, string sourceId) =>
        Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
    private static IResult RejectGetMutation(int version, string caseId, string sourceId, string recordId) =>
        Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
    private static Task<IResult> List(int version, string caseId, string sourceId, HttpContext http, DemoSessionStore sessions,
        ICaseStateStore cases, SourceModeStore modes, GovernedSourceReader reader, ArrivalJourneyService arrival,
        TimeProvider time, ILogger<SourceReadBudget> logger, IHostEnvironment environment) =>
        Read(version, caseId, sourceId, null, http, sessions, cases, modes, reader, arrival, time, logger, environment);

    private static Task<IResult> Get(int version, string caseId, string sourceId, string recordId, HttpContext http,
        DemoSessionStore sessions, ICaseStateStore cases, SourceModeStore modes, GovernedSourceReader reader,
        ArrivalJourneyService arrival, TimeProvider time, ILogger<SourceReadBudget> logger, IHostEnvironment environment) =>
        Read(version, caseId, sourceId, recordId, http, sessions, cases, modes, reader, arrival, time, logger, environment);
    internal static async Task<IResult> Read(int version, string caseId, string sourceId, string? recordId, HttpContext http,
        DemoSessionStore sessions, ICaseStateStore cases, SourceModeStore modes, GovernedSourceReader reader,
        ArrivalJourneyService arrival, TimeProvider time, ILogger<SourceReadBudget> logger, IHostEnvironment environment)
    {
        var diagnosticStartedAt = Stopwatch.GetTimestamp();
        if (version != 1) return Error(StatusCodes.Status400BadRequest, "unsupported_contract_version");

        var receivedAt = time.GetUtcNow();
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return Error(StatusCodes.Status401Unauthorized, "unauthenticated");

        var scope = TechnicalReviewPolicy.ScopeFor(cases, session.User.CaseScopes, caseId);
        if (scope is null)
        {
            if (cases.OwnerOf(caseId) is not null && !session.User.CaseScopes.Contains(caseId, StringComparer.Ordinal))
                SourceSystemDiagnostics.CrossCaseReadDenied(logger, environment.EnvironmentName, sourceId, caseId,
                    SourceSystemDiagnostics.ElapsedMilliseconds(diagnosticStartedAt));
            return Results.NotFound();
        }
        var caseState = cases.Read(scope);
        if (!Allowed(session.User.Role, sourceId))
        {
            if (SourceModes.Sources.Contains(sourceId, StringComparer.Ordinal) && arrival.IsEnrolled(caseState))
            {
                var deniedMode = modes.ModesFor(caseState)[sourceId];
                var denied = new GovernedSourceReadResult(sourceId, deniedMode, null, 0, "denied", "forbidden", []);
                var auditFailure = RecordReadAudit(scope, session, denied, arrival, logger, environment, caseId, diagnosticStartedAt);
                if (auditFailure is not null) return auditFailure;
            }
            SourceSystemDiagnostics.OutOfRoleReadDenied(logger, environment.EnvironmentName, sourceId, caseId,
                SourceSystemDiagnostics.ElapsedMilliseconds(diagnosticStartedAt));
            return Error(StatusCodes.Status403Forbidden, "forbidden");
        }
        if (http.Request.Query.Count != 0) return Error(StatusCodes.Status400BadRequest, "invalid_request");
        if (!SourceModes.Sources.Contains(sourceId, StringComparer.Ordinal))
            return Error(StatusCodes.Status422UnprocessableEntity, "unknown_source");
        var mode = modes.ModesFor(caseState)[sourceId];
        var outcome = await reader.ReadAsync(session.Id, caseId, sourceId, recordId, mode, receivedAt, http.RequestAborted);
        LogOutcome(outcome, logger, environment, caseId, diagnosticStartedAt);

        if (arrival.IsEnrolled(caseState))
        {
            var auditFailure = RecordReadAudit(scope, session, outcome, arrival, logger, environment, caseId, diagnosticStartedAt);
            if (auditFailure is not null) return auditFailure;
        }

        if (outcome.ErrorCode is not null)
            return Error(outcome.ErrorCode switch
            {
                "record_not_found" => StatusCodes.Status404NotFound,
                "unknown_source" => StatusCodes.Status422UnprocessableEntity,
                "source_case_mismatch" => StatusCodes.Status502BadGateway,
                "source_record_limit" => StatusCodes.Status422UnprocessableEntity,
                "source_read_timeout" => StatusCodes.Status504GatewayTimeout,
                "source_read_limit" => StatusCodes.Status429TooManyRequests,
                _ => StatusCodes.Status503ServiceUnavailable,
            }, outcome.ErrorCode);

        return recordId is null
            ? Results.Ok(new { contractVersion = 1, caseId, sourceId, records = outcome.Value })
            : Results.Ok(new { contractVersion = 1, caseId, sourceId, record = outcome.Value });
    }
    internal static bool Allowed(string role, string sourceId) => role switch
    {
        "technical-reviewer" => sourceId is "technical" or "maintenance" or "records",
        "finance-reviewer" => sourceId is "technical" or "finance",
        "legal-reviewer" => sourceId is "contracts" or "records",
        _ => false,
    };
    internal static IResult? RecordReadAudit(CaseScope scope, DemoSession session, GovernedSourceReadResult outcome,
        ArrivalJourneyService arrival, ILogger logger, IHostEnvironment environment, string caseId, long startedAt)
    {
        try
        {
            arrival.RecordSourceRead(scope, session.User.Id, session.User.Role, outcome);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or DbUpdateException or RevisionConflictException)
        {
            SourceSystemDiagnostics.AuditWriteFailed(logger, environment.EnvironmentName, outcome.SourceId, caseId,
                SourceSystemDiagnostics.ElapsedMilliseconds(startedAt));
            return Error(StatusCodes.Status503ServiceUnavailable, "audit_write_failed");
        }
    }

    internal static void LogOutcome(GovernedSourceReadResult outcome, ILogger logger, IHostEnvironment environment,
        string caseId, long startedAt)
    {
        var duration = SourceSystemDiagnostics.ElapsedMilliseconds(startedAt);
        switch (outcome.ErrorCode)
        {
            case "source_read_timeout":
                SourceSystemDiagnostics.ReadTimedOut(logger, environment.EnvironmentName, outcome.SourceId, caseId, duration);
                break;
            case "source_read_limit":
                SourceSystemDiagnostics.ReadLimited(logger, environment.EnvironmentName, outcome.SourceId, caseId, duration);
                break;
            case "source_unavailable":
                SourceSystemDiagnostics.SourceUnavailable(logger, environment.EnvironmentName, outcome.SourceId, caseId, duration);
                break;
            case "source_record_limit":
                SourceSystemDiagnostics.RecordLimitRejected(logger, environment.EnvironmentName, outcome.SourceId, caseId, duration);
                break;
        }
    }
    private static IResult Error(int status, string code) =>
        Results.Json(new { contractVersion = 1, error = new { code } }, statusCode: status);
}
