using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Investigation;

/// <summary>
/// Runs one investigation and appends its actual trace. The investigator records tool calls and citation checks,
/// this service records validation corrections, then the trace ends with a handover (approved) or a rejection.
/// Append failures propagate to the caller, so a failed append never looks like a successful investigation.
/// </summary>
public sealed class InvestigationExecutionService(ICaseStateStore store)
{
    public async Task<InvestigationResult> ExecuteAsync(
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
        var recorder = new InvestigationTraceRecorder(store, scope, context);

        InvestigationResult result;
        try
        {
            result = await investigator.InvestigateAsync(documents, cancellationToken, recorder);
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
}
