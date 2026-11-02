using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Diagnostics;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;

namespace AirlineDemo.Api.SourceSystems;

public static class ArrivalJourneyEndpoints
{
    private const int MaxCommandIdLength = 64;

    public static IEndpointRouteBuilder MapArrivalJourneyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/cases/{caseId}/arrival", GetStatus);
        app.MapPost("/api/v1/cases/{caseId}/arrival/start", Start);
        app.MapPost("/api/v1/cases/{caseId}/arrival/sources/{sourceId}/retrieve", RetrieveSource);
        app.MapPost("/api/v1/cases/{caseId}/arrival/complete", Complete);
        return app;
    }

    private static async Task<IResult> RetrieveSource(string caseId, string sourceId, HttpContext http,
        DemoSessionStore sessions, ICaseStateStore cases, SourceModeStore modes, GovernedSourceReader reader,
        ArrivalJourneyService arrival, TimeProvider time, ILogger<SourceReadBudget> logger, IHostEnvironment environment)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return Error(StatusCodes.Status401Unauthorized, "unauthenticated");

        var scope = TechnicalReviewPolicy.ScopeFor(cases, session.User.CaseScopes, caseId);
        if (scope is null) return Results.NotFound();
        var state = cases.Read(scope);
        if (!arrival.IsEnrolled(state)) return Results.NotFound();
        if (!SourceReadEndpoints.Allowed(session.User.Role, sourceId))
        {
            if (SourceModes.Sources.Contains(sourceId, StringComparer.Ordinal))
            {
                var denied = new GovernedSourceReadResult(sourceId, modes.ModesFor(state)[sourceId],
                    null, 0, "denied", "forbidden", []);
                var auditFailure = SourceReadEndpoints.RecordReadAudit(scope, session, denied, arrival, logger,
                    environment, caseId, startedAt);
                if (auditFailure is not null) return auditFailure;
            }
            return Error(StatusCodes.Status403Forbidden, "forbidden");
        }
        if (!SourceModes.Sources.Contains(sourceId, StringComparer.Ordinal))
            return Error(StatusCodes.Status422UnprocessableEntity, "unknown_source");
        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session))
            return Error(StatusCodes.Status403Forbidden, "anti_forgery_failed");

        var command = http.Request.Query.Count == 0 ? await TryReadCommandAsync(http) : null;
        if (command is null) return Error(StatusCodes.Status400BadRequest, "invalid_request");
        var replay = arrival.CheckRetrievalReplay(state, command.CommandId!, session.User.Id, session.User.Role,
            sourceId, command.ExpectedRevision!.Value);
        if (replay is { } replayResult) return RetrievalResponse(replayResult);
        if (state.Audit.Any(item => item.Action == ArrivalJourneyService.CompletionAction))
            return Error(StatusCodes.Status409Conflict, "arrival_completed");
        if (!state.Audit.Any(item => item.Action == ArrivalJourneyService.StartAction))
            return Error(StatusCodes.Status409Conflict, "arrival_not_started");
        if (state.Revision != command.ExpectedRevision)
            return Error(StatusCodes.Status409Conflict, "stale_revision");

        var mode = modes.ModesFor(state)[sourceId];
        var read = await reader.ReadAsync(session.Id, caseId, sourceId, null, mode, time.GetUtcNow(), http.RequestAborted);
        SourceReadEndpoints.LogOutcome(read, logger, environment, caseId, startedAt);
        if (read.ErrorCode is not null)
        {
            var auditFailure = SourceReadEndpoints.RecordReadAudit(scope, session, read, arrival, logger, environment,
                caseId, startedAt);
            return auditFailure ?? ReadError(read.ErrorCode);
        }

        IReadOnlyList<SourceBasisPin> pins = [];
        if (read.Outcome == "success")
        {
            try
            {
                pins = CreateSourcePins(read, caseId);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                var invalid = read with { Outcome = "rejected", ErrorCode = "source_record_invalid", RecordRefs = [] };
                var auditFailure = SourceReadEndpoints.RecordReadAudit(scope, session, invalid, arrival, logger, environment,
                    caseId, startedAt);
                return auditFailure ?? ReadError("source_record_invalid");
            }
        }

        try
        {
            var result = arrival.CommitRetrieval(scope, session.User.Id, session.User.Role, command.CommandId!,
                command.ExpectedRevision.Value, read, pins);
            return RetrievalResponse(result);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or DbUpdateException or InvalidDataException or BasisIntegrityException)
        {
            SourceSystemDiagnostics.AuditWriteFailed(logger, environment.EnvironmentName, sourceId, caseId,
                SourceSystemDiagnostics.ElapsedMilliseconds(startedAt));
            return Error(StatusCodes.Status503ServiceUnavailable, "audit_write_failed");
        }
    }

    private static async Task<IResult> Complete(string caseId, HttpContext http, DemoSessionStore sessions,
        ICaseStateStore cases, ArrivalJourneyService arrival)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return Error(StatusCodes.Status401Unauthorized, "unauthenticated");
        var scope = TechnicalReviewPolicy.ScopeFor(cases, session.User.CaseScopes, caseId);
        if (scope is null) return Results.NotFound();
        var state = cases.Read(scope);
        if (!arrival.IsEnrolled(state)) return Results.NotFound();
        if (session.User.Role != ArrivalJourneyService.Owner)
            return Error(StatusCodes.Status403Forbidden, "forbidden");
        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session))
            return Error(StatusCodes.Status403Forbidden, "anti_forgery_failed");

        var command = http.Request.Query.Count == 0 ? await TryReadCommandAsync(http) : null;
        if (command is null) return Error(StatusCodes.Status400BadRequest, "invalid_request");
        ArrivalCompletionResult result;
        try
        {
            result = arrival.Complete(scope, session.User.Id, command);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DbUpdateException)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "audit_write_failed");
        }
        return result.Outcome switch
        {
            ArrivalCompletionOutcome.Completed or ArrivalCompletionOutcome.Replayed => Results.Ok(result.Receipt),
            ArrivalCompletionOutcome.CommandIdConflict => Error(StatusCodes.Status409Conflict, "command_id_conflict"),
            ArrivalCompletionOutcome.StaleRevision => Error(StatusCodes.Status409Conflict, "stale_revision"),
            ArrivalCompletionOutcome.NotStarted => Error(StatusCodes.Status409Conflict, "arrival_not_started"),
            ArrivalCompletionOutcome.MissingSources => MissingSourcesError(result.MissingSources),
            _ => Error(StatusCodes.Status409Conflict, "stale_revision"),
        };
    }

    private static IResult GetStatus(string caseId, HttpContext http, DemoSessionStore sessions,
        ICaseStateStore cases, ArrivalJourneyService arrival)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return Error(StatusCodes.Status401Unauthorized, "unauthenticated");

        var scope = TechnicalReviewPolicy.ScopeFor(cases, session.User.CaseScopes, caseId);
        if (scope is null) return Results.NotFound();
        if (!DemoFixture.Users.Any(user => user.Role == session.User.Role))
            return Error(StatusCodes.Status403Forbidden, "forbidden");

        var state = cases.Read(scope);
        return !arrival.IsEnrolled(state)
            ? Results.NotFound()
            : Results.Ok(arrival.Status(state));
    }

    private static async Task<IResult> Start(string caseId, HttpContext http, DemoSessionStore sessions,
        ICaseStateStore cases, ArrivalJourneyService arrival)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return Error(StatusCodes.Status401Unauthorized, "unauthenticated");

        var knownRole = DemoFixture.Users.Any(user => user.Role == session.User.Role);
        if (knownRole && session.User.Role != ArrivalJourneyService.Owner)
            return Error(StatusCodes.Status403Forbidden, "forbidden");

        var scope = TechnicalReviewPolicy.ScopeFor(cases, session.User.CaseScopes, caseId);
        if (scope is null) return Results.NotFound();
        if (!knownRole || session.User.Role != ArrivalJourneyService.Owner)
            return Error(StatusCodes.Status403Forbidden, "forbidden");

        var state = cases.Read(scope);
        if (!arrival.IsEnrolled(state)) return Results.NotFound();
        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session))
            return Error(StatusCodes.Status403Forbidden, "anti_forgery_failed");

        var command = http.Request.Query.Count == 0 ? await TryReadCommandAsync(http) : null;
        if (command is null)
            return Error(StatusCodes.Status400BadRequest, "invalid_request");

        var result = arrival.Start(scope, session.User.Id, command);
        return result.Outcome switch
        {
            ArrivalStartOutcome.Started or ArrivalStartOutcome.Replayed => Results.Ok(result.Receipt),
            ArrivalStartOutcome.CommandIdConflict => Error(StatusCodes.Status409Conflict, "command_id_conflict"),
            ArrivalStartOutcome.StaleRevision => Error(StatusCodes.Status409Conflict, "stale_revision"),
            ArrivalStartOutcome.AlreadyStarted => Error(StatusCodes.Status409Conflict, "arrival_started"),
            ArrivalStartOutcome.Completed => Error(StatusCodes.Status409Conflict, "arrival_completed"),
            _ => Error(StatusCodes.Status409Conflict, "stale_revision"),
        };
    }

    private static async Task<ArrivalCommand?> TryReadCommandAsync(HttpContext http)
    {
        if (http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == false
            || http.Request.ContentLength == 0
            || !http.Request.HasJsonContentType())
            return null;

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var properties = document.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 2
                || properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != 2
                || !document.RootElement.TryGetProperty("commandId", out var commandId)
                || !document.RootElement.TryGetProperty("expectedRevision", out var revision)
                || commandId.ValueKind != JsonValueKind.String
                || revision.ValueKind != JsonValueKind.Number
                || !revision.TryGetInt64(out var revisionValue)
                || revisionValue < 0)
                return null;

            var commandValue = commandId.GetString();
            if (string.IsNullOrWhiteSpace(commandValue) || commandValue.Length > MaxCommandIdLength)
                return null;

            return new ArrivalCommand(commandValue, revisionValue);
        }
    }

    private static IResult Error(int status, string code) =>
        Results.Json(new { contractVersion = 1, error = new { code } }, statusCode: status);

    private static IResult MissingSourcesError(IReadOnlyList<string> missingSources) =>
        Results.Json(new { contractVersion = 1, error = new { code = "arrival_sources_missing", missingSources } },
            statusCode: StatusCodes.Status409Conflict);

    private static IResult RetrievalResponse(ArrivalRetrievalResult result) => result.Outcome switch
    {
        ArrivalRetrievalOutcome.Pinned or ArrivalRetrievalOutcome.Empty or ArrivalRetrievalOutcome.Stale
            or ArrivalRetrievalOutcome.Conflicting or ArrivalRetrievalOutcome.Replayed => Results.Ok(result.Receipt),
        ArrivalRetrievalOutcome.CommandIdConflict => Error(StatusCodes.Status409Conflict, "command_id_conflict"),
        ArrivalRetrievalOutcome.StaleRevision => Error(StatusCodes.Status409Conflict, "stale_revision"),
        ArrivalRetrievalOutcome.NotStarted => Error(StatusCodes.Status409Conflict, "arrival_not_started"),
        ArrivalRetrievalOutcome.Completed => Error(StatusCodes.Status409Conflict, "arrival_completed"),
        _ => Error(StatusCodes.Status409Conflict, "stale_revision"),
    };

    private static IResult ReadError(string code) =>
        Error(code switch
        {
            "record_not_found" => StatusCodes.Status404NotFound,
            "unknown_source" => StatusCodes.Status422UnprocessableEntity,
            "source_case_mismatch" or "source_record_invalid" => StatusCodes.Status502BadGateway,
            "source_record_limit" => StatusCodes.Status422UnprocessableEntity,
            "source_read_timeout" => StatusCodes.Status504GatewayTimeout,
            "source_read_limit" => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status503ServiceUnavailable,
        }, code);

    private static IReadOnlyList<SourceBasisPin> CreateSourcePins(GovernedSourceReadResult read, string caseId) =>
        read.Value switch
        {
            IReadOnlyList<SourceRecord<TechnicalPayload>> records => EvidenceIntake.CreateSourcePins(read.SourceId, caseId, records),
            IReadOnlyList<SourceRecord<MaintenancePayload>> records => EvidenceIntake.CreateSourcePins(read.SourceId, caseId, records),
            IReadOnlyList<SourceRecord<FinancePayload>> records => EvidenceIntake.CreateSourcePins(read.SourceId, caseId, records),
            IReadOnlyList<SourceRecord<RecordsPayload>> records => EvidenceIntake.CreateSourcePins(read.SourceId, caseId, records),
            IReadOnlyList<SourceRecord<ContractsPayload>> records => EvidenceIntake.CreateSourcePins(read.SourceId, caseId, records),
            _ => throw new InvalidDataException("The governed source reader returned an unsupported record collection."),
        };
}
