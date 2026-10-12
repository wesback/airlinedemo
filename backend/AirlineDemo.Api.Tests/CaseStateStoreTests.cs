using System.Reflection;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Tests;

public class CaseStateStoreTests
{
    private const string Content = "approved-records-v1";

    private static BasisSubmission Submit(string receiptId, string caseId, int version, string content)
    {
        var contentDigest = CaseStateStore.Sha256(content);
        return new BasisSubmission(receiptId, content, contentDigest,
            CaseStateStore.BasisDigestFor(caseId, version, contentDigest));
    }

    [Fact]
    public void Read_ForgedCaseId_RevealsNothing()
    {
        var store = new CaseStateStore();

        Assert.NotNull(store.Read("case-001", "case-001"));
        Assert.Null(store.Read("case-002", "case-001"));
        Assert.Null(store.GetBasis("case-002", "case-001", 1));
        Assert.Empty(store.Audit("case-002", "case-001"));
    }

    [Fact]
    public void AddBasis_StaleRevisionOrOutOfScope_MakesNoMutation()
    {
        var store = new CaseStateStore();

        var stale = store.AddBasis("case-001", 0, "presenter", Submit("r-1", "case-001", 1, Content));
        var missing = store.AddBasis("case-999", 1, "presenter", Submit("r-2", "case-999", 1, Content));

        Assert.Equal(CaseWriteOutcome.Stale, stale.Outcome);
        Assert.Equal(CaseWriteOutcome.NotFound, missing.Outcome);
        Assert.Equal(1, store.Read("case-001", "case-001")!.Revision);
        Assert.Null(store.GetBasis("case-001", "case-001", 1));
        Assert.Empty(store.Audit("case-001", "case-001"));
    }

    [Fact]
    public void AddBasis_MismatchedDigests_AreRejected()
    {
        var store = new CaseStateStore();
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
        var store = new CaseStateStore();
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

        var changedBody = store.AddBasis("case-001", 3, "presenter", v1 with { Content = Content + "-x" });
        Assert.Equal(CaseWriteOutcome.ReceiptConflict, changedBody.Outcome);
    }

    [Fact]
    public void AuditGrants_AreAppendOnly()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        var forbidden = new[] { "Update", "Delete", "Remove", "Clear", "Set", "Replace" };
        var methods = typeof(CaseStateStore).GetMethods(flags).Select(m => m.Name);

        Assert.DoesNotContain(methods, name => forbidden.Any(f => name.StartsWith(f, StringComparison.Ordinal)));

        var store = new CaseStateStore();
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
        var store = new CaseStateStore(() =>
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
        var first = new CaseStateStore();
        var submission = Submit("r-1", "case-001", 1, Content);
        first.AddBasis("case-001", 1, "presenter", submission);
        first.RecordDecision("case-001", 2, "reviewer", "reject", "identity ambiguous");
        var saved = first.Export();

        var restarted = new CaseStateStore();
        restarted.Restore(saved);

        Assert.Equal(first.Read("case-001", "case-001"), restarted.Read("case-001", "case-001"));
        Assert.Equal(first.Audit("case-001", "case-001"), restarted.Audit("case-001", "case-001"));
        Assert.Equal("rejected", restarted.Read("case-001", "case-001")!.Status);

        var replay = restarted.AddBasis("case-001", 3, "presenter", submission);
        Assert.Equal(CaseWriteOutcome.Replayed, replay.Outcome);
        Assert.Equal(3, restarted.Read("case-001", "case-001")!.Revision);
    }

    [Fact]
    public void Restore_CorruptOrTamperedState_ThrowsAndKeepsCurrentState()
    {
        var store = new CaseStateStore();
        store.AddBasis("case-001", 1, "presenter", Submit("r-1", "case-001", 1, Content));
        var saved = store.Export();

        Assert.Throws<CaseStateRestoreException>(() => store.Restore("{not json"));
        Assert.Throws<CaseStateRestoreException>(() => store.Restore("null"));
        Assert.Throws<CaseStateRestoreException>(() =>
            store.Restore(saved.Replace(Content, "tampered-content")));

        Assert.Equal(2, store.Read("case-001", "case-001")!.Revision);
        Assert.Equal(Content, store.GetBasis("case-001", "case-001", 1)!.Content);
    }
}
