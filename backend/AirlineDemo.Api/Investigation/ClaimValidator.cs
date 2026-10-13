namespace AirlineDemo.Api.Investigation;

/// <summary>Kinds of investigator claim that policy routes differently.</summary>
public enum ClaimKind
{
    Baseline,
    MissingRequirement,
    Conflict,
}

public enum ClaimStatus
{
    Supported,
    Downgraded,
}

/// <summary>A document in the current inventory with the digest recorded for its pages.</summary>
public sealed record BasisDocument(string DocumentId, string ContentDigest, IReadOnlyList<string> Pages)
{
    public static BasisDocument Create(string documentId, IReadOnlyList<string> pages) =>
        new(documentId, PagesDigest(pages), pages);

    internal static string PagesDigest(IReadOnlyList<string> pages) =>
        SyntheticContentRegistry.Sha256Hex(string.Join('\u001F', pages));
}

public sealed record ValidatedClaim(InvestigationClaim Claim, ClaimKind Kind, ClaimStatus Status, string? Reason);

/// <summary>
/// Checks each investigator claim against the current basis. A claim is supported only when every witness names a
/// document in the inventory whose current content still matches its recorded digest, on an existing page, with a
/// quote that appears verbatim there. Anything else is downgraded. Claim text is never interpreted.
/// </summary>
public static class ClaimValidator
{
    public static ValidatedClaim Validate(InvestigationClaim claim, ClaimKind kind, IReadOnlyDictionary<string, BasisDocument> basis)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(basis);

        if (claim.Witnesses is null || claim.Witnesses.Count == 0)
        {
            return new ValidatedClaim(claim, kind, ClaimStatus.Downgraded, "no witness");
        }

        foreach (var witness in claim.Witnesses)
        {
            var problem = WitnessProblem(witness, basis);
            if (problem is not null)
            {
                return new ValidatedClaim(claim, kind, ClaimStatus.Downgraded, problem);
            }
        }

        return new ValidatedClaim(claim, kind, ClaimStatus.Supported, null);
    }

    private static string? WitnessProblem(DocumentWitness? witness, IReadOnlyDictionary<string, BasisDocument> basis)
    {
        if (witness is null || string.IsNullOrEmpty(witness.DocumentId))
        {
            return "witness has no document";
        }

        if (!basis.TryGetValue(witness.DocumentId, out var document) || document.Pages is null)
        {
            return "document not in current inventory";
        }

        if (BasisDocument.PagesDigest(document.Pages) != document.ContentDigest)
        {
            return "content digest mismatch";
        }

        if (witness.Page < 1 || witness.Page > document.Pages.Count)
        {
            return "page outside document";
        }

        if (string.IsNullOrWhiteSpace(witness.Quote) ||
            !document.Pages[witness.Page - 1].Contains(witness.Quote, StringComparison.Ordinal))
        {
            return "quote not on witness page";
        }

        return null;
    }
}
