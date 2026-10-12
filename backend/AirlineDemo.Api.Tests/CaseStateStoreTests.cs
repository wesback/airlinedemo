using System.Reflection;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Tests;

public sealed class CaseStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "case-state-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string PathFor(string name = "cases.json") => Path.Combine(_dir, name);

    private static CaseCommit BasisCommit(BasisVersion basis, string? receipt = null) =>
        new(basis, receipt, "presenter", "submit-package", "basis " + basis.Version);

    [Fact]
    public void Read_results_cannot_be_mutated_outside_commit()
    {
        var store = new CaseStateStore(PathFor());
        var scope = store.CreateCase("case-a");
        store.Commit(scope, 0, new CaseCommit(null, "receipt-1", "presenter", "open", "opened"));

        var state = store.Read(scope);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)state.ReceiptIds).Add("forged"));
        Assert.Throws<NotSupportedException>(() => ((IList<AuditEvent>)state.Audit).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)state.ReceiptFingerprints).Remove("receipt-1"));

        var audit = store.ReadAudit(scope);
        Assert.Throws<NotSupportedException>(() => ((IList<AuditEvent>)audit).Clear());
        Assert.Single(store.ReadAudit(scope));
        Assert.Equal(["receipt-1"], store.Read(scope).ReceiptIds);
    }

    [Fact]
    public void Reused_receipt_with_different_payload_is_rejected_without_mutation()
    {
        var store = new CaseStateStore(PathFor());
        var scope = store.CreateCase("case-a");
        store.Commit(scope, 0, new CaseCommit(null, "receipt-1", "reviewer", "decide", "approve"));

        Assert.Throws<ReceiptConflictException>(() =>
            store.Commit(scope, 1, new CaseCommit(null, "receipt-1", "reviewer", "decide", "reject")));

        Assert.Equal(1, store.Read(scope).Revision);
        Assert.Single(store.ReadAudit(scope));
    }

    // AC coverage: M-02 (scope and revision isolation), M-10 (immutable basis versions and digests),
    // M-03 (append-only audit, atomic commit), restart and restore.

    [Fact]
    public void M02_forged_or_unknown_case_id_is_denied_and_reveals_nothing()
    {
        var store = new CaseStateStore(PathFor());
        store.CreateCase("case-a");

        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("case-b"));
        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase(""));
    }

    [Fact]
    public void M02_stale_revision_is_rejected_without_mutation()
    {
        var store = new CaseStateStore(PathFor());
        var scope = store.CreateCase("case-a");
        store.Commit(scope, 0, new CaseCommit(null, null, "presenter", "open", "opened"));

        var before = store.Read(scope);
        Assert.Throws<RevisionConflictException>(() =>
            store.Commit(scope, 0, new CaseCommit(null, "r-1", "presenter", "stale", "stale")));

        var after = store.Read(scope);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Audit.Count, after.Audit.Count);
        Assert.Empty(after.ReceiptIds);
    }

    [Fact]
    public void M10_basis_versions_are_immutable_and_digest_checked()
    {
        var store = new CaseStateStore(PathFor());
        var scope = store.CreateCase("case-a");
        var v1 = CaseStateStore.CreateBasisVersion(1, "evidence v1", string.Empty);
        store.Commit(scope, 0, BasisCommit(v1));

        var overwrite = CaseStateStore.CreateBasisVersion(1, "tampered", string.Empty);
        Assert.Throws<BasisIntegrityException>(() => store.Commit(scope, 1, BasisCommit(overwrite)));

        var mismatched = v1 with { Content = "changed after digest" };
        var v2 = CaseStateStore.CreateBasisVersion(2, "evidence v2", v1.BasisDigest);
        Assert.Throws<BasisIntegrityException>(() =>
            store.Commit(scope, 1, BasisCommit(v2 with { ContentDigest = mismatched.ContentDigest })));

        Assert.Equal("evidence v1", store.GetBasisVersion(scope, 1).Content);
        Assert.Equal(1, store.Read(scope).Revision);
    }

    [Fact]
    public void M10_prior_basis_versions_remain_retrievable_for_the_authorized_case_only()
    {
        var store = new CaseStateStore(PathFor());
        var a = store.CreateCase("case-a");
        var b = store.CreateCase("case-b");
        var v1 = CaseStateStore.CreateBasisVersion(1, "a-evidence", string.Empty);
        store.Commit(a, 0, BasisCommit(v1));

        Assert.Equal(v1.ContentDigest, store.GetBasisVersion(a, 1).ContentDigest);
        Assert.Throws<KeyNotFoundException>(() => store.GetBasisVersion(b, 1));
    }

    [Fact]
    public void M03_audit_api_is_append_only_with_no_update_or_delete_operation()
    {
        var mutators = typeof(CaseStateStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .Where(n => n.StartsWith("Update", StringComparison.Ordinal)
                || n.StartsWith("Delete", StringComparison.Ordinal)
                || n.StartsWith("Remove", StringComparison.Ordinal)
                || n.StartsWith("Set", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(mutators);
        Assert.DoesNotContain(typeof(CaseStateStore).GetMethods(), m => m.ReturnType == typeof(void) && m.Name.Contains("Audit"));
    }

    [Fact]
    public void M03_each_commit_appends_one_audit_event_and_replayed_receipt_adds_none()
    {
        var store = new CaseStateStore(PathFor());
        var scope = store.CreateCase("case-a");

        var first = store.Commit(scope, 0, new CaseCommit(null, "receipt-1", "reviewer", "decide", "approve"));
        Assert.False(first.Replayed);
        Assert.Single(store.ReadAudit(scope));

        var replay = store.Commit(scope, 0, new CaseCommit(null, "receipt-1", "reviewer", "decide", "approve"));
        Assert.True(replay.Replayed);
        Assert.Equal(1, replay.State.Revision);
        Assert.Single(store.ReadAudit(scope));
        Assert.Equal(["receipt-1"], store.Read(scope).ReceiptIds);
    }

    [Fact]
    public void M03_injected_persist_failure_leaves_no_partial_state_receipt_or_audit()
    {
        var armed = false;
        var store = new CaseStateStore(PathFor(), beforePersist: () =>
        {
            if (armed)
            {
                armed = false;
                throw new IOException("injected transaction failure");
            }
        });
        var scope = store.CreateCase("case-a");
        armed = true;
        var basis = CaseStateStore.CreateBasisVersion(1, "evidence", string.Empty);

        Assert.Throws<IOException>(() => store.Commit(scope, 0, new CaseCommit(basis, "receipt-1", "presenter", "submit", "x")));

        var state = store.Read(scope);
        Assert.Equal(0, state.Revision);
        Assert.Empty(state.Basis);
        Assert.Empty(state.ReceiptIds);
        Assert.Empty(state.Audit);

        var retry = store.Commit(scope, 0, new CaseCommit(basis, "receipt-1", "presenter", "submit", "x"));
        Assert.False(retry.Replayed);
        Assert.Single(store.ReadAudit(scope));
    }

    [Fact]
    public void M03_restart_restores_case_state_basis_receipts_and_audit_without_duplicating_actions()
    {
        var path = PathFor();
        var store = new CaseStateStore(path);
        var scope = store.CreateCase("case-a");
        var v1 = CaseStateStore.CreateBasisVersion(1, "evidence", string.Empty);
        store.Commit(scope, 0, BasisCommit(v1, "receipt-1"));

        var restarted = new CaseStateStore(path);
        var restoredScope = restarted.AuthorizeCase("case-a");
        var state = restarted.Read(restoredScope);

        Assert.Equal(1, state.Revision);
        Assert.Equal(v1.BasisDigest, state.Basis[0].BasisDigest);
        Assert.Equal(["receipt-1"], state.ReceiptIds);
        Assert.Single(state.Audit);

        var replay = restarted.Commit(restoredScope, 1, BasisCommit(v1, "receipt-1"));
        Assert.True(replay.Replayed);
        Assert.Single(restarted.ReadAudit(restoredScope));
    }

    [Fact]
    public void Unreadable_state_file_fails_restore_instead_of_returning_empty_state()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathFor(), "{ not json");

        Assert.Throws<InvalidDataException>(() => new CaseStateStore(PathFor()));
    }

    [Fact]
    public void Tampered_audit_history_fails_restore()
    {
        var path = PathFor();
        var store = new CaseStateStore(path);
        var scope = store.CreateCase("case-a");
        store.Commit(scope, 0, new CaseCommit(null, null, "presenter", "open", "opened"));

        File.WriteAllText(path, File.ReadAllText(path).Replace("\"revision\": 1", "\"revision\": 5"));

        Assert.Throws<InvalidDataException>(() => new CaseStateStore(path));
    }
}
