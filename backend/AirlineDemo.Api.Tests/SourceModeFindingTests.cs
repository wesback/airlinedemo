using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Policy;
using AirlineDemo.Api.Requests;
using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Tests;

public sealed class SourceModeFindingTests
{
    private const string CaseId = "mode-finding-case";
    private const string DocumentId = "engine-release";
    private const string ScenarioTime = "2026-10-14T00:00:00Z";
    private const string SourceSerial = "SYN-SN-77201";
    private const string DocumentSerial = "SYN-SN-77210";
    private static readonly ProcessingCoverage Complete = new(1, 1, 0, 0);
    private sealed class ProbeSource<TPayload>(string sourceId, IReadOnlyList<SourceRecord<TPayload>> records)
        : ISourceSystem<TPayload> where TPayload : class
    {
        private IReadOnlyList<SourceRecord<TPayload>> _records = records;
        public int Calls { get; private set; }
        public IReadOnlyList<SourceRecord<TPayload>> Records
        {
            get => _records;
            set => _records = value;
        }
        public Task<SourceReadResult<IReadOnlyList<SourceRecord<TPayload>>>> ListAsync(string caseId,
            CancellationToken cancellationToken = default, int? maxRecords = null)
        {
            Calls++;
            return Task.FromResult<SourceReadResult<IReadOnlyList<SourceRecord<TPayload>>>>(
                new SourceReadSucceeded<IReadOnlyList<SourceRecord<TPayload>>>(_records
                    .Where(record => record.CaseId == caseId && record.SourceId == sourceId)
                    .Take(maxRecords ?? int.MaxValue).ToArray()));
        }
        public Task<SourceReadResult<SourceRecord<TPayload>>> GetAsync(string caseId, string recordId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
    private sealed class RacingStore(ICaseStateStore inner) : ICaseStateStore
    {
        public CaseState? AfterRace { get; private set; }
        public CaseScope AuthorizeCase(string caseId, string owner) => inner.AuthorizeCase(caseId, owner);
        public string? OwnerOf(string caseId) => inner.OwnerOf(caseId);
        public CaseScope CreateCase(string caseId, string owner) => inner.CreateCase(caseId, owner);
        public CaseState Read(CaseScope scope) => inner.Read(scope);
        public BasisVersion GetBasisVersion(CaseScope scope, int version) => inner.GetBasisVersion(scope, version);
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => inner.ReadAudit(scope);
        public CommitResult Commit(CaseScope scope, long revision, CaseCommit commit)
        {
            if (commit.Action == "source-basis.gathered")
            {
                var observed = inner.Read(scope);
                inner.Commit(scope, observed.Revision, new CaseCommit(null, null, "concurrent-writer",
                    "concurrent-update", "intervening revision"));
                AfterRace = inner.Read(scope);
            }
            return inner.Commit(scope, revision, commit);
        }
        public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope) => inner.ListWorkItems(scope);
        public CommitResult CreateWorkItem(CaseScope scope, long revision, WorkItemDraft draft, string actor) =>
            inner.CreateWorkItem(scope, revision, draft, actor);
        public CommitResult ChangeWorkItemState(CaseScope scope, long revision, string itemId, string state, string actor) =>
            inner.ChangeWorkItemState(scope, revision, itemId, state, actor);
        public void AppendTrace(CaseScope scope, InvestigationTrace trace) => inner.AppendTrace(scope, trace);
        public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId) =>
            inner.FindTrace(scope, caseId, runId, investigationId);
    }

    private sealed record Scenario(string Path, CaseStateStore Store, CaseScope Scope, SourceModeStore Modes,
        ProbeSource<TechnicalPayload> Technical, ProbeSource<MaintenancePayload> Maintenance, ProbeSource<FinancePayload> Finance,
        ProbeSource<RecordsPayload> Records, ProbeSource<ContractsPayload> Contracts, MockRequestInbox Inbox,
        AssessmentStore Assessments, AssessmentBasis Basis, AssessmentExecutionService Execution);

    [Fact]
    public void ModesProduceExactReadAndFindingOutcomes()
    {
        foreach (var mode in SourceModes.Modes)
        {
            var scenario = CreateScenario(mode, SourceSerial,
                documentSerial: mode == "conflicting" ? DocumentSerial : SourceSerial);
            try
            {
                var read = SourceModeFindingPolicy.Apply(scenario.Technical, "technical", mode)
                    .ListAsync(CaseId).GetAwaiter().GetResult();
                switch (mode)
                {
                    case "available":
                        var available = Assert.IsType<SourceReadSucceeded<IReadOnlyList<SourceRecord<TechnicalPayload>>>>(read);
                        Assert.Equal((1, new DateOnly(2026, 10, 1), false),
                            (Assert.Single(available.Value).RecordVersion, available.Value[0].AsOf, available.Value[0].Stale));
                        break;
                    case "unavailable":
                        var unavailable = Assert.IsType<SourceUnavailable<IReadOnlyList<SourceRecord<TechnicalPayload>>>>(read);
                        Assert.Equal("source_unavailable", SourceUnavailable<IReadOnlyList<SourceRecord<TechnicalPayload>>>.ErrorCode);
                        Assert.Equal("technical", unavailable.SourceId);
                        Assert.Equal(0, scenario.Technical.Calls);
                        break;
                    case "stale":
                        var stale = Assert.IsType<SourceReadSucceeded<IReadOnlyList<SourceRecord<TechnicalPayload>>>>(read);
                        Assert.Equal((new DateOnly(2026, 9, 30), true),
                            (Assert.Single(stale.Value).AsOf, stale.Value[0].Stale));
                        break;
                    case "conflicting":
                        var conflicting = Assert.IsType<SourceReadSucceeded<IReadOnlyList<SourceRecord<TechnicalPayload>>>>(read);
                        Assert.Equal([1, 2], conflicting.Value.Select(record => record.RecordVersion));
                        Assert.Equal([SourceSerial, SourceSerial],
                            conflicting.Value.Select(record => record.Payload.Fields["serialNumber"]));
                        break;
                }
                var documentSerial = mode == "conflicting" ? DocumentSerial : SourceSerial;
                var assessment = scenario.Execution.Assess(scenario.Scope, scenario.Basis, [Claim(documentSerial)]);
                if (mode == "available")
                {
                    Assert.Equal(("Baseline", "supported baseline"), (assessment.Outcome, assessment.Reason));
                    Assert.Equal([new AssessedWitness(DocumentId, 1), SourceWitness(1)],
                        Assert.Single(assessment.Claims).Witnesses);
                }
                else
                {
                    var expectedReason = mode == "unavailable" ? "source_unavailable"
                        : mode == "stale" ? "source_stale" : "source_conflict";
                    Assert.Equal((AssessmentExecutionService.Review, expectedReason), (assessment.Outcome, assessment.Reason));
                    AssessedWitness[] expectedWitnesses = mode switch
                    {
                        "unavailable" => [new AssessedWitness(DocumentId, 1)],
                        "conflicting" => [new AssessedWitness(DocumentId, 1), SourceWitness(1), SourceWitness(2)],
                        _ => [new AssessedWitness(DocumentId, 1), SourceWitness(1)],
                    };
                    Assert.Equal(expectedWitnesses, Assert.Single(assessment.Claims).Witnesses);
                }
            }
            finally { Delete(scenario.Path); }
        }
    }

    [Fact]
    public void CaseBConflictRoutesBothWitnessesToReview()
    {
        var matching = CreateScenario("available", SourceSerial, "case-b-matching", documentSerial: SourceSerial);
        var conflict = CreateScenario("conflicting", SourceSerial, "case-b-conflict", documentSerial: DocumentSerial);
        try
        {
            var normal = matching.Execution.Assess(matching.Scope, matching.Basis, [Claim(SourceSerial)]);
            Assert.Equal(("Baseline", "supported baseline"), (normal.Outcome, normal.Reason));
            Assert.Equal([new AssessedWitness(DocumentId, 1), SourceWitness(1)],
                Assert.Single(normal.Claims).Witnesses);
            Assert.Empty(matching.Store.ListWorkItems(matching.Scope));
            var review = conflict.Execution.Assess(conflict.Scope, conflict.Basis, [Claim(DocumentSerial)]);
            Assert.Equal((AssessmentExecutionService.Review, "source_conflict"), (review.Outcome, review.Reason));
            Assert.Null(review.RequestId);
            var workItem = Assert.Single(conflict.Store.ListWorkItems(conflict.Scope));
            Assert.Equal((review.WorkItemId, "records-investigation", "open", "technical-reviewer"),
                (workItem.ItemId, workItem.CapabilityId, workItem.State, workItem.Owner));
            Assert.Equal(
                [new AssessedWitness(DocumentId, 1), SourceWitness(1), SourceWitness(2)],
                Assert.Single(review.Claims).Witnesses);
            Assert.Empty(conflict.Inbox.Requests);
        }
        finally
        {
            Delete(matching.Path);
            Delete(conflict.Path);
        }
    }

    [Fact]
    public void UnrelatedComponentSerialDoesNotConflictWithClaim()
    {
        var scenario = CreateScenario("available", SourceSerial, documentSerial: SourceSerial);
        try
        {
            scenario.Technical.Records =
            [
                TechnicalRecord(CaseId, SourceSerial),
                TechnicalRecord(CaseId, "SYN-SN-APU-001") with
                {
                    RecordId = "tech-apu-component-001",
                    Payload = new TechnicalPayload(new Dictionary<string, string>
                    {
                        ["serialNumber"] = "SYN-SN-APU-001",
                        ["position"] = "apu_1",
                    }),
                },
            ];
            var assessment = scenario.Execution.Assess(scenario.Scope, scenario.Basis, [Claim(SourceSerial)]);
            Assert.Equal(("Baseline", "supported baseline"), (assessment.Outcome, assessment.Reason));
            Assert.Equal([new AssessedWitness(DocumentId, 1), SourceWitness(1)],
                Assert.Single(assessment.Claims).Witnesses);
        }
        finally { Delete(scenario.Path); }
    }

    [Fact]
    public void SourceFailuresNeverCreateRequestsAcceptancesOrRefreshBasis()
    {
        foreach (var mode in new[] { "unavailable", "stale", "conflicting" })
        {
            var scenario = CreateScenario(mode, SourceSerial, $"failure-{mode}", documentSerial: DocumentSerial);
            try
            {
                var gathered = Gather(scenario);
                var original = CaseStateStore.CreateBasisVersion(1, gathered.Content, string.Empty);
                scenario.Store.Commit(scenario.Scope, scenario.Store.Read(scenario.Scope).Revision,
                    new CaseCommit(original, null, "presenter", "submit-evidence", "initial source basis"));
                var before = scenario.Store.Read(scenario.Scope);
                scenario.Assessments.SetCurrentBasis(scenario.Scope.CaseId, scenario.Basis);
                var result = scenario.Execution.Assess(scenario.Scope, scenario.Basis, [Claim(DocumentSerial)]);
                Assert.Equal(AssessmentExecutionService.Review, result.Outcome);
                Assert.Empty(scenario.Inbox.Requests);
                Assert.DoesNotContain(scenario.Store.ReadAudit(scenario.Scope), audit => audit.Action == "mock-response.accepted");
                var after = scenario.Store.Read(scenario.Scope);
                Assert.Equal(before.Basis, after.Basis);
            }
            finally { Delete(scenario.Path); }
        }
        var path = TempPath();
        try
        {
            var setup = CreateScenario("available", SourceSerial, "failure-race", path, DocumentSerial);
            var gathered = Gather(setup);
            var pinned = CaseStateStore.CreateBasisVersion(1, gathered.Content, string.Empty);
            setup.Store.Commit(setup.Scope, setup.Store.Read(setup.Scope).Revision,
                new CaseCommit(pinned, null, "presenter", "submit-evidence", "initial source basis"));
            var before = setup.Store.Read(setup.Scope);
            setup.Technical.Records = [TechnicalRecord("failure-race", "SYN-SN-77202")];
            var racing = new RacingStore(setup.Store);
            var execution = new AssessmentExecutionService(
                racing, setup.Inbox, setup.Assessments, setup.Technical, setup.Maintenance, setup.Finance,
                setup.Records, setup.Contracts, setup.Modes);
            Assert.Throws<RevisionConflictException>(() =>
                execution.Assess(setup.Scope, setup.Basis, [Claim(DocumentSerial)]));
            var intervening = Assert.IsType<CaseState>(racing.AfterRace);
            var after = setup.Store.Read(setup.Scope);
            Assert.Equal(before.Basis, after.Basis);
            Assert.Equal(before.Basis.Select(version =>
                    (version.Version, version.Content, version.ContentDigest, version.BasisDigest)),
                after.Basis.Select(version =>
                    (version.Version, version.Content, version.ContentDigest, version.BasisDigest)));
            Assert.Equal(intervening.Audit, after.Audit);
            Assert.Empty(setup.Inbox.Requests);
            Assert.Empty(setup.Store.ListWorkItems(setup.Scope));
            Assert.Equal(0, after.Audit.Count(audit => audit.Action == "mock-response.accepted"));
        }
        finally { Delete(path); }
    }

    private static Scenario CreateScenario(string mode, string sourceSerial, string? caseId = null, string? existingPath = null, string? documentSerial = null)
    {
        caseId ??= CaseId;
        var path = existingPath ?? TempPath();
        var store = new CaseStateStore(path);
        var scope = store.CreateCase(caseId, "presenter");
        var technical = new ProbeSource<TechnicalPayload>("technical", [TechnicalRecord(caseId, sourceSerial)]);
        var maintenance = new ProbeSource<MaintenancePayload>("maintenance", []); var finance = new ProbeSource<FinancePayload>("finance", []);
        var records = new ProbeSource<RecordsPayload>("records", []); var contracts = new ProbeSource<ContractsPayload>("contracts", []);
        var modes = new SourceModeStore(store, TimeProvider.System);
        if (mode != SourceModes.Available)
            modes.Set(scope, "presenter", $"mode-{mode}", store.Read(scope).Revision, "technical", mode);
        var inbox = new MockRequestInbox();
        var assessments = new AssessmentStore();
        var basis = new AssessmentBasis(
            "basis-source-mode", 1, ScenarioTime, Complete, false,
            new Dictionary<string, BasisDocument>
            {
                [DocumentId] = BasisDocument.Create(DocumentId, [$"Engine serial {documentSerial ?? sourceSerial}."]),
            });
        assessments.SetCurrentBasis(caseId, basis);
        var execution = new AssessmentExecutionService(
            store, inbox, assessments, technical, maintenance, finance, records, contracts, modes);
        return new Scenario(path, store, scope, modes, technical, maintenance, finance, records, contracts, inbox, assessments, basis, execution);
    }

    private static AssessmentClaim Claim(string serial) =>
        new(ClaimKind.Baseline, serial, "Engine", serial, [new DocumentWitness(DocumentId, 1, $"Engine serial {serial}.")]);

    private static SourceRecord<TechnicalPayload> TechnicalRecord(string caseId, string serial) => new()
    {
        SourceId = "technical", RecordId = "tech-b-component-001", RecordVersion = 1,
        AsOf = new DateOnly(2026, 10, 1), CaseId = caseId, Kind = "component_identity",
        Payload = new TechnicalPayload(new Dictionary<string, string> { ["serialNumber"] = serial, ["position"] = "engine_1" }),
        Synthetic = true,
    };

    private static AssessedWitness SourceWitness(int version) =>
        new($"source:technical:tech-b-component-001:v{version}", 0);

    private static GatheredSourceBasis Gather(Scenario scenario) =>
        EvidenceIntake.GatherSourceBasisAsync(scenario.Scope.CaseId, scenario.Technical, scenario.Maintenance, scenario.Finance,
            scenario.Records, scenario.Contracts).GetAwaiter().GetResult();

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"source-mode-findings-{Guid.NewGuid():N}.json");
    private static void Delete(string path)
    {
        foreach (var file in new[] { path, path + ".lock", path + ".tmp" })
            if (File.Exists(file)) File.Delete(file);
    }
}
