using System.Reflection;
using System.Text.Json;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Tests;

public class CaseStateStoreTests
{
    private const string Content = "approved-records-v1";
    private static readonly byte[] Key = Enumerable.Repeat((byte)7, 32).ToArray();
    private static readonly byte[] OtherKey = Enumerable.Repeat((byte)9, 32).ToArray();

    private static CaseStateStore NewStore(Action? fault = null) => new(Key, fault);

    private static BasisSubmission Submit(string receiptId, string caseId, int version, string content)
    {
        var contentDigest = CaseStateStore.Sha256(content);
        return new BasisSubmission(receiptId, content, contentDigest,
            CaseStateStore.BasisDigestFor(caseId, version, contentDigest));
    }

    [Fact]
    public void Read_ForgedCaseId_RevealsNothing()
    {
        var store = NewStore();
        store.AddBasis("case-001", 1, "presenter", Submit("r-1", "case-001", 1, Content));

        Assert.NotNull(store.Read("case-001", "case-001"));
        Assert.Null(store.Read("case-002", "case-001"));
        Assert.Null(store.GetBasis("case-002", "case-001", 1));
        Assert.Empty(store.Audit("case-002", "case-001"));
        Assert.Empty(store.Items("case-002", "case-001", "finding"));
    }

    [Fact]
    public void AddBasis_StaleRevisionOrOutOfScope_MakesNoMutation()
    {
        var store = NewStore();

        var stale = store.AddBasis("case-001", 0, "presenter", Submit("r-1", "case-001", 1, Content));
        var missing = store.AddBasis("case-999", 1, "presenter", Submit("r-2", "case-999", 1, Content));

        Assert.Equal(CaseWriteOutcome.Stale, stale.Outcome);
        Assert.Equal(CaseWriteOutcome.NotFound, missing.Outcome);
        Assert.Equal(1, store.Read("case-001", "case-001")!.Revision);
        Assert.Null(store.GetBasis("case-001", "case-001", 1));
        Assert.Empty(store.Audit("case-001", "case-001"));
    }

    [Fact]
    public void RecordItem_StaleOrUnknownCase_MakesNoMutation()
    {
        var store = NewStore();

        var stale = store.RecordItem("case-001", 0, "reviewer", "finding", "F-1", "identity ambiguous");
        var missing = store.RecordItem("case-999", 1, "reviewer", "finding", "F-1", "identity ambiguous");

        Assert.Equal(CaseWriteOutcome.Stale, stale.Outcome);
        Assert.Equal(CaseWriteOutcome.NotFound, missing.Outcome);
        Assert.Empty(store.Items("case-001", "case-001", "finding"));
        Assert.Empty(store.Audit("case-001", "case-001"));
    }

    [Fact]
    public void RecordItem_ResubmittedSameItem_IsReplayAndDifferentDetailConflicts()
    {
        var store = NewStore();
        store.RecordItem("case-001", 1, "reviewer", "request", "Q-1", "passport scan");

        var replay = store.RecordItem("case-001", 2, "reviewer", "request", "Q-1", "passport scan");
        var conflict = store.RecordItem("case-001", 2, "reviewer", "request", "Q-1", "other");

        Assert.Equal(CaseWriteOutcome.Replayed, replay.Outcome);
        Assert.Equal(CaseWriteOutcome.ReceiptConflict, conflict.Outcome);
        Assert.Equal(2, store.Read("case-001", "case-001")!.Revision);
        Assert.Single(store.Items("case-001", "case-001", "request"));
        Assert.Single(store.Audit("case-001", "case-001"));
    }

    [Fact]
    public void AddBasis_MismatchedDigests_AreRejected()
    {
        var store = NewStore();
        var good = Submit("r-1", "case-001", 1, Content);

        var badContent = store.AddBasis("case-001", 1, "presenter", good with { Content = "tampered" });
        var badBasis = store.AddBasis("case-001", 1, "presenter", good with { BasisDigest = "00" });

        Assert.Equal(CaseWriteOutcome.DigestMismatch, badContent.Outcome);
        Assert.Equal(CaseWriteOutcome.DigestMismatch, badBasis.Outcome);
        Assert.Equal(1, store.Read("case-001", "case-001")!.Revision);
        Assert.Null(store.GetBasis("case-001", "case-001", 1));
    }

    [Fact]
    public void Basis_PriorVersionsStayImmutableAndScoped()
    {
        var store = NewStore();
        var v1 = Submit("r-1", "case-001", 1, Content);
        store.AddBasis("case-001", 1, "presenter", v1);
        store.AddBasis("case-001", 2, "presenter", Submit("r-2", "case-001", 2, "new-evidence-v2"));

        var retrieved = store.GetBasis("case-001", "case-001", 1);
        Assert.Equal(Content, retrieved!.Content);
        Assert.Equal(v1.BasisDigest, retrieved.BasisDigest);
        Assert.Null(store.GetBasis("case-001", "case-002", 1));

        var replay = store.AddBasis("case-001", 1, "presenter", v1);
        Assert.Equal(CaseWriteOutcome.Replayed, replay.Outcome);
        Assert.Equal(3, store.Read("case-001", "case-001")!.Revision);

        // A replay whose content no longer matches its declared digest is rejected before receipt lookup.
        var changedBody = store.AddBasis("case-001", 3, "presenter", v1 with { Content = Content + "-x" });
        Assert.Equal(CaseWriteOutcome.DigestMismatch, changedBody.Outcome);

        var forgedDigest = store.AddBasis("case-001", 3, "presenter", v1 with { ContentDigest = "00" });
        Assert.Equal(CaseWriteOutcome.DigestMismatch, forgedDigest.Outcome);
        Assert.Equal(3, store.Read("case-001", "case-001")!.Revision);

        var otherBody = store.AddBasis("case-001", 3, "presenter", v1 with { ReceiptId = "r-1", Content = "other", ContentDigest = CaseStateStore.Sha256("other") });
        Assert.Equal(CaseWriteOutcome.ReceiptConflict, otherBody.Outcome);
    }

    [Fact]
    public void ReceiptReplay_WithForgedBasisDigest_IsRejectedWithoutMutation()
    {
        var store = NewStore();
        var submission = Submit("r-1", "case-001", 1, Content);
        store.AddBasis("case-001", 1, "presenter", submission);
        var before = store.Audit("case-001", "case-001").Count;

        var forged = submission with { BasisDigest = CaseStateStore.Sha256("forged") };
        var result = store.AddBasis("case-001", 1, "presenter", forged);

        Assert.NotEqual(CaseWriteOutcome.Replayed, result.Outcome);
        Assert.Equal(before, store.Audit("case-001", "case-001").Count);
    }

    [Fact]
    public void Restore_RejectsOlderSnapshotBelowAnchoredAuditCount()
    {
        var store = NewStore();
        store.AddBasis("case-001", 1, "presenter", Submit("r-1", "case-001", 1, Content));
        var older = store.Export();
        store.RecordDecision("case-001", 2, "reviewer", "accept", "basis matches records");
        var newer = store.Export();
        var anchor = store.AuditEventCount;

        var restarted = NewStore();
        Assert.Throws<CaseStateRestoreException>(() => restarted.Restore(older, anchor));
        Assert.Equal(0, restarted.AuditEventCount);

        restarted.Restore(newer, anchor);
        Assert.Equal(anchor, restarted.AuditEventCount);
    }

    [Fact]
    public void AuditGrants_AllowOnlyApprovedAppendAndDenyUpdateDelete()
    {
        var grants = CaseStateStore.AuditGrantScript;

        Assert.Contains("GRANT EXECUTE ON PROCEDURE append_case_audit TO app_identity", grants);
        Assert.Contains("REVOKE UPDATE, DELETE ON case_audit FROM app_identity", grants);
        Assert.DoesNotContain("GRANT UPDATE", grants);
        Assert.DoesNotContain("GRANT DELETE", grants);
        Assert.DoesNotContain("GRANT ALL", grants);
    }

    [Fact]
    public void AuditTrail_IsAppendOnlyThroughPublicApi()
    {
        // In-process check only: the store exposes no update/delete path. Database grant enforcement (M-03) is not
        // covered here because the repository has no database layer.
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        var forbidden = new[] { "Update", "Delete", "Remove", "Clear", "Set", "Replace" };
        var methods = typeof(CaseStateStore).GetMethods(flags).Select(m => m.Name);
        Assert.DoesNotContain(methods, name => forbidden.Any(f => name.StartsWith(f, StringComparison.Ordinal)));

        var store = NewStore();
        store.AddBasis("case-001", 1, "presenter", Submit("r-1", "case-001", 1, Content));
        var before = store.Audit("case-001", "case-001").ToList();
        store.RecordDecision("case-001", 2, "reviewer", "accept", "basis matches records");
        var after = store.Audit("case-001", "case-001");

        Assert.Equal(before.Count + 1, after.Count);
        Assert.Equal(before, after.Take(before.Count));
        Assert.Equal(2, after[1].Sequence);
    }

    [Fact]
    public void Transaction_InjectedFailure_LeavesNoPartialState()
    {
        var fail = true;
        var store = NewStore(() =>
        {
            if (fail) throw new InvalidOperationException("injected");
        });
        var submission = Submit("r-1", "case-001", 1, Content);

        Assert.Throws<InvalidOperationException>(() => store.AddBasis("case-001", 1, "presenter", submission));

        Assert.Equal(1, store.Read("case-001", "case-001")!.Revision);
        Assert.Null(store.GetBasis("case-001", "case-001", 1));
        Assert.Empty(store.Audit("case-001", "case-001"));

        fail = false;
        var retry = store.AddBasis("case-001", 1, "presenter", submission);
        Assert.Equal(CaseWriteOutcome.Committed, retry.Outcome);
        Assert.Single(store.Audit("case-001", "case-001"));
    }

    [Fact]
    public void Restart_RestoresFindingsRequestsAndDecisionsWithoutDuplicates()
    {
        var first = NewStore();
        var submission = Submit("r-1", "case-001", 1, Content);
        first.AddBasis("case-001", 1, "presenter", submission);
        first.RecordItem("case-001", 2, "reviewer", "finding", "F-1", "identity ambiguous");
        first.RecordItem("case-001", 3, "reviewer", "request", "Q-1", "passport scan");
        first.RecordDecision("case-001", 4, "reviewer", "reject", "identity ambiguous");
        var saved = first.Export();

        var restarted = NewStore();
        restarted.Restore(saved);

        Assert.Equal(first.Read("case-001", "case-001"), restarted.Read("case-001", "case-001"));
        Assert.Equal(first.Audit("case-001", "case-001"), restarted.Audit("case-001", "case-001"));
        Assert.Equal(first.Items("case-001", "case-001", "finding"), restarted.Items("case-001", "case-001", "finding"));
        Assert.Equal(first.Items("case-001", "case-001", "request"), restarted.Items("case-001", "case-001", "request"));
        Assert.Equal("rejected", restarted.Read("case-001", "case-001")!.Status);

        var replayBasis = restarted.AddBasis("case-001", 5, "presenter", submission);
        var replayItem = restarted.RecordItem("case-001", 5, "reviewer", "finding", "F-1", "identity ambiguous");
        Assert.Equal(CaseWriteOutcome.Replayed, replayBasis.Outcome);
        Assert.Equal(CaseWriteOutcome.Replayed, replayItem.Outcome);
        Assert.Equal(5, restarted.Read("case-001", "case-001")!.Revision);
        Assert.Single(restarted.Items("case-001", "case-001", "finding"));
    }

    [Fact]
    public void Restore_EmptyOrTruncatedHistory_ThrowsAndKeepsAuditTrail()
    {
        var store = NewStore();
        store.AddBasis("case-001", 1, "presenter", Submit("r-1", "case-001", 1, Content));
        store.RecordDecision("case-001", 2, "reviewer", "accept", "basis matches records");
        var auditBefore = store.Audit("case-001", "case-001").ToList();

        var emptyLedger = NewStore().Export();
        Assert.Throws<CaseStateRestoreException>(() => store.Restore(emptyLedger));
        Assert.Throws<CaseStateRestoreException>(() => store.Restore("{\"Cases\":{},\"Basis\":[],\"Audit\":[],\"Receipts\":{}}"));

        Assert.Equal(auditBefore, store.Audit("case-001", "case-001"));
        Assert.Equal("accepted", store.Read("case-001", "case-001")!.Status);
        Assert.Equal(Content, store.GetBasis("case-001", "case-001", 1)!.Content);
    }

    [Fact]
    public void Restore_CorruptOrTamperedState_ThrowsAndKeepsCurrentState()
    {
        var store = NewStore();
        store.AddBasis("case-001", 1, "presenter", Submit("r-1", "case-001", 1, Content));
        var saved = store.Export();

        Assert.Throws<CaseStateRestoreException>(() => store.Restore("{not json"));
        Assert.Throws<CaseStateRestoreException>(() => store.Restore("null"));
        Assert.Throws<CaseStateRestoreException>(() =>
            store.Restore(saved.Replace(Content, "tampered-content")));

        Assert.Equal(2, store.Read("case-001", "case-001")!.Revision);
        Assert.Equal(Content, store.GetBasis("case-001", "case-001", 1)!.Content);
    }

    [Fact]
    public void Restore_ForgedSnapshotWithoutServerKey_IsRejectedAndForgedDecisionNotApplied()
    {
        var attacker = new CaseStateStore(OtherKey);
        attacker.AddBasis("case-001", 1, "presenter", Submit("r-1", "case-001", 1, Content));
        attacker.RecordDecision("case-001", 2, "reviewer", "accept", "forged");
        var forged = attacker.Export();

        var store = NewStore();
        store.AddBasis("case-001", 1, "presenter", Submit("r-1", "case-001", 1, Content));
        var auditBefore = store.Audit("case-001", "case-001").ToList();

        Assert.Throws<CaseStateRestoreException>(() => store.Restore(forged));

        Assert.Equal(2, store.Read("case-001", "case-001")!.Revision);
        Assert.Equal("awaiting-review", store.Read("case-001", "case-001")!.Status);
        Assert.Equal(auditBefore, store.Audit("case-001", "case-001"));
    }

    [Fact]
    public void Restore_MacRewrittenWithoutKey_IsRejected()
    {
        var store = NewStore();
        store.AddBasis("case-001", 1, "presenter", Submit("r-1", "case-001", 1, Content));
        using var doc = JsonDocument.Parse(store.Export());
        var payload = doc.RootElement.GetProperty("Payload").GetString()!;
        var edited = JsonSerializer.Serialize(new { Payload = payload, Mac = new string('0', 64) });

        Assert.Throws<CaseStateRestoreException>(() => NewStore().Restore(edited));
    }

    [Fact]
    public void Constructor_ShortKey_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new CaseStateStore(new byte[16]));
    }
}
