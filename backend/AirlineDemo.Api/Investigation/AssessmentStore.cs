using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Investigation;

/// <summary>Stores current basis snapshots and supplemental records; committed assessment history is read from the case store.</summary>
public sealed class AssessmentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<AssessmentRecord>> _supplementalByCase = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssessmentBasis> _currentByCase = new(StringComparer.Ordinal);
    private readonly ICaseStateStore? _caseStates;

    public AssessmentStore(ICaseStateStore? caseStates = null) => _caseStates = caseStates;
    internal bool HasPersistentHistory => _caseStates is not null;

    /// <summary>
    /// Records the case's current accepted evidence basis. An assessment against any other basis is not current.
    /// The stored basis is an immutable deep copy, so the caller changing the dictionary or page lists it passed in
    /// cannot change the evidence a later assessment validates against.
    /// </summary>
    public void SetCurrentBasis(string caseId, AssessmentBasis basis)
    {
        ArgumentNullException.ThrowIfNull(basis);
        var snapshot = Snapshot(basis);
        lock (_gate)
        {
            _currentByCase[caseId] = snapshot;
        }
    }

    private static AssessmentBasis Snapshot(AssessmentBasis basis)
    {
        var documents = basis.Documents.ToDictionary(
            pair => pair.Key,
            pair => pair.Value with { Pages = Array.AsReadOnly(pair.Value.Pages.ToArray()) },
            StringComparer.Ordinal);
        return basis with { Documents = new System.Collections.ObjectModel.ReadOnlyDictionary<string, BasisDocument>(documents) };
    }

    /// <summary>
    /// Runs <paramref name="action"/> only while <paramref name="expected"/> is still the case's current basis.
    /// The check and the action run under the same lock that guards <see cref="SetCurrentBasis"/>, so a basis
    /// cannot be superseded between them. Returns false, without running the action, when the basis is stale.
    /// </summary>
    public bool RunIfCurrent(string caseId, AssessmentBasis expected, Action action)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            if (!_currentByCase.TryGetValue(caseId, out var latest) || !ReferenceEquals(latest, expected))
            {
                return false;
            }

            action();
            return true;
        }
    }

    public AssessmentBasis? CurrentBasis(string caseId)
    {
        lock (_gate)
        {
            return _currentByCase.TryGetValue(caseId, out var basis) ? basis : null;
        }
    }

    public void Append(AssessmentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            if (!_supplementalByCase.TryGetValue(record.CaseId, out var records))
            {
                records = [];
                _supplementalByCase[record.CaseId] = records;
            }

            records.Add(record);
        }
    }

    public IReadOnlyList<AssessmentRecord> List(string caseId)
    {
        if (_caseStates is not null)
        {
            var owner = _caseStates.OwnerOf(caseId);
            if (owner is null) return Array.Empty<AssessmentRecord>();
            var scope = _caseStates.AuthorizeCase(caseId, owner);
            var persisted = _caseStates.ListAssessments(scope).Select(ToAssessmentRecord).ToList();
            lock (_gate)
            {
                if (_supplementalByCase.TryGetValue(caseId, out var supplemental)) persisted.AddRange(supplemental);
            }
            return persisted;
        }

        lock (_gate)
        {
            return _supplementalByCase.TryGetValue(caseId, out var records) ? records.ToArray() : Array.Empty<AssessmentRecord>();
        }
    }

    private static AssessmentRecord ToAssessmentRecord(CaseAssessment assessment) =>
        new(
            assessment.CaseId,
            assessment.BasisId,
            assessment.BasisVersion,
            assessment.Outcome,
            assessment.Reason,
            assessment.Claims,
            assessment.RequestId,
            assessment.WorkItemId,
            assessment.AssessmentId);
}
