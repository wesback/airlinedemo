using AirlineDemo.Api.Investigation;

namespace AirlineDemo.Api.Evidence;

/// <summary>Creates read sources that expose only the documents accepted into one package.</summary>
public sealed class EvidenceCaseReadSourceFactory(EvidenceBasisStore basis)
{
    /// <param name="pinnedVersions">The accepted version of each document in the package, so retries read the same evidence.</param>
    public ICaseReadSource Create(string caseId, IReadOnlyList<string> acceptedFileNames, IReadOnlyDictionary<string, int> pinnedVersions) =>
        new AcceptedEvidenceReadSource(caseId, acceptedFileNames, pinnedVersions, basis);
}

internal sealed class AcceptedEvidenceReadSource(
    string caseId,
    IReadOnlyList<string> acceptedFileNames,
    IReadOnlyDictionary<string, int> pinnedVersions,
    EvidenceBasisStore basis) : ICaseReadSource
{
    private bool Owns(string requestedCaseId) => string.Equals(requestedCaseId, caseId, StringComparison.Ordinal);

    public IReadOnlyList<string> GetInventory(string requestedCaseId) =>
        Owns(requestedCaseId) ? acceptedFileNames : Array.Empty<string>();

    public string? GetApprovedRequirements(string requestedCaseId) => null;

    public string? GetSourcePage(string requestedCaseId, string documentId, int page)
    {
        if (!Owns(requestedCaseId) || page != 1 || !acceptedFileNames.Contains(documentId, StringComparer.Ordinal)) return null;
        if (!pinnedVersions.TryGetValue(documentId, out var version)) return null;
        return basis.History($"{caseId}/{documentId}").FirstOrDefault(v => v.Version == version)?.Text;
    }

    public IReadOnlyList<string> SearchWithinBasis(string requestedCaseId, string query) => Array.Empty<string>();

    public string? GetProcessingStatus(string requestedCaseId) => null;
}
