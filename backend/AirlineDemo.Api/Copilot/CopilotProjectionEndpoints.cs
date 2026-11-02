using AirlineDemo.Api.Auth;
using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Copilot;

public static class CopilotProjectionEndpoints
{
    public static IEndpointRouteBuilder MapCopilotProjectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/cases/{caseId}/copilot/summary", (string caseId, HttpContext http, DemoSessionStore sessions, CopilotProjections projections) =>
            ToResult(projections.Summary(DemoAuthEndpoints.CurrentSession(http, sessions), caseId)));
        app.MapGet("/api/v1/cases/{caseId}/copilot/open-points", (string caseId, HttpContext http, DemoSessionStore sessions, CopilotProjections projections) =>
            ToResult(projections.OpenPoints(DemoAuthEndpoints.CurrentSession(http, sessions), caseId)));
        app.MapGet("/api/v1/cases/{caseId}/copilot/activity", Activity);
        return app;
    }

    // Every copilot invocation of the case in time order, with its detail rows (FR-026 to FR-029). Record refs and
    // screening are limited to the sources the viewer's role may see (FR-032); the user is resolved at read time.
    private static async Task<IResult> Activity(string caseId, HttpContext http, DemoSessionStore sessions, CopilotProjections projections,
        ICopilotInvocationStore ledger, ICopilotInvocationDetailStore detailStore, ICopilotInvocationLedger refs)
    {
        var access = projections.Authorize(DemoAuthEndpoints.CurrentSession(http, sessions), caseId);
        if (access.Denied is not null) return ToResult(access.Denied);
        IReadOnlyList<CopilotInvocationRecord> records;
        ILookup<Guid, CopilotInvocationDetail> details;
        try
        {
            records = await ledger.ReadCaseAsync(caseId, http.RequestAborted);
            details = (await detailStore.ListForCaseAsync(caseId, http.RequestAborted)).ToLookup(detail => detail.InvocationId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { return CopilotInvocation.AuditUnavailable(); }
        var users = DemoFixture.Users.ToDictionary(user => refs.CallerRef(user.Id), user => user.DisplayName, StringComparer.Ordinal);
        var items = records.GroupBy(record => record.InvocationId)
            .Select(group => group.FirstOrDefault(record => record.Phase == "outcome") ?? group.First())
            .OrderBy(record => record.At).ThenBy(record => record.InvocationId)
            .Select(record => ActivityItem(record, details[record.InvocationId].ToList(), access.Role!, users));
        return Results.Ok(new { contractVersion = CopilotProjections.ContractVersion, caseId, items });
    }

    private static object ActivityItem(CopilotInvocationRecord record, List<CopilotInvocationDetail> rows, string viewerRole,
        Dictionary<string, string> users)
    {
        var reads = rows.Where(row => row.Kind == "record_read" && SourceSummaryService.CanSeeDetails(viewerRole, row.SourceId!)).ToList();
        var state = reads.Any(row => row.ScreeningState == ScreeningVerdict.Detected) ? ScreeningVerdict.Detected
            : reads.Any(row => row.ScreeningState == ScreeningVerdict.UnavailableState) ? ScreeningVerdict.UnavailableState : ScreeningVerdict.Clear;
        var gate = rows.FirstOrDefault(row => row.Kind == "release_gate");
        return new
        {
            invocationId = record.InvocationId, operation = record.Operation, at = record.At, outcome = record.Outcome,
            httpStatus = record.HttpStatus, resultCode = record.ResultCode, source = "from copilot",
            user = record.CallerRef is { } caller ? users.GetValueOrDefault(caller) : null, role = record.Role, provenance = record.Provenance,
            records = reads.Select(row => new { sourceId = row.SourceId, recordId = row.RecordId, recordVersion = row.RecordVersion }),
            screening = reads.Count == 0 ? null : new { state, flaggedFields = reads.SelectMany(row => row.FlaggedFields ?? []).Distinct(StringComparer.Ordinal) },
            proposalId = rows.FirstOrDefault(row => row.ProposalId is not null)?.ProposalId,
            gate = gate is null ? null : new { name = gate.Gate, result = gate.GateResult, blockers = gate.BlockerCodes ?? [] },
        };
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
