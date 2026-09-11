namespace AirlineDemo.Api;

public static class FindingDerivation
{
    public static IReadOnlyList<Finding> Derive(
        EvidenceBasis evidenceBasis,
        InvestigationOutcome investigation)
    {
        ArgumentNullException.ThrowIfNull(evidenceBasis);
        ArgumentNullException.ThrowIfNull(investigation);

        if (investigation.Status != "complete")
        {
            return [];
        }

        if (!StringComparer.Ordinal.Equals(
                investigation.BasisId,
                evidenceBasis.BasisId) ||
            investigation.Error is not null ||
            !ScopeMatches(investigation.Context, evidenceBasis.Context))
        {
            throw new InvalidOperationException(
                "A complete investigation must belong to its evidence basis.");
        }

        return investigation.Findings
            .OrderBy(finding => finding.FindingId, StringComparer.Ordinal)
            .ThenBy(finding => finding.RequirementId, StringComparer.Ordinal)
            .Select(finding => new Finding(
                finding.FindingId,
                finding.ComponentId,
                finding.RequirementId,
                evidenceBasis.BasisId,
                finding.Assessment,
                finding.ReasonCode,
                finding.Explanation,
                finding.EvidenceRefs.ToArray()))
            .ToArray();
    }

    private static bool ScopeMatches(CaseContext left, CaseContext right) =>
        left.RunId == right.RunId &&
        left.CaseId == right.CaseId &&
        left.AirlineId == right.AirlineId &&
        left.AircraftId == right.AircraftId &&
        left.LeaseId == right.LeaseId;
}
