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

    /// <summary>Appends an investigation trace. Traces are never updated or deleted; a duplicate (case, run, investigation) throws <see cref="TraceConflictException"/>.</summary>
    void AppendTrace(CaseScope scope, InvestigationTrace trace);

    /// <summary>Returns the trace only when the case, run and investigation ids all match exactly; otherwise null.</summary>
    InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId);
}
