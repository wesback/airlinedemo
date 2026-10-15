using System.Reflection;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Tests;

public sealed class CaseStateStoreTests : IDisposable
{
    private const string Owner = "owner-a";

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
        var scope = store.CreateCase("case-a", Owner);
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
        var scope = store.CreateCase("case-a", Owner);
        store.Commit(scope, 0, new CaseCommit(null, "receipt-1", "reviewer", "decide", "approve"));

        Assert.Throws<ReceiptConflictException>(() =>
            store.Commit(scope, 1, new CaseCommit(null, "receipt-1", "reviewer", "decide", "reject")));

        Assert.Equal(1, store.Read(scope).Revision);
        Assert.Single(store.ReadAudit(scope));
    }

    [Fact]
    public void Replayed_receipt_with_changed_basis_content_and_reused_digests_is_rejected_without_mutation()
    {
        var store = new CaseStateStore(PathFor());
        var scope = store.CreateCase("case-a", Owner);
        var v1 = CaseStateStore.CreateBasisVersion(1, "evidence v1", string.Empty);
        store.Commit(scope, 0, BasisCommit(v1, "receipt-1"));

        var tampered = v1 with { Content = "evidence changed" };
        Assert.Throws<ReceiptConflictException>(() =>
            store.Commit(scope, 1, BasisCommit(tampered, "receipt-1")));

        Assert.Equal("evidence v1", store.GetBasisVersion(scope, 1).Content);
        Assert.Equal(1, store.Read(scope).Revision);
        Assert.Single(store.ReadAudit(scope));
    }

    [Fact]
    public void Commit_from_a_second_instance_on_the_same_file_is_checked_against_latest_revision()
    {
        var path = PathFor();
        var first = new CaseStateStore(path);
        var firstScope = first.CreateCase("case-a", Owner);
        var second = new CaseStateStore(path);
        var secondScope = second.AuthorizeCase("case-a", Owner);

        first.Commit(firstScope, 0, new CaseCommit(null, "receipt-1", "presenter", "open", "opened"));

        Assert.Throws<RevisionConflictException>(() =>
            second.Commit(secondScope, 0, new CaseCommit(null, "receipt-2", "reviewer", "decide", "approve")));

        var restarted = new CaseStateStore(path);
        var state = restarted.Read(restarted.AuthorizeCase("case-a", Owner));
        Assert.Equal(1, state.Revision);
        Assert.Equal(["receipt-1"], state.ReceiptIds);
        Assert.Single(state.Audit);
    }

    // AC coverage: M-02 (scope and revision isolation), M-10 (immutable basis versions and digests),
    // M-03 (append-only audit, atomic commit), restart and restore.

    [Fact]
    public void M02_forged_or_unknown_case_id_is_denied_and_reveals_nothing()
    {
        var store = new CaseStateStore(PathFor());
        store.CreateCase("case-a", Owner);

        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("case-b", Owner));
        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("", Owner));
    }

    [Fact]
    public void M02_existing_case_id_owned_by_another_caller_is_denied()
    {
        var store = new CaseStateStore(PathFor());
        store.CreateCase("case-a", Owner);
        store.CreateCase("case-b", "owner-b");

        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("case-a", "owner-b"));
        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("case-a", ""));
        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("case-b", Owner));
        Assert.NotNull(store.AuthorizeCase("case-a", Owner));
    }

    [Fact]
    public void M02_stale_revision_is_rejected_without_mutation()
    {
        var store = new CaseStateStore(PathFor());
        var scope = store.CreateCase("case-a", Owner);
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
        var scope = store.CreateCase("case-a", Owner);
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
        var a = store.CreateCase("case-a", Owner);
        var b = store.CreateCase("case-b", "owner-b");
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
        var scope = store.CreateCase("case-a", Owner);

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
        var scope = store.CreateCase("case-a", Owner);
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
    public void M03_restart_restores_case_state_basis_receipts_decisions_and_audit_without_duplicating_actions()
    {
        var path = PathFor();
        var store = new CaseStateStore(path);
        var scope = store.CreateCase("case-a", Owner);
        var v1 = CaseStateStore.CreateBasisVersion(1, "evidence", string.Empty);
        store.Commit(scope, 0, BasisCommit(v1, "receipt-1"));

        var restarted = new CaseStateStore(path);
        var restoredScope = restarted.AuthorizeCase("case-a", Owner);
        var state = restarted.Read(restoredScope);

        Assert.Equal(1, state.Revision);
        Assert.Equal(v1.BasisDigest, state.Basis[0].BasisDigest);
        Assert.Equal(["receipt-1"], state.ReceiptIds);
        Assert.Single(state.Audit);
        Assert.Equal("presenter", state.Audit[0].Actor);
        Assert.Equal("submit-package", state.Audit[0].Action);
        Assert.Equal("basis 1", state.Audit[0].Detail);

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
        var scope = store.CreateCase("case-a", Owner);
        store.Commit(scope, 0, new CaseCommit(null, null, "presenter", "open", "opened"));

        File.WriteAllText(path, File.ReadAllText(path).Replace("\"revision\": 1", "\"revision\": 5"));

        Assert.Throws<InvalidDataException>(() => new CaseStateStore(path));
    }

    // QW-01: fields survive restart; creation timestamp is server-assigned.
    [Fact]
    public void QW01_work_item_fields_survive_restart_with_server_timestamp()
    {
        var store = new CaseStateStore(PathFor());
        var scope = store.CreateCase("case-a", Owner);
        var before = DateTimeOffset.UtcNow;

        store.CreateWorkItem(scope, 0, new WorkItemDraft("item-1", "cap-1", "open", Owner), "presenter");
        var after = DateTimeOffset.UtcNow;

        Assert.Null(typeof(WorkItemDraft).GetProperty("CreatedAt"));
        var restarted = new CaseStateStore(PathFor());
        var item = Assert.Single(restarted.ListWorkItems(restarted.AuthorizeCase("case-a", Owner)));
        Assert.Equal("case-a", item.CaseId);
        Assert.Equal("item-1", item.ItemId);
        Assert.Equal("cap-1", item.CapabilityId);
        Assert.Equal("open", item.State);
        Assert.Equal(Owner, item.Owner);
        Assert.InRange(item.CreatedAt, before, after);
    }

    // QW-01: each missing or blank required field is rejected and nothing is written.
    [Fact]
    public void QW01_rejects_missing_or_blank_work_item_fields()
    {
        var store = new CaseStateStore(PathFor());
        var scope = store.CreateCase("case-a", Owner);
        var drafts = new[]
        {
            new WorkItemDraft("", "cap-1", "open", Owner),
            new WorkItemDraft(" ", "cap-1", "open", Owner),
            new WorkItemDraft("item-1", "", "open", Owner),
            new WorkItemDraft("item-1", "cap-1", " ", Owner),
            new WorkItemDraft("item-1", "cap-1", "open", ""),
        };

        foreach (var draft in drafts)
        {
            Assert.Throws<ArgumentException>(() => store.CreateWorkItem(scope, 0, draft, "presenter"));
        }

        Assert.Empty(store.ListWorkItems(scope));
        Assert.Equal(0, store.Read(scope).Revision);
    }

    // QW-01: item IDs are unique within a case, but the same ID may exist in another case.
    [Fact]
    public void QW01_rejects_duplicate_item_id_within_case_only()
    {
        var store = new CaseStateStore(PathFor());
        var a = store.CreateCase("case-a", Owner);
        var b = store.CreateCase("case-b", "owner-b");
        store.CreateWorkItem(a, 0, new WorkItemDraft("item-1", "cap-1", "open", Owner), "presenter");

        Assert.Throws<InvalidOperationException>(() =>
            store.CreateWorkItem(a, 1, new WorkItemDraft("item-1", "cap-2", "open", Owner), "presenter"));
        Assert.Single(store.ListWorkItems(a));

        store.CreateWorkItem(b, 0, new WorkItemDraft("item-1", "cap-1", "open", "owner-b"), "presenter");
        Assert.Single(store.ListWorkItems(b));
    }

    // QW-02: scope isolates cases; the wrong owner is denied.
    [Fact]
    public void QW02_scope_returns_only_its_case_items_and_denies_other_owners()
    {
        var store = new CaseStateStore(PathFor());
        var a = store.CreateCase("case-a", Owner);
        var b = store.CreateCase("case-b", "owner-b");
        store.CreateWorkItem(a, 0, new WorkItemDraft("item-a", "cap-1", "open", Owner), "presenter");
        store.CreateWorkItem(b, 0, new WorkItemDraft("item-b", "cap-1", "open", "owner-b"), "reviewer");

        var onlyA = Assert.Single(store.ListWorkItems(store.AuthorizeCase("case-a", Owner)));
        Assert.Equal("item-a", onlyA.ItemId);
        var onlyB = Assert.Single(store.ListWorkItems(store.AuthorizeCase("case-b", "owner-b")));
        Assert.Equal("item-b", onlyB.ItemId);
        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("case-a", "owner-b"));
        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("case-b", Owner));
    }

    // QW-03: create and state change need the current revision, bump it once, and audit once.
    [Fact]
    public void QW03_work_item_changes_bump_revision_and_audit_once_and_stale_revision_is_rejected()
    {
        var store = new CaseStateStore(PathFor());
        var scope = store.CreateCase("case-a", Owner);
        var auditBefore = store.ReadAudit(scope).Count;

        var created = store.CreateWorkItem(scope, 0, new WorkItemDraft("item-1", "cap-1", "open", Owner), "presenter");
        Assert.Equal(1, created.State.Revision);
        Assert.Equal(auditBefore + 1, store.ReadAudit(scope).Count);
        var createdAudit = store.ReadAudit(scope)[auditBefore];
        Assert.Equal(("presenter", "create-work-item", "item-1", 1L), (createdAudit.Actor, createdAudit.Action, createdAudit.Detail, createdAudit.Revision));

        var changed = store.ChangeWorkItemState(scope, 1, "item-1", "closed", "reviewer");
        Assert.Equal(2, changed.State.Revision);
        Assert.Equal("closed", Assert.Single(store.ListWorkItems(scope)).State);
        Assert.Equal(auditBefore + 2, store.ReadAudit(scope).Count);
        var changeAudit = store.ReadAudit(scope)[auditBefore + 1];
        Assert.Equal(("reviewer", "change-work-item-state", "item-1 -> closed", 2L), (changeAudit.Actor, changeAudit.Action, changeAudit.Detail, changeAudit.Revision));

        var auditAfter = store.ReadAudit(scope).Count;
        Assert.Throws<RevisionConflictException>(() => store.ChangeWorkItemState(scope, 1, "item-1", "reopened", "reviewer"));
        Assert.Equal("closed", Assert.Single(store.ListWorkItems(scope)).State);
        Assert.Equal(2, store.Read(scope).Revision);
        Assert.Equal(auditAfter, store.ReadAudit(scope).Count);
    }
}
