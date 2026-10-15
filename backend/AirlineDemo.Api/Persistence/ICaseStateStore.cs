namespace AirlineDemo.Api.Persistence;

/// <summary>
/// Scoped, versioned case state. Implemented by the file-backed <see cref="CaseStateStore"/>;
/// the SQL adapter implements the same contract once runtime storage switches.
/// </summary>
public interface ICaseStateStore
{
    CaseScope AuthorizeCase(string caseId, string owner);

    string? OwnerOf(string caseId);

    CaseScope CreateCase(string caseId, string owner);

    CaseState Read(CaseScope scope);

    BasisVersion GetBasisVersion(CaseScope scope, int version);

    IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope);

    CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit);

    IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope);

    CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor);

    CommitResult ChangeWorkItemState(CaseScope scope, long expectedRevision, string itemId, string state, string actor);
}
