namespace AirlineDemo.Api.Investigation;

/// <summary>Retains assessment records per case. Records are append-only and returned as copies.</summary>
public sealed class AssessmentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<AssessmentRecord>> _byCase = new(StringComparer.Ordinal);

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
