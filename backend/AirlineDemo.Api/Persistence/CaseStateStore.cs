using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AirlineDemo.Api.Persistence;

public sealed record CaseState(string CaseId, int Revision, string Status, string? CurrentBasisDigest);

public sealed record BasisVersion(string CaseId, int Version, string ContentDigest, string BasisDigest, string Content);

public sealed record AuditEvent(long Sequence, string CaseId, string Actor, string Action, string Detail);

public sealed record CaseReceipt(string ReceiptId, string CaseId, int Revision, string ContentDigest, string BasisDigest);

public sealed record BasisSubmission(string ReceiptId, string Content, string ContentDigest, string BasisDigest);

public sealed record CaseItem(string CaseId, string Kind, string ItemId, string Detail);

public sealed record SignedSnapshot(string Payload, string Mac);

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
    private readonly byte[] _integrityKey;
    private Ledger _ledger = Ledger.Seed();

    // The integrity key is server-held; exported snapshots are MAC-signed so an edited snapshot cannot be restored.
    public CaseStateStore(byte[] integrityKey, Action? faultInjection = null)
    {
        if (integrityKey is null || integrityKey.Length < 32)
            throw new ArgumentException("Integrity key must be at least 32 bytes.", nameof(integrityKey));
        _integrityKey = integrityKey;
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

    public IReadOnlyList<CaseItem> Items(string requestedCaseId, string authorizedCaseId, string kind)
    {
        if (requestedCaseId != authorizedCaseId) return [];
        lock (_gate) return _ledger.Items.Where(i => i.CaseId == authorizedCaseId && i.Kind == kind).ToList();
    }

    // Findings and requests are append-only per case; resubmitting the same item is a replay, never a duplicate.
    public WriteResult RecordItem(string authorizedCaseId, int expectedRevision, string actor, string kind, string itemId, string detail)
    {
        lock (_gate)
        {
            if (!_ledger.Cases.TryGetValue(authorizedCaseId, out var current))
                return new(CaseWriteOutcome.NotFound, null, null);
            if (kind is not ("finding" or "request") || string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(detail))
                return new(CaseWriteOutcome.Invalid, null, null);

            var existing = _ledger.Items.FirstOrDefault(i => i.CaseId == authorizedCaseId && i.Kind == kind && i.ItemId == itemId);
            if (existing is not null)
                return existing.Detail == detail
                    ? new(CaseWriteOutcome.Replayed, current, null)
                    : new(CaseWriteOutcome.ReceiptConflict, null, null);
            if (current.Revision != expectedRevision)
                return new(CaseWriteOutcome.Stale, null, null);

            return Transact(next =>
            {
                next.Items.Add(new CaseItem(authorizedCaseId, kind, itemId, detail));
                var updated = current with { Revision = current.Revision + 1 };
                next.Cases[authorizedCaseId] = updated;
                next.Audit.Add(new AuditEvent(next.Audit.Count + 1, authorizedCaseId, actor, "item-" + kind, itemId));
                return new WriteResult(CaseWriteOutcome.Committed, updated, null);
            });
        }
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
                    && existing.ContentDigest == s.ContentDigest
                    && _ledger.Basis.Any(b => b.CaseId == authorizedCaseId
                        && b.BasisDigest == s.BasisDigest
                        && b.ContentDigest == s.ContentDigest
                        && BasisDigestFor(b.CaseId, b.Version, b.ContentDigest) == s.BasisDigest);
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
        lock (_gate)
        {
            var payload = JsonSerializer.Serialize(_ledger);
            return JsonSerializer.Serialize(new SignedSnapshot(payload, Mac(payload)));
        }
    }

    private string Mac(string payload) =>
        Convert.ToHexString(HMACSHA256.HashData(_integrityKey, Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    // Audit grants for the app identity: appends go through the approved procedure only; UPDATE and DELETE are denied.
    public const string AuditGrantScript =
        "REVOKE UPDATE, DELETE ON case_audit FROM app_identity;\n" +
        "GRANT EXECUTE ON PROCEDURE append_case_audit TO app_identity;";

    public int AuditEventCount
    {
        get { lock (_gate) return _ledger.Audit.Count; }
    }

    // Replaces state only when the saved document fully verifies and extends the committed history; otherwise throws
    // and keeps the current state. Restore can never shorten or rewrite audit events, basis versions, or receipts.
    // minimumAuditEvents is the audit count recorded outside the snapshot; an older signed snapshot below it is a rollback.
    public void Restore(string json, int minimumAuditEvents = 0)
    {
        Ledger? loaded;
        try
        {
            var envelope = JsonSerializer.Deserialize<SignedSnapshot>(json);
            if (envelope?.Payload is null || envelope.Mac is null)
                throw new CaseStateRestoreException("Saved case state is missing.");
            var expected = Encoding.UTF8.GetBytes(Mac(envelope.Payload));
            if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(envelope.Mac)))
                throw new CaseStateRestoreException("Saved case state failed its integrity check.");
            loaded = JsonSerializer.Deserialize<Ledger>(envelope.Payload);
        }
        catch (JsonException ex)
        {
            throw new CaseStateRestoreException("Saved case state is unreadable.", ex);
        }

        if (loaded is null) throw new CaseStateRestoreException("Saved case state is missing.");
        Verify(loaded);
        if (loaded.Audit.Count < minimumAuditEvents)
            throw new CaseStateRestoreException("Saved case state is older than the last recorded state.");

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
        if (loaded.Items.Count < current.Items.Count || !loaded.Items.Take(current.Items.Count).SequenceEqual(current.Items))
            throw new CaseStateRestoreException("Saved items do not extend committed items.");
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
        if (l.Cases is null || l.Basis is null || l.Audit is null || l.Receipts is null || l.Items is null)
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

        // Each committed mutation writes exactly one audit event, so every saved state must be explained by its history.
        foreach (var (id, c) in l.Cases)
        {
            if (c.Revision - 1 != l.Audit.Count(a => a.CaseId == id))
                throw new CaseStateRestoreException($"Saved case {id} revision does not match its audit history.");

            var basis = l.Basis.Where(b => b.CaseId == id).OrderBy(b => b.Version).ToList();
            if (basis.Count != l.Audit.Count(a => a.CaseId == id && a.Action == "basis-added"))
                throw new CaseStateRestoreException($"Saved basis for {id} does not match its audit history.");
            if (c.CurrentBasisDigest != (basis.Count == 0 ? null : basis[^1].BasisDigest))
                throw new CaseStateRestoreException($"Saved current basis for {id} does not match basis history.");

            var lastDecision = l.Audit.LastOrDefault(a => a.CaseId == id && a.Action.StartsWith("decision-", StringComparison.Ordinal));
            var expectedStatus = lastDecision?.Action switch
            {
                "decision-accept" => "accepted",
                "decision-reject" => "rejected",
                _ => "awaiting-review",
            };
            if (c.Status != expectedStatus)
                throw new CaseStateRestoreException($"Saved status for {id} does not match its decision history.");
        }

        foreach (var audit in l.Audit)
            if (!l.Cases.ContainsKey(audit.CaseId))
                throw new CaseStateRestoreException($"Saved audit belongs to unknown case {audit.CaseId}.");

        foreach (var item in l.Items)
        {
            if (!l.Cases.ContainsKey(item.CaseId) || item.Kind is not ("finding" or "request"))
                throw new CaseStateRestoreException($"Saved item for {item.CaseId} is invalid.");
            if (!l.Audit.Any(a => a.CaseId == item.CaseId && a.Action == "item-" + item.Kind && a.Detail == item.ItemId))
                throw new CaseStateRestoreException($"Saved item {item.ItemId} has no audit event.");
        }

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
        public List<CaseItem> Items { get; set; } = new();

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
            Items = new(Items),
        };
    }
}
