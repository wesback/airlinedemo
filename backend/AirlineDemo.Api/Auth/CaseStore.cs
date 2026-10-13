namespace AirlineDemo.Api.Auth;

public sealed record CaseRecord(string Id, int Revision, string Status);

public enum ReviewOutcome { Recorded, NotFound, Stale }

public class CaseStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CaseRecord> _cases = new()
    {
        ["case-001"] = new("case-001", 1, "awaiting-review"),
        ["case-002"] = new("case-002", 1, "awaiting-review"),
        ["case-003"] = new("case-003", 1, "awaiting-review"),
    };

    public CaseRecord? Find(string id)
    {
        lock (_gate) return _cases.GetValueOrDefault(id);
    }

    public virtual ReviewOutcome RecordReview(string id, int expectedRevision, string decision, out CaseRecord? updated)
    {
        lock (_gate)
        {
            updated = null;
            if (!_cases.TryGetValue(id, out var current)) return ReviewOutcome.NotFound;
            if (current.Revision != expectedRevision) return ReviewOutcome.Stale;

            updated = current with { Revision = current.Revision + 1, Status = decision == "accept" ? "accepted" : "rejected" };
            _cases[id] = updated;
            return ReviewOutcome.Recorded;
        }
    }
}
