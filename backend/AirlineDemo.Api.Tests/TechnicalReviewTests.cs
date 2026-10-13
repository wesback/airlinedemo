using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;

namespace AirlineDemo.Api.Tests;

public sealed class TechnicalReviewTests : IDisposable
{
    private const string Owner = "owner-a";
    private const string CaseId = "case-a";
    private const string Reviewer = "reviewer-1";
    private static readonly string[] InScope = [CaseId];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tech-review-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private CaseStateStore NewStore() => new(Path.Combine(_dir, "cases.json"));

    private static (CaseStateStore Store, long Revision) WithBasis(CaseStateStore store, string content)
    {
        var scope = store.CreateCase(CaseId, Owner);
        var basis = CaseStateStore.CreateBasisVersion(1, content, string.Empty);
        store.Commit(scope, 0, new CaseCommit(basis, null, "presenter", "submit-evidence", "basis 1"));
        return (store, 1);
    }

    private static TechnicalReviewCommand Accept(long revision, int basis, string? reason = "Evidence supports return.") =>
        new(revision, basis, "accept", reason);

    [Fact]
    public void Authorized_reviewer_records_reasoned_decision_with_provenance()
    {
        var (store, rev) = WithBasis(NewStore(), "evidence v1");

        var result = TechnicalReviewPolicy.Decide(store, InScope, CaseEndpoints.ReviewerRole, Reviewer, CaseId,
            Accept(rev, 1), "trace-1");

        Assert.Equal(TechnicalReviewOutcome.Recorded, result.Outcome);
        var decision = result.Decision!;
        Assert.Equal(Reviewer, decision.Actor);
        Assert.Equal("Evidence supports return.", decision.Reason);
        Assert.Equal(1, decision.BasisVersion);
        Assert.Equal("approved", decision.Outcome);
        Assert.True(decision.Current);
        var audit = store.ReadAudit(store.AuthorizeCase(CaseId, Owner)).Single(a => a.Action == TechnicalReviewPolicy.DecisionAction);
        Assert.Equal(Reviewer, audit.Actor);
        Assert.Equal(2, audit.Revision);
    }

    [Theory]
    [InlineData("presenter")]
    [InlineData("viewer")]
    public void Unauthorized_role_is_denied_without_decision(string role)
    {
        var (store, rev) = WithBasis(NewStore(), "evidence v1");
        var before = store.Read(store.AuthorizeCase(CaseId, Owner)).Revision;

        var result = TechnicalReviewPolicy.Decide(store, InScope, role, "someone", CaseId, Accept(rev, 1), "trace-1");

        Assert.Equal(TechnicalReviewOutcome.Forbidden, result.Outcome);
        Assert.Equal(before, store.Read(store.AuthorizeCase(CaseId, Owner)).Revision);
        Assert.Empty(TechnicalReviewPolicy.Decisions(store.Read(store.AuthorizeCase(CaseId, Owner))));
    }

    [Fact]
    public void Missing_or_blank_reason_is_denied_without_decision()
    {
        var (store, rev) = WithBasis(NewStore(), "evidence v1");
        var scope = store.AuthorizeCase(CaseId, Owner);

        var missing = TechnicalReviewPolicy.Decide(store, InScope, CaseEndpoints.ReviewerRole, Reviewer, CaseId,
            Accept(rev, 1, reason: null), "trace-1");
        var blank = TechnicalReviewPolicy.Decide(store, InScope, CaseEndpoints.ReviewerRole, Reviewer, CaseId,
            Accept(rev, 1, reason: "   "), "trace-2");

        Assert.Equal(TechnicalReviewOutcome.Invalid, missing.Outcome);
        Assert.Equal(TechnicalReviewOutcome.Invalid, blank.Outcome);
        Assert.Equal(rev, store.Read(scope).Revision);
        Assert.Empty(TechnicalReviewPolicy.Decisions(store.Read(scope)));
    }

    [Fact]
    public void Case_outside_scope_is_not_found_without_decision()
    {
        var (store, rev) = WithBasis(NewStore(), "evidence v1");

        var result = TechnicalReviewPolicy.Decide(store, ["other-case"], CaseEndpoints.ReviewerRole, Reviewer, CaseId,
            Accept(rev, 1), "trace-1");

        Assert.Equal(TechnicalReviewOutcome.NotFound, result.Outcome);
        Assert.Empty(TechnicalReviewPolicy.Decisions(store.Read(store.AuthorizeCase(CaseId, Owner))));
    }

    [Fact]
    public void Stale_basis_or_revision_is_denied_without_decision()
    {
        var (store, rev) = WithBasis(NewStore(), "evidence v1");

        var staleBasis = TechnicalReviewPolicy.Decide(store, InScope, CaseEndpoints.ReviewerRole, Reviewer, CaseId,
            Accept(rev, 0), "trace-1");
        var staleRevision = TechnicalReviewPolicy.Decide(store, InScope, CaseEndpoints.ReviewerRole, Reviewer, CaseId,
            Accept(rev - 1, 1), "trace-2");

        Assert.Equal(TechnicalReviewOutcome.Stale, staleBasis.Outcome);
        Assert.Equal(TechnicalReviewOutcome.Stale, staleRevision.Outcome);
        Assert.Empty(TechnicalReviewPolicy.Decisions(store.Read(store.AuthorizeCase(CaseId, Owner))));
    }

    [Fact]
    public void Later_basis_marks_prior_acceptance_non_current_and_stops_it_authorizing()
    {
        var store = NewStore();
        var (_, rev) = WithBasis(store, "evidence v1");
        var decided = TechnicalReviewPolicy.Decide(store, InScope, CaseEndpoints.ReviewerRole, Reviewer, CaseId,
            Accept(rev, 1), "trace-1");
        Assert.True(TechnicalReviewPolicy.AuthorizesRequest(store.Read(store.AuthorizeCase(CaseId, Owner))));

        var scope = store.AuthorizeCase(CaseId, Owner);
        var next = CaseStateStore.CreateBasisVersion(2, "evidence v2", store.Read(scope).Basis[0].BasisDigest);
        store.Commit(scope, decided.Decision!.Revision, new CaseCommit(next, null, "presenter", "submit-evidence", "basis 2"));

        var state = store.Read(scope);
        var prior = Assert.Single(TechnicalReviewPolicy.Decisions(state));
        Assert.False(prior.Current);
        Assert.Equal(1, prior.BasisVersion);
        Assert.Equal("Evidence supports return.", prior.Reason);
        Assert.False(TechnicalReviewPolicy.AuthorizesRequest(state));
    }

    [Fact]
    public void Decision_on_superseded_basis_cannot_be_recorded()
    {
        var store = NewStore();
        var (_, rev) = WithBasis(store, "evidence v1");
        var scope = store.AuthorizeCase(CaseId, Owner);
        var next = CaseStateStore.CreateBasisVersion(2, "evidence v2", store.Read(scope).Basis[0].BasisDigest);
        store.Commit(scope, rev, new CaseCommit(next, null, "presenter", "submit-evidence", "basis 2"));

        var result = TechnicalReviewPolicy.Decide(store, InScope, CaseEndpoints.ReviewerRole, Reviewer, CaseId,
            Accept(rev, 1), "trace-1");

        Assert.Equal(TechnicalReviewOutcome.Stale, result.Outcome);
        Assert.Empty(TechnicalReviewPolicy.Decisions(store.Read(scope)));
    }
}
