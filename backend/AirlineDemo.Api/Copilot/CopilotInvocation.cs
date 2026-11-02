using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;

namespace AirlineDemo.Api.Copilot;

/// <summary>An operation result; <c>OutcomeRecorded</c> is set when the store already appended the outcome in its own transaction.</summary>
public sealed record CopilotInvocationResult(IResult Result, int HttpStatus, string ResultCode, CopilotOutcome Outcome, bool OutcomeRecorded = false);

/// <summary>
/// Wraps one copilot operation in durable admission and outcome records. The provenance is chosen by the calling
/// dispatch code, never by the request. Admission completes before <c>work</c> runs; any ledger failure stops with
/// <c>503 audit_unavailable</c>.
/// </summary>
public static class CopilotInvocation
{
    public const int ClientClosedRequest = 499;

    public static Task<IResult> RunAsync(ICopilotInvocationLedger ledger, ICaseStateStore cases, DemoSession? session,
        string operation, CopilotProvenance provenance, string? caseId, Func<CancellationToken, Task<CopilotInvocationResult>> work,
        CancellationToken cancellationToken) =>
        RunAsync(ledger, cases, session, operation, provenance, caseId, (_, token) => work(token), cancellationToken);

    public static async Task<IResult> RunAsync(ICopilotInvocationLedger ledger, ICaseStateStore cases, DemoSession? session,
        string operation, CopilotProvenance provenance, string? caseId, Func<CopilotAdmission, CancellationToken, Task<CopilotInvocationResult>> work,
        CancellationToken cancellationToken)
    {
        CopilotAdmission admission;
        try { admission = await ledger.Admit(operation, provenance, session, AuthorizedCaseRef(cases, session, caseId), cancellationToken); }
        catch (CopilotAuditUnavailableException) { return AuditUnavailable(); }

        CopilotInvocationResult result;
        try { result = await work(admission, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { await ledger.Outcome(admission, ClientClosedRequest, "cancelled", CopilotOutcome.Cancelled, CancellationToken.None); }
            catch (CopilotAuditUnavailableException) { }
            throw;
        }

        if (result.OutcomeRecorded) return result.Result;
        try { await ledger.Outcome(admission, result.HttpStatus, result.ResultCode, result.Outcome, cancellationToken); }
        catch (CopilotAuditUnavailableException) { return AuditUnavailable(); }
        return result.Result;
    }

    /// <summary>The canonical case id only when that case is in the caller's scope; reads ownership, never case data.</summary>
    public static string? AuthorizedCaseRef(ICaseStateStore cases, DemoSession? session, string? caseId) =>
        session is not null && caseId is not null && CasePackEndpoints.Metadata(caseId) is not null
            && TechnicalReviewPolicy.ScopeFor(cases, session.User.CaseScopes, caseId) is not null
            ? caseId : null;

    public static IResult AuditUnavailable() => Results.Json(
        new { contractVersion = CopilotProjections.ContractVersion, error = new { code = "audit_unavailable" } },
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
