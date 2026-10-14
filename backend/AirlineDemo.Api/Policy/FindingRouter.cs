using AirlineDemo.Api.Investigation;

namespace AirlineDemo.Api.Policy;

/// <summary>Processing and inventory coverage for one investigation. Zero inventory is never complete.</summary>
public sealed record ProcessingCoverage(int Inventory, int Processed, int Failed, int Partial)
{
    public bool IsComplete => Inventory > 0 && Processed == Inventory && Failed == 0 && Partial == 0;
}

public enum FindingOutcome
{
    Blocked,
    Review,
    CompleteBaseline,
}

/// <summary>Deterministic decision. A complete baseline is never automatically accepted.</summary>
public sealed record FindingDecision(FindingOutcome Outcome, int RequestCount, string Reason)
{
    public bool AutoAccepted => false;
}

/// <summary>
/// Deterministic policy over validated claims. It reads only structured fields (coverage, claim kind, validation
/// status, identity flag), never claim or document text, so document-embedded instructions cannot change routing.
/// </summary>
public static class FindingRouter
{
    public static FindingDecision Route(ProcessingCoverage coverage, IReadOnlyList<ValidatedClaim> claims, bool identityAmbiguous)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(claims);

        if (!coverage.IsComplete)
        {
            return new FindingDecision(FindingOutcome.Blocked, 0, "processing or inventory coverage incomplete");
        }

        if (identityAmbiguous)
        {
            return new FindingDecision(FindingOutcome.Review, 0, "ambiguous identity");
        }

        if (claims.Any(c => c.Kind == ClaimKind.Conflict))
        {
            return new FindingDecision(FindingOutcome.Review, 0, "conflicting evidence");
        }

        if (claims.Any(c => c.Status == ClaimStatus.Downgraded))
        {
            return new FindingDecision(FindingOutcome.Review, 0, "unsupported citation downgraded");
        }

        if (!claims.Any(c => c.Status == ClaimStatus.Supported))
        {
            return new FindingDecision(FindingOutcome.Review, 0, "zero eligible examples");
        }

        // A structurally valid gap claim is only a candidate: a person must confirm it before any request exists.
        if (claims.Any(c => c.Kind == ClaimKind.MissingRequirement))
        {
            return new FindingDecision(FindingOutcome.Review, 0, "supported missing requirement needs internal review");
        }

        return new FindingDecision(FindingOutcome.CompleteBaseline, 0, "evidence-linked baseline");
    }
}
