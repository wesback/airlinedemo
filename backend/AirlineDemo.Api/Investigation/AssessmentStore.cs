namespace AirlineDemo.Api.Investigation;

/// <summary>Retains assessment records per case. Records are append-only and returned as copies.</summary>
public sealed class AssessmentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<AssessmentRecord>> _byCase = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssessmentBasis> _currentByCase = new(StringComparer.Ordinal);

    /// <summary>Records the case's current accepted evidence basis. An assessment against any other basis is not current.</summary>
    public void SetCurrentBasis(string caseId, AssessmentBasis basis)
    {
        ArgumentNullException.ThrowIfNull(basis);
        lock (_gate)
        {
            _currentByCase[caseId] = basis;
        }
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
            if (!_byCase.TryGetValue(record.CaseId, out var records))
            {
                records = [];
                _byCase[record.CaseId] = records;
            }

            records.Add(record);
        }
    }

    public IReadOnlyList<AssessmentRecord> List(string caseId)
    {
        lock (_gate)
        {
            return _byCase.TryGetValue(caseId, out var records) ? records.ToArray() : Array.Empty<AssessmentRecord>();
        }
    }
}
