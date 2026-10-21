using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Projection;

/// <summary>One exact ARR history read, scoped by case, run, and evidence basis.</summary>
public sealed record ArrCaseHistorySelection(string CaseId, string RunId, string BasisId,
    IReadOnlyList<CaseJourneyEvent> Events, bool OlderHistoryAvailable);

/// <summary>ARR supplies case history; the case-pack projection adds no history store.</summary>
public interface IArrCaseHistorySource
{
    ArrCaseHistorySelection? Read(string caseId, string runId, string basisId);
}

/// <summary>Default ARR history reader backed by the scoped case-state store.</summary>
public sealed class ArrCaseHistorySource(ICaseStateStore store) : IArrCaseHistorySource
{
    public ArrCaseHistorySelection? Read(string caseId, string runId, string basisId)
    {
        ArgumentNullException.ThrowIfNull(store);
        var owner = store.OwnerOf(caseId);
        if (string.IsNullOrWhiteSpace(owner)) return null;
        try
        {
            var scope = store.AuthorizeCase(caseId, owner);
            var state = store.Read(scope);
            if (!StringComparer.Ordinal.Equals(state.CaseId, caseId)) return null;
            var all = (state.JourneyEvents ?? []).Where(item =>
                StringComparer.Ordinal.Equals(item.CaseId, caseId)).OrderBy(item => item.Sequence).ToArray();
            var selected = all.Where(item =>
                StringComparer.Ordinal.Equals(item.RunId, runId)
                && StringComparer.Ordinal.Equals(item.BasisId, basisId)).ToArray();
            if (selected.Length == 0) return null;
            var runEvents = all.Where(item => StringComparer.Ordinal.Equals(item.RunId, runId)).ToArray();
            var older = all.Any(item => !StringComparer.Ordinal.Equals(item.RunId, runId))
                || runEvents.Length > selected.Length;
            return new ArrCaseHistorySelection(caseId, runId, basisId, Array.AsReadOnly(selected), older);
        }
        catch (CaseAccessDeniedException)
        {
            return null;
        }
    }
}
