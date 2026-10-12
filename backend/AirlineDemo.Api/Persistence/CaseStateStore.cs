using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AirlineDemo.Api.Persistence;

public sealed record CaseState(string CaseId, int Revision, string Status, string? CurrentBasisDigest);

public sealed record BasisVersion(string CaseId, int Version, string ContentDigest, string BasisDigest, string Content);

public sealed record AuditEvent(long Sequence, string CaseId, string Actor, string Action, string Detail);

public sealed record CaseReceipt(string ReceiptId, string CaseId, int Revision, string ContentDigest, string BasisDigest);

public sealed record BasisSubmission(string ReceiptId, string Content, string ContentDigest, string BasisDigest);

public sealed record WriteResult(CaseWriteOutcome Outcome, CaseState? Case, CaseReceipt? Receipt);

public enum CaseWriteOutcome { Committed, Replayed, NotFound, Stale, DigestMismatch, ReceiptConflict, Invalid }

public sealed class CaseStateRestoreException(string message, Exception? inner = null) : Exception(message, inner);

// In-process case ledger. Every mutation goes through Transact, which applies changes to a copy and swaps it in
// only after the whole change succeeds, so a failure leaves no partial basis, receipt, or audit event behind.
// The audit trail is exposed append-only: there is no public method that updates or removes audit events.
public sealed class CaseStateStore
{
    private readonly Lock _gate = new();
    private readonly Action? _faultInjection;
    private Ledger _ledger = Ledger.Seed();

    public CaseStateStore(Action? faultInjection = null)
    {
        _faultInjection = faultInjection;
    }

    public static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static string BasisDigestFor(string caseId, int version, string contentDigest) =>
        Sha256($"{caseId}|{version}|{contentDigest}");

    // requestedCaseId is caller-supplied; authorizedCaseId is the server-derived scope. Mismatches reveal nothing.
    public CaseState? Read(string requestedCaseId, string authorizedCaseId)
    {
        if (requestedCaseId != authorizedCaseId) return null;
        lock (_gate) return _ledger.Cases.GetValueOrDefault(authorizedCaseId);
    }

    public BasisVersion? GetBasis(string requestedCaseId, string authorizedCaseId, int version)
    {
        if (requestedCaseId != authorizedCaseId) return null;
        lock (_gate) return _ledger.Basis.FirstOrDefault(b => b.CaseId == authorizedCaseId && b.Version == version);
    }

    public IReadOnlyList<AuditEvent> Audit(string requestedCaseId, string authorizedCaseId)
    {
        if (requestedCaseId != authorizedCaseId) return [];
        lock (_gate) return _ledger.Audit.Where(a => a.CaseId == authorizedCaseId).ToList();
    }

    public WriteResult AddBasis(string authorizedCaseId, int expectedRevision, string actor, BasisSubmission s)
    {
        if (Sha256(s.Content) != s.ContentDigest) return new(CaseWriteOutcome.DigestMismatch, null, null);

        lock (_gate)
        {
            if (_ledger.Receipts.TryGetValue(s.ReceiptId, out var existing))
            {
                var sameRequest = existing.CaseId == authorizedCaseId
                    && existing.BasisDigest == s.BasisDigest
                    && existing.ContentDigest == s.ContentDigest;
                return sameRequest
                    ? new(CaseWriteOutcome.Replayed, _ledger.Cases[existing.CaseId], existing)
                    : new(CaseWriteOutcome.ReceiptConflict, null, null);
            }

            if (!_ledger.Cases.TryGetValue(authorizedCaseId, out var current))
                return new(CaseWriteOutcome.NotFound, null, null);
            if (current.Revision != expectedRevision)
                return new(CaseWriteOutcome.Stale, null, null);

            var version = _ledger.Basis.Count(b => b.CaseId == authorizedCaseId) + 1;
            if (BasisDigestFor(authorizedCaseId, version, s.ContentDigest) != s.BasisDigest)
                return new(CaseWriteOutcome.DigestMismatch, null, null);

            return Transact(next =>
            {
                next.Basis.Add(new BasisVersion(authorizedCaseId, version, s.ContentDigest, s.BasisDigest, s.Content));
                var updated = current with { Revision = current.Revision + 1, CurrentBasisDigest = s.BasisDigest };
                next.Cases[authorizedCaseId] = updated;
                next.Audit.Add(new AuditEvent(next.Audit.Count + 1, authorizedCaseId, actor, "basis-added", $"version {version}"));
                var receipt = new CaseReceipt(s.ReceiptId, authorizedCaseId, updated.Revision, s.ContentDigest, s.BasisDigest);
                next.Receipts[s.ReceiptId] = receipt;
                return new WriteResult(CaseWriteOutcome.Committed, updated, receipt);
            });
        }
    }

    // A decision is bound to the current basis digest; it is rejected when no basis exists yet.
    public WriteResult RecordDecision(string authorizedCaseId, int expectedRevision, string actor, string decision, string reason)
    {
        lock (_gate)
        {
            if (!_ledger.Cases.TryGetValue(authorizedCaseId, out var current))
                return new(CaseWriteOutcome.NotFound, null, null);
            if (current.Revision != expectedRevision)
                return new(CaseWriteOutcome.Stale, null, null);
            if (decision is not ("accept" or "reject") || current.CurrentBasisDigest is null || string.IsNullOrWhiteSpace(reason))
                return new(CaseWriteOutcome.Invalid, null, null);

            return Transact(next =>
            {
                var updated = current with { Revision = current.Revision + 1, Status = decision == "accept" ? "accepted" : "rejected" };
                next.Cases[authorizedCaseId] = updated;
                next.Audit.Add(new AuditEvent(next.Audit.Count + 1, authorizedCaseId, actor, "decision-" + decision,
                    $"basis {current.CurrentBasisDigest}: {reason}"));
                return new WriteResult(CaseWriteOutcome.Committed, updated, null);
            });
        }
    }

    public string Export()
    {
        lock (_gate) return JsonSerializer.Serialize(_ledger);
    }

    // Replaces state only when the saved document fully verifies and extends the committed history; otherwise throws
    // and keeps the current state. Restore can never shorten or rewrite audit events, basis versions, or receipts.
    public void Restore(string json)
    {
        Ledger? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<Ledger>(json);
        }
        catch (JsonException ex)
        {
            throw new CaseStateRestoreException("Saved case state is unreadable.", ex);
        }

        if (loaded is null) throw new CaseStateRestoreException("Saved case state is missing.");
        Verify(loaded);

        lock (_gate)
        {
            EnsureExtends(_ledger, loaded);
            _ledger = loaded;
        }
    }

    private static void EnsureExtends(Ledger current, Ledger loaded)
    {
        if (loaded.Audit.Count < current.Audit.Count || !loaded.Audit.Take(current.Audit.Count).SequenceEqual(current.Audit))
            throw new CaseStateRestoreException("Saved audit history does not extend committed history.");
        if (loaded.Basis.Count < current.Basis.Count || !loaded.Basis.Take(current.Basis.Count).SequenceEqual(current.Basis))
            throw new CaseStateRestoreException("Saved basis history does not extend committed history.");
        foreach (var (receiptId, receipt) in current.Receipts)
            if (!loaded.Receipts.TryGetValue(receiptId, out var saved) || saved != receipt)
                throw new CaseStateRestoreException("Saved receipts do not extend committed receipts.");
        foreach (var (caseId, state) in current.Cases)
            if (!loaded.Cases.TryGetValue(caseId, out var saved) || saved.Revision < state.Revision)
                throw new CaseStateRestoreException($"Saved case {caseId} revision regresses.");
    }

    private WriteResult Transact(Func<Ledger, WriteResult> change)
    {
        var next = _ledger.Clone();
        var result = change(next);
        _faultInjection?.Invoke();
        _ledger = next;
        return result;
    }

    private static void Verify(Ledger l)
    {
        if (l.Cases is null || l.Basis is null || l.Audit is null || l.Receipts is null)
            throw new CaseStateRestoreException("Saved case state is incomplete.");
        foreach (var id in Ledger.SeededCaseIds)
            if (!l.Cases.ContainsKey(id)) throw new CaseStateRestoreException($"Saved case state is missing {id}.");

        foreach (var (key, c) in l.Cases)
            if (key != c.CaseId) throw new CaseStateRestoreException($"Saved case {key} is mislabelled.");

        foreach (var group in l.Basis.GroupBy(b => b.CaseId))
        {
            if (!l.Cases.ContainsKey(group.Key))
                throw new CaseStateRestoreException($"Saved basis belongs to unknown case {group.Key}.");
            var expectedVersion = 1;
            foreach (var b in group)
            {
                if (b.Version != expectedVersion
                    || Sha256(b.Content) != b.ContentDigest
                    || BasisDigestFor(b.CaseId, b.Version, b.ContentDigest) != b.BasisDigest)
                    throw new CaseStateRestoreException($"Saved basis version {b.Version} for {group.Key} fails verification.");
                expectedVersion++;
            }
        }

        for (var i = 0; i < l.Audit.Count; i++)
            if (l.Audit[i].Sequence != i + 1) throw new CaseStateRestoreException("Saved audit sequence is not contiguous.");

        foreach (var receipt in l.Receipts.Values)
            if (!l.Cases.ContainsKey(receipt.CaseId))
                throw new CaseStateRestoreException($"Saved receipt belongs to unknown case {receipt.CaseId}.");
    }

    internal sealed class Ledger
    {
        public Dictionary<string, CaseState> Cases { get; set; } = new();
        public List<BasisVersion> Basis { get; set; } = new();
        public List<AuditEvent> Audit { get; set; } = new();
        public Dictionary<string, CaseReceipt> Receipts { get; set; } = new();

        public static readonly string[] SeededCaseIds = ["case-001", "case-002", "case-003"];

        public static Ledger Seed()
        {
            var ledger = new Ledger();
            foreach (var id in SeededCaseIds)
                ledger.Cases[id] = new CaseState(id, 1, "awaiting-review", null);
            return ledger;
        }

        public Ledger Clone() => new()
        {
            Cases = new(Cases),
            Basis = new(Basis),
            Audit = new(Audit),
            Receipts = new(Receipts),
        };
    }
}
