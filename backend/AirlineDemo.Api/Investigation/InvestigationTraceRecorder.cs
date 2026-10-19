using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Investigation;

/// <summary>Run-level metadata written with each trace. Identifiers and categorical values only.</summary>
public sealed record InvestigationTraceContext(
    string RunId,
    string InvestigationId,
    int BasisVersion,
    string ModelDeploymentName,
    string ApiVersion,
    string Region,
    string ContentFilterSetting,
    string Decision,
    string ActorToken);

/// <summary>
/// Records the actual ordered events of one started investigation and appends them as one trace. The investigator
/// records tool calls and citation checks while it runs; the caller records validation corrections, then calls
/// <see cref="Complete"/> (handover) or <see cref="Reject"/> (no handover). Append failures propagate to the caller.
/// </summary>
public sealed class InvestigationTraceRecorder(ICaseStateStore store, CaseScope scope, InvestigationTraceContext context)
{
    public const string NoValue = InvestigationTraceRules.NoValue;
    public const string UnknownTool = "unknown-tool";
    public const string DeterministicPolicy = "deterministic-policy";
    public const string ToolCallRequested = "requested";
    public const string CitationVerified = "verified";
    public const string CitationQuoteNotOnPage = "quote-not-on-page";
    public const string DowngradedCorrection = "Downgraded";

    private readonly List<InvestigationTraceStep> _steps = [];

    public void RecordToolCall(string toolName) =>
        Add(toolName, NoValue, 0, ToolCallRequested, NoValue, NoValue);

    public void RecordCitationCheck(string documentId, int page, string outcome) =>
        Add("citation-check", documentId, page, outcome, NoValue, NoValue);

    // A downgrade is recorded as a correction; a supported claim is recorded with no correction.
    public void RecordValidation(ValidatedClaim validated)
    {
        ArgumentNullException.ThrowIfNull(validated);
        var correction = validated.Status == ClaimStatus.Downgraded ? DowngradedCorrection : NoValue;
        Add("claim-validation", NoValue, 0, NoValue, correction, NoValue);
    }

    public void Complete(string outcome)
    {
        Add("policy-handover", NoValue, 0, NoValue, NoValue, DeterministicPolicy);
        Append(outcome);
    }

    public void Reject(string outcome) => Append(outcome);

    private void Add(string toolName, string documentId, int page, string citationOutcome, string correction, string handover) =>
        _steps.Add(new InvestigationTraceStep(_steps.Count + 1, toolName, documentId, page, citationOutcome, correction, handover));

    private void Append(string outcome) =>
        store.AppendTrace(scope, new InvestigationTrace(
            scope.CaseId,
            context.RunId,
            context.InvestigationId,
            context.BasisVersion,
            context.ModelDeploymentName,
            context.ApiVersion,
            context.Region,
            context.ContentFilterSetting,
            context.Decision,
            context.ActorToken,
            outcome,
            _steps.ToList()));
}
