using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Policy;
using AirlineDemo.Api.Requests;

namespace AirlineDemo.Api.Tests;

public sealed class AssessmentPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "assessment-history-" + Guid.NewGuid().ToString("N"));
    private string PathFor => Path.Combine(_directory, "cases.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void AC005_ReloadPreservesExactCaseAndBasisIdentity()
    {
        var store = new CaseStateStore(PathFor);
        var scope = store.CreateCase("case-1", "owner-1");
        var otherScope = store.CreateCase("case-2", "owner-2");
        var assessment = new CaseAssessment("assessment-1", scope.CaseId, "basis-7", 7, "Request", "reason",
            [new("MissingRequirement", "oil sample", "Engine", "ENG-123", [new("doc-1", 9)])], "request-1", "work-1");
        var receipt = new DeliveryReceipt("event-1", "request-1", scope.CaseId, "report-1", "basis-7", 7, "template-3", "request body");
        store.Commit(scope, 0, new CaseCommit(null, receipt.EventId, "actor", "assessment", "saved",
            Assessment: assessment, RequestReceipt: receipt));
        var otherAssessment = assessment with { AssessmentId = "assessment-2", CaseId = otherScope.CaseId };
        store.Commit(otherScope, 0, new CaseCommit(null, null, "actor", "assessment", "saved", Assessment: otherAssessment));

        var reloaded = new CaseStateStore(PathFor);
        var stored = reloaded.FindAssessment(reloaded.AuthorizeCase("case-1", "owner-1"), assessment.AssessmentId)!;
        Assert.Equal(("assessment-1", "case-1", "basis-7", 7, "Request", "reason", "request-1", "work-1"),
            (stored.AssessmentId, stored.CaseId, stored.BasisId, stored.BasisVersion, stored.Outcome, stored.Reason, stored.RequestId, stored.WorkItemId));
        var claim = Assert.Single(stored.Claims);
        Assert.Equal(("MissingRequirement", "oil sample", "Engine", "ENG-123"), (claim.Kind, claim.Value, claim.Component, claim.SerialNumber));
        Assert.Equal(("doc-1", 9), (Assert.Single(claim.Witnesses).DocumentId, claim.Witnesses[0].Page));
        Assert.Equal(receipt, Assert.Single(reloaded.ListRequestReceipts(reloaded.AuthorizeCase("case-1", "owner-1"))));
        Assert.Null(reloaded.FindAssessment(reloaded.AuthorizeCase("case-2", "owner-2"), assessment.AssessmentId));
        Assert.Null(reloaded.FindAssessment(reloaded.AuthorizeCase("case-1", "owner-1"), otherAssessment.AssessmentId));
    }

    [Fact]
    public void M04_CommitIsAtomicAgainstFailureAndRevisionRace()
    {
        var successStore = new CaseStateStore(Path.Combine(_directory, "success.json"));
        var successCases = new ForwardingCaseStateStore(successStore);
        var (successScope, successExecution, successInbox) = Prepare(successCases, "success-case");
        var successful = successExecution.Assess(successScope, Basis(), [Claim()]);

        Assert.Equal(1, successCases.CommitCalls);
        Assert.Equal(0, successCases.LastExpectedRevision);
        Assert.Equal(1, successCases.LastCommitResult!.State.Revision);
        Assert.Equal(1, successCases.LastCommitResult.State.Audit.Single().Revision);
        Assert.False(successCases.LastCommitResult.Replayed);
        AssertPersistedDelivery(successStore, successScope, successful.AssessmentId!, successInbox);
        var successEvent = Assert.Single(successStore.ReadAudit(successScope));
        Assert.Equal(1, successEvent.Revision);
        Assert.Equal(1, successEvent.Sequence);
        Assert.Equal(AssessmentExecutionService.Actor, successEvent.Actor);
        Assert.Equal("assessment-delivered", successEvent.Action);

        var failWrite = false;
        var failingStore = new CaseStateStore(PathFor, () =>
        {
            if (failWrite) throw new IOException("injected persistence failure");
        });
        var failingCases = new ForwardingCaseStateStore(failingStore);
        var (failingScope, failingExecution, failingInbox) = Prepare(failingCases, "failure-case");
        failWrite = true;

        Assert.Throws<IOException>(() => failingExecution.Assess(failingScope, Basis(), [Claim()]));
        Assert.Equal(1, failingCases.CommitCalls);
        Assert.Equal(0, failingCases.LastExpectedRevision);

        var failedReload = new CaseStateStore(PathFor);
        var failedScope = failedReload.AuthorizeCase("failure-case", "owner");
        Assert.Empty(failedReload.ListAssessments(failedScope));
        Assert.Empty(failedReload.ListRequestReceipts(failedScope));
        Assert.Empty(failedReload.ReadAudit(failedScope));
        Assert.Empty(failingInbox.Requests);

        var raceStore = new CaseStateStore(Path.Combine(_directory, "race.json"));
        var raceCases = new ForwardingCaseStateStore(raceStore);
        var (raceScope, raceExecution, raceInbox) = Prepare(raceCases, "race-case");
        raceCases.BeforeCommit = (scope, revision, _) =>
            raceStore.Commit(scope, revision, new CaseCommit(null, null, "intervening-writer", "concurrent-update", "intervening revision"));

        Assert.Throws<RevisionConflictException>(() => raceExecution.Assess(raceScope, Basis(), [Claim()]));
        Assert.Equal(1, raceCases.CommitCalls);
        Assert.Equal(0, raceCases.LastExpectedRevision);

        var raceReload = new CaseStateStore(Path.Combine(_directory, "race.json"));
        var reloadedRaceScope = raceReload.AuthorizeCase("race-case", "owner");
        Assert.Empty(raceReload.ListAssessments(reloadedRaceScope));
        Assert.Empty(raceReload.ListRequestReceipts(reloadedRaceScope));
        var raceEvent = Assert.Single(raceReload.ReadAudit(reloadedRaceScope));
        Assert.Equal((1L, "intervening-writer", "concurrent-update", "intervening revision"),
            (raceEvent.Revision, raceEvent.Actor, raceEvent.Action, raceEvent.Detail));
        Assert.Empty(raceInbox.Requests);
    }

    [Fact]
    public void AC007_StoredAssessmentIsAnImmutableSnapshot()
    {
        var store = new CaseStateStore(PathFor);
        var scope = store.CreateCase("case-1", "owner-1");
        var witnesses = new List<AssessedWitness> { new("doc-1", 9) };
        var claims = new List<AssessedClaim>
        {
            new("MissingRequirement", "oil sample", "Engine", "ENG-123", witnesses),
        };
        var assessment = new CaseAssessment(
            "assessment-1", "case-1", "basis-7", 7, "Request", "reason", claims, "request-1", null);

        store.Commit(scope, 0, new CaseCommit(null, null, "actor", "assessment", "saved", Assessment: assessment));
        AssertAssessmentSnapshot(store.FindAssessment(scope, assessment.AssessmentId));

        witnesses[0] = new AssessedWitness("changed-doc", 2);
        witnesses.Add(new AssessedWitness("extra-doc", 3));
        claims[0] = new AssessedClaim("Baseline", "changed", "Changed", "CHANGED", []);
        claims.Add(new AssessedClaim("Conflict", "extra", "Extra", "EXTRA", []));
        AssertAssessmentSnapshot(store.FindAssessment(scope, assessment.AssessmentId));

        var reloaded = new CaseStateStore(PathFor);
        var reloadedScope = reloaded.AuthorizeCase("case-1", "owner-1");
        var firstRead = reloaded.FindAssessment(reloadedScope, assessment.AssessmentId);
        var secondRead = reloaded.FindAssessment(reloadedScope, assessment.AssessmentId);
        AssertAssessmentSnapshot(firstRead);
        AssertAssessmentSnapshot(secondRead);
        Assert.Equal(
            (firstRead!.CaseId, firstRead.BasisId, firstRead.BasisVersion, firstRead.Outcome, firstRead.Claims),
            (secondRead!.CaseId, secondRead.BasisId, secondRead.BasisVersion, secondRead.Outcome, secondRead.Claims));
    }

    private static (CaseScope Scope, AssessmentExecutionService Execution, MockRequestInbox Inbox) Prepare(
        ICaseStateStore cases,
        string caseId)
    {
        var scope = cases.CreateCase(caseId, "owner");
        var inbox = new MockRequestInbox();
        var assessments = new AssessmentStore();
        assessments.SetCurrentBasis(caseId, Basis());
        return (scope, new AssessmentExecutionService(cases, inbox, assessments), inbox);
    }

    private static AssessmentBasis Basis() =>
        new("basis-7", 1, "2026-10-14T00:00:00Z", new ProcessingCoverage(1, 1, 0, 0), false,
            new Dictionary<string, BasisDocument>
            {
                ["doc-1"] = BasisDocument.Create("doc-1", ["pending reserve approval"]),
            });

    private static AssessmentClaim Claim() =>
        new(ClaimKind.MissingRequirement, "pending reserve approval", "Engine", "ENG-123",
            [new DocumentWitness("doc-1", 1, "pending reserve approval")]);

    private static void AssertPersistedDelivery(
        ICaseStateStore store,
        CaseScope scope,
        string assessmentId,
        MockRequestInbox inbox)
    {
        var assessment = Assert.IsType<CaseAssessment>(store.FindAssessment(scope, assessmentId));
        var receipt = Assert.Single(store.ListRequestReceipts(scope));
        Assert.Equal(
            (assessmentId, scope.CaseId, "basis-7", 1, "Request", "single supported missing requirement on current basis", receipt.RequestId, (string?)null),
            (assessment.AssessmentId, assessment.CaseId, assessment.BasisId, assessment.BasisVersion, assessment.Outcome,
                assessment.Reason, assessment.RequestId, assessment.WorkItemId));
        Assert.Equal(scope.CaseId, assessment.CaseId);
        Assert.Equal(assessment.CaseId, receipt.CaseId);
        Assert.Equal(assessment.BasisId, receipt.BasisId);
        Assert.Equal(assessment.BasisVersion, receipt.BasisVersion);
        Assert.Equal(assessment.RequestId, receipt.RequestId);
        Assert.Equal(
            ($"{scope.CaseId}:basis-7:1:assessment", receipt.RequestId, scope.CaseId, "assessment-1", "basis-7", 1,
                MockRequestTemplate.Version,
                $"Synthetic demonstration request. For Engine ENG-123, please provide pending reserve approval. Evidence basis basis-7 v1. Reference {receipt.RequestId}."),
            (receipt.EventId, receipt.RequestId, receipt.CaseId, receipt.ReportId, receipt.BasisId, receipt.BasisVersion,
                receipt.TemplateVersion, receipt.Body));
        Assert.Equal(receipt, Assert.Single(inbox.Requests));
        var claim = Assert.Single(assessment.Claims);
        Assert.Equal(("MissingRequirement", "pending reserve approval", "Engine", "ENG-123"),
            (claim.Kind, claim.Value, claim.Component, claim.SerialNumber));
        Assert.Equal(("doc-1", 1), (Assert.Single(claim.Witnesses).DocumentId, claim.Witnesses[0].Page));
    }

    private static void AssertAssessmentSnapshot(CaseAssessment? assessment)
    {
        Assert.NotNull(assessment);
        Assert.Equal(("case-1", "basis-7", 7, "Request", "reason", "request-1"),
            (assessment.CaseId, assessment.BasisId, assessment.BasisVersion, assessment.Outcome, assessment.Reason, assessment.RequestId));
        var claim = Assert.Single(assessment.Claims);
        Assert.Equal(("MissingRequirement", "oil sample", "Engine", "ENG-123"),
            (claim.Kind, claim.Value, claim.Component, claim.SerialNumber));
        Assert.Equal(("doc-1", 9), (Assert.Single(claim.Witnesses).DocumentId, claim.Witnesses[0].Page));
    }

    private sealed class ForwardingCaseStateStore(ICaseStateStore inner) : ICaseStateStore
    {
        public int CommitCalls { get; private set; }
        public long LastExpectedRevision { get; private set; }
        public CommitResult? LastCommitResult { get; private set; }
        public Action<CaseScope, long, CaseCommit>? BeforeCommit { get; set; }

        public CaseScope AuthorizeCase(string caseId, string owner) => inner.AuthorizeCase(caseId, owner);
        public string? OwnerOf(string caseId) => inner.OwnerOf(caseId);
        public CaseScope CreateCase(string caseId, string owner) => inner.CreateCase(caseId, owner);
        public CaseState Read(CaseScope scope) => inner.Read(scope);
        public BasisVersion GetBasisVersion(CaseScope scope, int version) => inner.GetBasisVersion(scope, version);
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => inner.ReadAudit(scope);
        public IReadOnlyList<CaseAssessment> ListAssessments(CaseScope scope) => inner.ListAssessments(scope);
        public CaseAssessment? FindAssessment(CaseScope scope, string assessmentId) => inner.FindAssessment(scope, assessmentId);
        public IReadOnlyList<DeliveryReceipt> ListRequestReceipts(CaseScope scope) => inner.ListRequestReceipts(scope);
        public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit)
        {
            CommitCalls++;
            LastExpectedRevision = expectedRevision;
            BeforeCommit?.Invoke(scope, expectedRevision, commit);
            LastCommitResult = inner.Commit(scope, expectedRevision, commit);
            return LastCommitResult;
        }
        public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope) => inner.ListWorkItems(scope);
        public CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor) =>
            inner.CreateWorkItem(scope, expectedRevision, draft, actor);
        public CommitResult ChangeWorkItemState(CaseScope scope, long expectedRevision, string itemId, string state, string actor) =>
            inner.ChangeWorkItemState(scope, expectedRevision, itemId, state, actor);
        public void AppendTrace(CaseScope scope, InvestigationTrace trace) => inner.AppendTrace(scope, trace);
        public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId) =>
            inner.FindTrace(scope, caseId, runId, investigationId);
    }
}
