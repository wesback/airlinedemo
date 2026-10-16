using AirlineDemo.Api.Investigation;

namespace AirlineDemo.Api.Evidence;

/// <summary>Creates read sources that expose only the documents accepted into one package.</summary>
public sealed class EvidenceCaseReadSourceFactory(EvidenceBasisStore basis)
{
    public ICaseReadSource Create(string caseId, IReadOnlyList<string> acceptedFileNames) =>
        new AcceptedEvidenceReadSource(caseId, acceptedFileNames, basis);
}

internal sealed class AcceptedEvidenceReadSource(string caseId, IReadOnlyList<string> acceptedFileNames, EvidenceBasisStore basis) : ICaseReadSource
{
    private bool Owns(string requestedCaseId) => string.Equals(requestedCaseId, caseId, StringComparison.Ordinal);

    public IReadOnlyList<string> GetInventory(string requestedCaseId) =>
        Owns(requestedCaseId) ? acceptedFileNames : Array.Empty<string>();

    public string? GetApprovedRequirements(string requestedCaseId) => null;

    public string? GetSourcePage(string requestedCaseId, string documentId, int page)
    {
        if (!Owns(requestedCaseId) || page != 1 || !acceptedFileNames.Contains(documentId, StringComparer.Ordinal)) return null;
        return basis.Latest($"{caseId}/{documentId}")?.Text;
    }

    public IReadOnlyList<string> SearchWithinBasis(string requestedCaseId, string query) => Array.Empty<string>();

    public string? GetProcessingStatus(string requestedCaseId) => null;
}
