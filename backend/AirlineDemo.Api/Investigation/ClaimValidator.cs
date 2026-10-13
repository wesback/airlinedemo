using System.Text.RegularExpressions;

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
        SyntheticContentRegistry.Sha256Hex(
            string.Concat(pages.Select(page => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{page.Length}:{page};"))));
}

/// <summary>
/// A claim after validation. Only <see cref="ClaimValidator"/> can construct one, so policy code cannot be handed a
/// Supported claim that was never checked against the basis.
/// </summary>
public sealed record ValidatedClaim
{
    internal ValidatedClaim(InvestigationClaim claim, ClaimKind kind, ClaimStatus status, string? reason)
    {
        Claim = claim;
        Kind = kind;
        Status = status;
        Reason = reason;
    }

    public InvestigationClaim Claim { get; }
    public ClaimKind Kind { get; }
    public ClaimStatus Status { get; }
    public string? Reason { get; }
}

/// <summary>
/// Checks each investigator claim against the current basis. A claim is supported only when its typed value appears
/// in every witness quote, and every witness names a document in the inventory whose current content still matches
/// its recorded digest, on an existing page, with a quote that appears verbatim there. Anything else is downgraded.
/// Claim text is never interpreted.
/// </summary>
public static class ClaimValidator
{
    public static ValidatedClaim Validate(
        InvestigationClaim claim,
        ClaimKind kind,
        IReadOnlyDictionary<string, BasisDocument> basis,
        string typedValue)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(basis);

        if (string.IsNullOrWhiteSpace(typedValue))
        {
            return new ValidatedClaim(claim, kind, ClaimStatus.Downgraded, "claim has no typed value");
        }

        if (claim.Witnesses is null || claim.Witnesses.Count == 0)
        {
            return new ValidatedClaim(claim, kind, ClaimStatus.Downgraded, "no witness");
        }

        foreach (var witness in claim.Witnesses)
        {
            var problem = WitnessProblem(witness, kind, basis, typedValue);
            if (problem is not null)
            {
                return new ValidatedClaim(claim, kind, ClaimStatus.Downgraded, problem);
            }
        }

        if (string.IsNullOrWhiteSpace(claim.Text) || !claim.Text.Contains(typedValue, StringComparison.Ordinal))
        {
            return new ValidatedClaim(claim, kind, ClaimStatus.Downgraded, "claim text does not state typed value");
        }

        // The claim may assert nothing beyond what its witnesses state, so a matching value cannot carry extra claims.
        var assertion = claim.Text.Trim();
        if (claim.Witnesses.Any(witness => !witness.Quote.Contains(assertion, StringComparison.Ordinal)))
        {
            return new ValidatedClaim(claim, kind, ClaimStatus.Downgraded, "claim text not stated by witness quote");
        }

        return new ValidatedClaim(claim, kind, ClaimStatus.Supported, null);
    }

    private static string? WitnessProblem(DocumentWitness? witness, ClaimKind kind, IReadOnlyDictionary<string, BasisDocument> basis, string typedValue)
    {
        if (witness is null || string.IsNullOrEmpty(witness.DocumentId))
        {
            return "witness has no document";
        }

        if (!basis.TryGetValue(witness.DocumentId, out var document) || document.Pages is null)
        {
            return "document not in current inventory";
        }

        if (!string.Equals(document.DocumentId, witness.DocumentId, StringComparison.Ordinal))
        {
            return "document identity mismatch";
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

        if (!witness.Quote.Contains(typedValue, StringComparison.Ordinal))
        {
            return "claim value not in witness quote";
        }

        // A quote that negates its statement cannot support a baseline or missing-requirement claim; conflicts may quote denials.
        if (kind != ClaimKind.Conflict && Negation.IsMatch(witness.Quote))
        {
            return "witness quote negates claim";
        }

        // A missing-requirement claim needs a current gap, so a quote saying the requirement is done or historical cannot support it.
        if (kind == ClaimKind.MissingRequirement && Resolution.IsMatch(witness.Quote))
        {
            return "witness quote shows requirement resolved";
        }

        return null;
    }

    private static readonly Regex Negation = new(
        @"\b(?:not|no|never|none|nil|zero|nothing|without|neither|nor|cannot|denied|denies|deny|denying|rejected|disproves?|absent|absence|lacks?|lacking)\b|n['’]t\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Resolution = new(
        @"\b(?:satisfied|satisfies|satisfy|satisfying|completed|completes|complete|completion|resolved|resolves|resolving|fulfilled|fulfils|fulfills|cleared|closed|met|historical|historically|previously|formerly|already|done|ready|approved|accepted|passed|passes|compliant|valid|effective|addressed|remedied|corrected|meets|meet|meeting|current|currently)\b|up[- ]to[- ]date|in place",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
