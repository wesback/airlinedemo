namespace AirlineDemo.Api.Persistence;

/// <summary>Scoped, versioned case state. Mutations go through <see cref="Commit"/>; the audit log is read-only.</summary>
public interface ICaseStateStore
{
    CaseScope AuthorizeCase(string caseId, string owner);
    string? OwnerOf(string caseId);
    CaseScope CreateCase(string caseId, string owner);
    CaseState Read(CaseScope scope);
    BasisVersion GetBasisVersion(CaseScope scope, int version);
    IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope);
    CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit);
}
