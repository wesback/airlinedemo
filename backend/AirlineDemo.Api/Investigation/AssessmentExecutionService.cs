using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Policy;
using AirlineDemo.Api.Requests;

namespace AirlineDemo.Api.Investigation;

/// <summary>One typed model claim: the kind, typed value, identity and cited witnesses. No prompt or response text.</summary>
public sealed record AssessmentClaim(
    ClaimKind Kind,
    string Value,
    string Component,
    string SerialNumber,
    IReadOnlyList<DocumentWitness> Witnesses);

/// <summary>The accepted evidence basis an assessment runs against, with its completeness and identity state.</summary>
public sealed record AssessmentBasis(
    string BasisId,
    int BasisVersion,
    string ScenarioTime,
    ProcessingCoverage Coverage,
    bool IdentityAmbiguous,
    IReadOnlyDictionary<string, BasisDocument> Documents);

public sealed record AssessedWitness(string DocumentId, int Page);

public sealed record AssessedClaim(string Kind, string Value, string Component, string SerialNumber, IReadOnlyList<AssessedWitness> Witnesses);

/// <summary>
/// The stored result of one assessment. It holds typed values and source identifiers only: no witness quote,
/// document text or prompt.
/// </summary>
public sealed record AssessmentRecord(
    string CaseId,
    string BasisId,
    int BasisVersion,
    string Outcome,
    string Reason,
    IReadOnlyList<AssessedClaim> Claims,
    string? RequestId,
    string? WorkItemId);

/// <summary>
/// Validates typed claims against the current basis and applies the deterministic policy. A single supported
/// missing requirement on complete coverage creates one request through the mock inbox. A supported baseline creates
/// nothing. Every other case creates one open <c>records-investigation</c> item owned by <c>technical-reviewer</c>.
/// </summary>
public sealed class AssessmentExecutionService(ICaseStateStore store, MockRequestInbox inbox, AssessmentStore assessments)
{
    public const string Baseline = "Baseline";
    public const string Request = "Request";
    public const string Review = "Review";
    public const string ReviewCapability = "records-investigation";
    public const string ReviewOwner = "technical-reviewer";
    public const string Actor = "assessment-service";

    public AssessmentRecord Assess(CaseScope scope, AssessmentBasis basis, IReadOnlyList<AssessmentClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(claims);

        var validated = claims
            .Select(claim => ClaimValidator.Validate(
                new InvestigationClaim(claim.Value, claim.Witnesses), claim.Kind, basis.Documents, claim.Value))
            .ToArray();
        var assessed = claims
            .Select(claim => new AssessedClaim(
                claim.Kind.ToString(),
                claim.Value,
                claim.Component,
                claim.SerialNumber,
                (claim.Witnesses ?? []).Where(w => w is not null).Select(w => new AssessedWitness(w.DocumentId, w.Page)).ToArray()))
            .ToArray();

        var current = assessments.CurrentBasis(scope.CaseId);
        var isCurrent = current is not null && current.BasisId == basis.BasisId && current.BasisVersion == basis.BasisVersion;
        var (outcome, reason) = isCurrent
            ? Route(basis, validated)
            : (Review, "supplied basis is not the current accepted basis");
        string? requestId = null;
        string? workItemId = null;

        if (outcome == Request)
        {
            var claim = claims[0];
            var finding = new DeliveryFinding(basis.Coverage, validated, basis.IdentityAmbiguous, basis.BasisId, basis.BasisVersion);
            var envelope = new DeliveryEnvelope(
                $"{scope.CaseId}:{basis.BasisId}:{basis.BasisVersion}:assessment",
                scope.CaseId,
                $"assessment-{basis.BasisVersion}",
                basis.BasisId,
                basis.BasisVersion,
                basis.ScenarioTime,
                claim.Component,
                claim.SerialNumber,
                claim.Value);
            var delivery = inbox.Submit(envelope, finding, basis.ScenarioTime);
            if (delivery.Outcome == DeliveryOutcome.Delivered && delivery.Receipt is not null)
            {
                requestId = delivery.Receipt.RequestId;
            }
            else
            {
                // The inbox applies the same policy, so this is a safety net: anything it will not deliver goes to review.
                outcome = Review;
                reason = $"inbox declined: {delivery.Reason}";
            }
        }

        if (outcome == Review)
        {
            workItemId = $"assessment-{Guid.NewGuid():N}";
            store.CreateWorkItem(
                scope,
                store.Read(scope).Revision,
                new WorkItemDraft(workItemId, ReviewCapability, "open", ReviewOwner),
                Actor);
        }

        var record = new AssessmentRecord(
            scope.CaseId, basis.BasisId, basis.BasisVersion, outcome, reason, assessed, requestId, workItemId);
        assessments.Append(record);
        return record;
    }

    private static (string Outcome, string Reason) Route(AssessmentBasis basis, IReadOnlyList<ValidatedClaim> validated)
    {
        if (!basis.Coverage.IsComplete)
        {
            return (Review, "processing or inventory coverage incomplete");
        }

        if (basis.IdentityAmbiguous)
        {
            return (Review, "ambiguous identity");
        }

        if (validated.Any(v => v.Kind == ClaimKind.Conflict))
        {
            return (Review, "conflicting evidence");
        }

        var downgraded = validated.FirstOrDefault(v => v.Status == ClaimStatus.Downgraded);
        if (downgraded is not null)
        {
            return (Review, $"downgraded claim: {downgraded.Reason}");
        }

        if (validated.Count != 1)
        {
            return (Review, validated.Count == 0 ? "no claim" : "ambiguous: multiple claims");
        }

        return validated[0].Kind switch
        {
            ClaimKind.Baseline => (Baseline, "supported baseline"),
            ClaimKind.MissingRequirement => (Request, "single supported missing requirement on current basis"),
            _ => (Review, "unsupported claim kind"),
        };
    }
}
