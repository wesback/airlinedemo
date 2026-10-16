using System.Reflection;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Requests;

namespace AirlineDemo.Api.Tests;

public sealed class AssessmentPersistenceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"assessment-history-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        foreach (var path in new[] { _path, _path + ".lock", _path + ".race", _path + ".race.lock" })
            if (File.Exists(path)) File.Delete(path);
    }

    private static (AssessmentRecord Assessment, DeliveryReceipt Receipt) Records(List<AssessedClaim>? claims = null, List<AssessedWitness>? witnesses = null)
    {
        witnesses ??= [new("doc-1", 3)];
        claims ??= [new("MissingRequirement", "reserve approval", "Engine", "ENG-7", witnesses)];
        return (new("case-a", "basis-a", 7, "Request", "supported gap", claims, "REQ-7", null, "assessment-7"),
            new("event-7", "REQ-7", "case-a", "report-7", "basis-a", 7, "template-4", "body-7"));
    }

    private static CaseCommit Commit(AssessmentRecord a, DeliveryReceipt r) =>
        new(null, r.RequestId, "assessment-service", "assessment", "assessment-7 Request", Assessment: a, RequestReceipt: r);

    [Fact]
    public void AC005_ReloadPreservesExactCaseAndBasisIdentity()
    {
        var store = new CaseStateStore(_path);
        var scope = store.CreateCase("case-a", "owner-a");
        store.CreateCase("case-b", "owner-b");
        var (a, r) = Records();
        store.Commit(scope, 0, Commit(a, r));
        var reloaded = new CaseStateStore(_path);
        var restoredScope = reloaded.AuthorizeCase("case-a", "owner-a");
        var state = reloaded.Read(restoredScope);
        var actual = Assert.Single(state.Assessments!);
        Assert.Equal((a.AssessmentId, a.CaseId, a.BasisId, a.BasisVersion, a.Outcome, a.Reason, a.RequestId, a.WorkItemId),
            (actual.AssessmentId, actual.CaseId, actual.BasisId, actual.BasisVersion, actual.Outcome, actual.Reason, actual.RequestId, actual.WorkItemId));
        var claim = Assert.Single(actual.Claims);
        Assert.Equal(("MissingRequirement", "reserve approval", "Engine", "ENG-7"),
            (claim.Kind, claim.Value, claim.Component, claim.SerialNumber));
        Assert.Equal(new AssessedWitness("doc-1", 3), Assert.Single(claim.Witnesses));
        Assert.Equal(r, Assert.Single(state.RequestReceipts!));
        var audit = Assert.Single(state.Audit);
        Assert.Equal((state.CaseId, state.Revision, "assessment"), (actual.CaseId, audit.Revision, audit.Action));
        var other = reloaded.Read(reloaded.AuthorizeCase("case-b", "owner-b"));
        Assert.Empty(other.Assessments!);
        Assert.Empty(other.RequestReceipts!);
        Assert.Null(other.Assessments!.FirstOrDefault(x => x.AssessmentId == actual.AssessmentId));
    }

    [Fact]
    public void M04_CommitIsAtomicAgainstFailureAndRevisionRace()
    {
        var fail = false;
        var failedStore = new CaseStateStore(_path, () => { if (fail) throw new InvalidOperationException("injected write failure"); });
        var scope = failedStore.CreateCase("case-a", "owner-a");
        var (a, r) = Records();
        fail = true;
        Assert.Equal("injected write failure", Assert.Throws<InvalidOperationException>(() => failedStore.Commit(scope, 0, Commit(a, r))).Message);
        var failed = new CaseStateStore(_path).Read(scope);
        Assert.Equal(0L, failed.Revision);
        Assert.Empty(failed.Assessments!);
        Assert.Empty(failed.RequestReceipts!);
        Assert.Empty(failed.Audit);

        var inner = new CaseStateStore(_path + ".race");
        var raceScope = inner.CreateCase("case-a", "owner-a");
        var racing = RevisionRaceStore.Create(inner, raceScope);
        Assert.Throws<RevisionConflictException>(() => racing.Commit(raceScope, 0, Commit(a, r)));
        var raced = new CaseStateStore(_path + ".race").Read(raceScope);
        Assert.Equal(1L, raced.Revision);
        Assert.Empty(raced.Assessments!);
        Assert.Empty(raced.RequestReceipts!);
        Assert.Equal((1L, "intervening"), (Assert.Single(raced.Audit).Revision, raced.Audit[0].Action));
    }

    [Fact]
    public void AC007_StoredAssessmentIsAnImmutableSnapshot()
    {
        var store = new CaseStateStore(_path);
        var scope = store.CreateCase("case-a", "owner-a");
        var witnesses = new List<AssessedWitness> { new("doc-1", 3) };
        var claims = new List<AssessedClaim> { new("MissingRequirement", "reserve approval", "Engine", "ENG-7", witnesses) };
        var (a, r) = Records(claims, witnesses);
        store.Commit(scope, 0, Commit(a, r));
        var before = Assert.Single(store.Read(scope).Assessments!);
        Assert.Equal(("case-a", "basis-a", 7, "Request"), (before.CaseId, before.BasisId, before.BasisVersion, before.Outcome));
        Assert.Equal(("MissingRequirement", "reserve approval", "Engine", "ENG-7"),
            (Assert.Single(before.Claims).Kind, before.Claims[0].Value, before.Claims[0].Component, before.Claims[0].SerialNumber));
        Assert.Equal(new AssessedWitness("doc-1", 3), Assert.Single(before.Claims[0].Witnesses));
        claims.Add(new("Conflict", "changed", "Wing", "W-2", []));
        witnesses.Add(new("doc-2", 9));
        var reloaded = new CaseStateStore(_path);
        var first = Assert.Single(reloaded.Read(scope).Assessments!);
        var second = Assert.Single(reloaded.Read(scope).Assessments!);
        Assert.Equal((first.CaseId, first.BasisId, first.BasisVersion, first.Outcome),
            (second.CaseId, second.BasisId, second.BasisVersion, second.Outcome));
        Assert.Equal(("MissingRequirement", "reserve approval", "Engine", "ENG-7"),
            (Assert.Single(first.Claims).Kind, first.Claims[0].Value, first.Claims[0].Component, first.Claims[0].SerialNumber));
        Assert.Equal(first.Claims[0].Witnesses, second.Claims[0].Witnesses);
        Assert.Equal(new AssessedWitness("doc-1", 3), Assert.Single(first.Claims[0].Witnesses));
    }

    public class RevisionRaceStore : DispatchProxy
    {
        private ICaseStateStore _inner = null!;
        private CaseScope _scope = null!;

        public static ICaseStateStore Create(ICaseStateStore inner, CaseScope scope)
        {
            var proxy = Create<ICaseStateStore, RevisionRaceStore>();
            var race = (RevisionRaceStore)(object)proxy;
            race._inner = inner;
            race._scope = scope;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ICaseStateStore.Commit))
            {
                _inner.Commit(_scope, _inner.Read(_scope).Revision, new(null, null, "racer", "intervening", "revision changed"));
                return _inner.Commit((CaseScope)args![0]!, (long)args[1]!, (CaseCommit)args[2]!);
            }
            return method.Invoke(_inner, args);
        }
    }
}
