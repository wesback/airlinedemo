using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Auth;

public sealed record CaseRecord(string Id, int Revision, string Status);

public enum ReviewOutcome { Recorded, NotFound, Stale }

// Endpoint-facing case access. Every read and write goes through ICaseStateStore.
public class CaseStore
{
    public const string SeedOwner = "presenter";
    public const string AcceptAction = "review-accept";
    public const string RejectAction = "review-reject";
    private const string AwaitingReview = "awaiting-review";
    private static readonly string[] DemoCaseIds = ["case-001", "case-002", "case-003"];

    private readonly ICaseStateStore? _store;

    public CaseStore(ICaseStateStore store) => _store = store;

    // Parameterless constructor for test doubles that override the operations below.
    protected CaseStore() => _store = null;

    private ICaseStateStore Store => _store ?? throw new InvalidOperationException("Case store is not configured.");

    // Creates the demo cases at revision 1 when they are missing; existing cases are left as they are.
    public static void SeedDemoCases(ICaseStateStore store)
    {
        foreach (var caseId in DemoCaseIds)
        {
            if (store.OwnerOf(caseId) is not null) continue;
            var scope = store.CreateCase(caseId, SeedOwner);
            store.Commit(scope, 0, new CaseCommit(
                CaseStateStore.CreateBasisVersion(1, "evidence v1", string.Empty), null, SeedOwner, "submit-evidence", "basis 1"));
        }
    }

    public virtual CaseRecord? Find(string id)
    {
        var owner = Store.OwnerOf(id);
        return owner is null ? null : ToRecord(Store.Read(Store.AuthorizeCase(id, owner)));
    }

    public virtual ReviewOutcome RecordReview(string id, int expectedRevision, string decision, out CaseRecord? updated)
    {
        updated = null;
        var owner = Store.OwnerOf(id);
        if (owner is null) return ReviewOutcome.NotFound;

        var action = decision == "accept" ? AcceptAction : RejectAction;
        try
        {
            var result = Store.Commit(Store.AuthorizeCase(id, owner), expectedRevision,
                new CaseCommit(null, null, CaseEndpoints.ReviewerRole, action, "technical review " + decision));
            updated = ToRecord(result.State);
            return ReviewOutcome.Recorded;
        }
        catch (RevisionConflictException)
        {
            return ReviewOutcome.Stale;
        }
    }

    private static CaseRecord ToRecord(CaseState state)
    {
        var status = state.Audit.LastOrDefault()?.Action switch
        {
            AcceptAction => "accepted",
            RejectAction => "rejected",
            _ => AwaitingReview,
        };
        return new CaseRecord(state.CaseId, (int)state.Revision, status);
    }
}
