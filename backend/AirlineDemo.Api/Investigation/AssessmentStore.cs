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
