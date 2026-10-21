using AirlineDemo.Api.Requests;

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

    IReadOnlyList<CaseAssessment> ListAssessments(CaseScope scope) => throw new NotSupportedException("Assessment history is not supported by this store.");
    CaseAssessment? FindAssessment(CaseScope scope, string assessmentId) => throw new NotSupportedException("Assessment history is not supported by this store.");
    IReadOnlyList<DeliveryReceipt> ListRequestReceipts(CaseScope scope) => throw new NotSupportedException("Request receipts are not supported by this store.");

    /// <summary>
    /// Atomically appends an optional run-scoped journey event with the revision, audit event, and other
    /// commit state. The store assigns the event sequence and returns history through <see cref="Read"/>.
    /// </summary>
    CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit);

    /// <summary>Atomically starts a Case C blocked-variant run and stores its receipt and audit event.</summary>
    CasePackResetCommitResult CommitBlockedVariantReset(
        CaseScope scope, long expectedRevision, string actor, string commandId, string variant,
        DateTimeOffset? auditAt = null) =>
        throw new NotSupportedException("Case-pack blocked-variant reset is not supported by this store.");

    IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope);

    CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor);

    CommitResult ChangeWorkItemState(CaseScope scope, long expectedRevision, string itemId, string state, string actor);

    /// <summary>Appends a trace; traces are never updated or deleted. A duplicate (case, run, investigation) throws <see cref="TraceConflictException"/>.</summary>
    void AppendTrace(CaseScope scope, InvestigationTrace trace);

    /// <summary>Returns the trace only when the case, run and investigation ids all match exactly; otherwise null.</summary>
    InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId);
}
