using AirlineDemo.Api.Foundry;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Investigation;

/// <summary>
/// Runs one investigation and appends its actual trace. The investigator records tool calls and citation checks,
/// this service records validation corrections, then the trace ends with a handover (approved) or a rejection.
/// Append failures propagate to the caller, so a failed append never looks like a successful investigation.
/// Input refused before execution starts is thrown without writing a trace.
/// </summary>
public sealed class InvestigationExecutionService(ICaseStateStore store)
{
    public Task<InvestigationResult> ExecuteAsync(
        CaseScope scope,
        InvestigationTraceContext context,
        BoundedInvestigator investigator,
        IReadOnlyList<string> documents,
        ClaimKind kind,
        IReadOnlyDictionary<string, BasisDocument> basis,
        string typedValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(investigator);
        return ExecuteCoreAsync(scope, context, documents, kind, basis, typedValue,
            (recorder, token) => investigator.InvestigateAsync(documents, token, recorder), cancellationToken);
    }

    public Task<InvestigationResult> ExecuteManagedAsync(
        CaseScope scope,
        InvestigationTraceContext context,
        IReadOnlyList<string> documents,
        ClaimKind kind,
        IReadOnlyDictionary<string, BasisDocument> basis,
        string typedValue,
        Func<InvestigationTraceRecorder, CancellationToken, Task<InvestigationResult>> execute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return ExecuteCoreAsync(scope, context, documents, kind, basis, typedValue, async (recorder, token) =>
        {
            try { return await execute(recorder, token); }
            catch (ManagedProjectException failure) when (LimitBreach(failure.Code) is { } breach)
            {
                throw new InvestigationLimitExceededException(breach);
            }
        }, cancellationToken);
    }

    private async Task<InvestigationResult> ExecuteCoreAsync(
        CaseScope scope,
        InvestigationTraceContext context,
        IReadOnlyList<string> documents,
        ClaimKind kind,
        IReadOnlyDictionary<string, BasisDocument> basis,
        string typedValue,
        Func<InvestigationTraceRecorder, CancellationToken, Task<InvestigationResult>> execute,
        CancellationToken cancellationToken)
    {
        BoundedInvestigator.ValidateDocuments(documents);
        var recorder = new InvestigationTraceRecorder(store, scope, context);

        InvestigationResult result;
        try
        {
            result = await execute(recorder, cancellationToken);
        }
        catch (Exception ex)
        {
            recorder.Reject(ex is InvestigationOutputException ? "rejected" : "escalated");
            throw;
        }

        foreach (var claim in result.Claims)
        {
            recorder.RecordValidation(ClaimValidator.Validate(claim, kind, basis, typedValue));
        }

        recorder.Complete("approved");
        return result;
    }

    private static InvestigationBreach? LimitBreach(string code) => code switch
    {
        "copilot_tool_limit" => InvestigationBreach.ToolCalls,
        "copilot_model_limit" => InvestigationBreach.ModelCalls,
        "copilot_timeout" => InvestigationBreach.Time,
        "copilot_input_token_limit" => InvestigationBreach.InputTokens,
        "copilot_output_token_limit" => InvestigationBreach.OutputTokens,
        "copilot_retry_limit" => InvestigationBreach.Retries,
        _ => null,
    };
}
