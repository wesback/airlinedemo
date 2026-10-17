using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Policy;
using AirlineDemo.Api.Requests;
using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Tests;
public sealed class SourceBasisPinningTests
{
    private const string CaseId = "source-pin-case";
    private const string ScenarioTime = "2026-10-14T00:00:00Z";
    private sealed class MutableSource<TPayload>(string sourceId) : ISourceSystem<TPayload>
        where TPayload : class
    {
        public IReadOnlyList<SourceRecord<TPayload>> Records { get; set; } = [];
        public Task<SourceReadResult<IReadOnlyList<SourceRecord<TPayload>>>> ListAsync(
            string caseId, CancellationToken cancellationToken = default, int? maxRecords = null) =>
            Task.FromResult<SourceReadResult<IReadOnlyList<SourceRecord<TPayload>>>>(
                new SourceReadSucceeded<IReadOnlyList<SourceRecord<TPayload>>>(
                    Records.Where(record => record.CaseId == caseId && record.SourceId == sourceId)
                        .Take(maxRecords ?? int.MaxValue).ToArray()));
        public Task<SourceReadResult<SourceRecord<TPayload>>> GetAsync(string caseId, string recordId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
    private sealed record SourceSet(
        MutableSource<TechnicalPayload> Technical,
        MutableSource<MaintenancePayload> Maintenance,
        MutableSource<FinancePayload> Finance,
        MutableSource<RecordsPayload> Records,
        MutableSource<ContractsPayload> Contracts)
    {
        public AssessmentExecutionService Service(ICaseStateStore store, AssessmentStore assessments) =>
            new(store, new MockRequestInbox(), assessments, Technical, Maintenance, Finance, Records, Contracts);
    }
    private sealed class RevisionRacingStore(ICaseStateStore inner) : ICaseStateStore
    {
        private bool _advanced;
        public CaseState? AfterInterveningCommit { get; private set; }
        public CaseScope AuthorizeCase(string caseId, string owner) => inner.AuthorizeCase(caseId, owner);
        public string? OwnerOf(string caseId) => inner.OwnerOf(caseId);
        public CaseScope CreateCase(string caseId, string owner) => inner.CreateCase(caseId, owner);
        public CaseState Read(CaseScope scope)
        {
            var observed = inner.Read(scope);
            if (!_advanced)
            {
                _advanced = true;
                inner.Commit(scope, observed.Revision, new CaseCommit(null, null, "concurrent-writer", "concurrent-update", "intervening revision"));
                AfterInterveningCommit = inner.Read(scope);
            }
            return observed;
        }
        public BasisVersion GetBasisVersion(CaseScope scope, int version) => inner.GetBasisVersion(scope, version);
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => inner.ReadAudit(scope);
        public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit) =>
            inner.Commit(scope, expectedRevision, commit);
        public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope) => inner.ListWorkItems(scope);
        public CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor) =>
            inner.CreateWorkItem(scope, expectedRevision, draft, actor);
        public CommitResult ChangeWorkItemState(
            CaseScope scope, long expectedRevision, string itemId, string state, string actor) =>
            inner.ChangeWorkItemState(scope, expectedRevision, itemId, state, actor);
        public void AppendTrace(CaseScope scope, InvestigationTrace trace) => inner.AppendTrace(scope, trace);
        public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId) =>
            inner.FindTrace(scope, caseId, runId, investigationId);
    }

    [Fact]
    public async Task AC1_GatheredBasisStoresExactPinsDeterministicallyAndRejectsMissingPins()
    {
        var path = StatePath();
        var emptyPath = StatePath();
        try
        {
            var store = new CaseStateStore(path);
            var scope = store.CreateCase(CaseId, "presenter");
            var sources = Sources(1, "engine-serial-123");
            var first = await Gather(sources);
            var repeated = await Gather(sources);
            Assert.Equal(first.Content, repeated.Content);
            var pin = Assert.Single(first.Pins);
            Assert.Equal("technical", pin.SourceId);
            Assert.Equal("engine-record-1", pin.RecordId);
            Assert.Equal(1, pin.RecordVersion);
            Assert.Equal(Digest(pin.Content), pin.ContentDigest);
            var basis = Basis("basis-source-pin", 0);
            var assessments = new AssessmentStore();
            sources.Service(store, assessments).Assess(scope, basis, []);
            var refreshed = assessments.CurrentBasis(CaseId)!;
            var stored = Assert.Single(store.Read(scope).Basis);
            Assert.Equal(1, refreshed.BasisVersion);
            Assert.Equal(first.Content, stored.Content);
            Assert.Equal(Digest(first.Content).ToUpperInvariant(), stored.ContentDigest);
            using (var snapshot = JsonDocument.Parse(stored.Content))
            {
                var storedPin = Assert.Single(snapshot.RootElement.GetProperty("pins").EnumerateArray());
                Assert.Equal("technical", storedPin.GetProperty("sourceId").GetString());
                Assert.Equal("engine-record-1", storedPin.GetProperty("recordId").GetString());
                Assert.Equal(1, storedPin.GetProperty("recordVersion").GetInt32());
                Assert.Equal(pin.ContentDigest, storedPin.GetProperty("contentDigest").GetString());
                Assert.Equal(pin.Content, storedPin.GetProperty("content").GetString());
            }
            Assert.Equal(
                "{\"asOf\":\"2026-10-01\",\"caseId\":\"source-pin-case\",\"kind\":\"component\",\"payload\":{\"fields\":{\"SerialNumber\":\"exact-case-key\",\"component\":\"engine\",\"serialNumber\":\"engine-serial-123\"}},\"recordId\":\"engine-record-1\",\"recordVersion\":1,\"sourceId\":\"technical\",\"stale\":false,\"synthetic\":true}",
                pin.Content);
            Assert.Throws<ArgumentException>(() => EvidenceIntake.CreateSourceBasis([]));
            var emptyStore = new CaseStateStore(emptyPath);
            var emptyScope = emptyStore.CreateCase("empty-pin-case", "presenter");
            const string noPins = "{\"format\":\"source-record-pins-v1\",\"pins\":[]}";
            var emptyBasis = CaseStateStore.CreateBasisVersion(1, noPins, string.Empty);
            Assert.Throws<BasisIntegrityException>(() => emptyStore.Commit(
                emptyScope, 0, new CaseCommit(emptyBasis, null, "assessment-service", "source-basis.gathered", "no pins")));
            Assert.Empty(emptyStore.Read(emptyScope).Basis);
            Assert.Empty(emptyStore.ReadAudit(emptyScope));
        }
        finally
        {
            DeleteState(path);
            DeleteState(emptyPath);
        }
    }

    [Fact]
    public void BasisContainingFormatNameAsOrdinaryTextIsNotParsedAsPins()
    {
        var path = StatePath();
        try
        {
            var store = new CaseStateStore(path);
            var scope = store.CreateCase(CaseId, "presenter");
            const string ordinaryEvidence = "This evidence discusses source-record-pins-v1 but is not a pin document.";
            var basis = CaseStateStore.CreateBasisVersion(1, ordinaryEvidence, string.Empty);

            store.Commit(scope, 0, new CaseCommit(basis, null, "presenter", "submit-evidence", "ordinary evidence"));

            var stored = Assert.Single(store.Read(scope).Basis);
            Assert.Equal(ordinaryEvidence, stored.Content);
        }
        finally
        {
            DeleteState(path);
        }
    }

    [Theory]
    [InlineData("{\"format\" : \"source-record-pins-v1\", \"pins\": [")]
    [InlineData("{\"f\\u006Frmat\":\"source-record-pins-v1\",\"pins\":[")]
    [InlineData("{\"pins\":],\"format\":\"source-record-pins-v1\"}")]
    public void MalformedSourcePinJsonIsRejectedWithoutChangingState(string malformedPins)
    {
        var path = StatePath();
        try
        {
            var store = new CaseStateStore(path);
            var scope = store.CreateCase(CaseId, "presenter");
            var basis = CaseStateStore.CreateBasisVersion(1, malformedPins, string.Empty);
            var before = store.Read(scope);

            Assert.Throws<BasisIntegrityException>(() => store.Commit(
                scope, before.Revision,
                new CaseCommit(basis, null, "assessment-service", "source-basis.gathered", "malformed pins")));

            var after = store.Read(scope);
            Assert.Equal(before.Basis, after.Basis);
            Assert.Equal(before.Audit, after.Audit);
            Assert.Equal(before.Revision, after.Revision);
        }
        finally
        {
            DeleteState(path);
        }
    }

    [Fact]
    public void AC2_ReassessmentAppendsChangedVersionAndPreservesPriorBasisIdentityAndContent()
    {
        var path = StatePath();
        try
        {
            var store = new CaseStateStore(path);
            var scope = store.CreateCase(CaseId, "presenter");
            var initial = CaseStateStore.CreateBasisVersion(1, "initial accepted basis", string.Empty);
            store.Commit(scope, 0, new CaseCommit(initial, null, "presenter", "submit-evidence", "basis 1"));
            var sources = Sources(1, "engine-serial-123");
            var assessments = new AssessmentStore();
            var service = sources.Service(store, assessments);
            var basis = Basis("basis-immutable-id", 1);
            assessments.SetCurrentBasis(CaseId, basis);
            service.Assess(scope, basis, []);
            var priorSourceBasis = store.Read(scope).Basis[1];
            var firstAssessmentBasis = Assert.IsType<AssessmentBasis>(assessments.CurrentBasis(CaseId));
            Assert.Equal("basis-immutable-id", firstAssessmentBasis.BasisId);
            Assert.Equal(2, firstAssessmentBasis.BasisVersion);
            Assert.Equal(1, Assert.Single(firstAssessmentBasis.SourcePins!).RecordVersion);
            sources.Technical.Records = [TechnicalRecord(2, "engine-serial-456")];
            service.Assess(scope, firstAssessmentBasis, []);
            var changedState = store.Read(scope);
            Assert.Equal(3, changedState.Basis.Count);
            Assert.Equal(priorSourceBasis, changedState.Basis[1]);
            using var updatedSnapshot = JsonDocument.Parse(changedState.Basis[2].Content);
            var updatedPin = Assert.Single(updatedSnapshot.RootElement.GetProperty("pins").EnumerateArray());
            Assert.Equal(2, updatedPin.GetProperty("recordVersion").GetInt32());
            Assert.Equal(Digest(updatedPin.GetProperty("content").GetString()!),
                updatedPin.GetProperty("contentDigest").GetString());
            using var updatedRecord = JsonDocument.Parse(updatedPin.GetProperty("content").GetString()!);
            Assert.Equal("engine-serial-456",
                updatedRecord.RootElement.GetProperty("payload").GetProperty("fields").GetProperty("serialNumber").GetString());
            var changedAssessmentBasis = assessments.CurrentBasis(CaseId)!;
            Assert.Equal("basis-immutable-id", changedAssessmentBasis.BasisId);
            Assert.Equal(3, changedAssessmentBasis.BasisVersion);
            Assert.Equal(2, Assert.Single(changedAssessmentBasis.SourcePins!).RecordVersion);
            var beforeUnchangedRefresh = store.Read(scope);
            service.Assess(scope, changedAssessmentBasis, []);
            var afterUnchangedRefresh = store.Read(scope);
            Assert.Equal(beforeUnchangedRefresh.Basis, afterUnchangedRefresh.Basis);
        }
        finally
        {
            DeleteState(path);
        }
    }

    [Fact]
    public void AC3_RevisionConflictLeavesBasisAndAuditHistoryUnchanged()
    {
        var path = StatePath();
        try
        {
            var inner = new CaseStateStore(path);
            var scope = inner.CreateCase(CaseId, "presenter");
            inner.Commit(scope, 0, new CaseCommit(
                CaseStateStore.CreateBasisVersion(1, "prior basis", string.Empty),
                null,
                "presenter",
                "submit-evidence",
                "basis 1"));
            var racingStore = new RevisionRacingStore(inner);
            var service = Sources(1, "engine-serial-123").Service(racingStore, new AssessmentStore());
            var basis = Basis("basis-race", 1);
            Assert.Throws<RevisionConflictException>(() => service.Assess(scope, basis, []));
            var after = inner.Read(scope);
            var interleaved = Assert.IsType<CaseState>(racingStore.AfterInterveningCommit);
            Assert.Equal(interleaved.Basis, after.Basis);
            Assert.Equal(interleaved.Audit, after.Audit);
            Assert.Equal(interleaved.Revision, after.Revision);
        }
        finally
        {
            DeleteState(path);
        }
    }

    private static SourceSet Sources(int recordVersion, string serialNumber)
    {
        var technical = new MutableSource<TechnicalPayload>("technical")
        {
            Records = [TechnicalRecord(recordVersion, serialNumber)],
        };
        return new SourceSet(
            technical,
            new MutableSource<MaintenancePayload>("maintenance"),
            new MutableSource<FinancePayload>("finance"),
            new MutableSource<RecordsPayload>("records"),
            new MutableSource<ContractsPayload>("contracts"));
    }
    private static SourceRecord<TechnicalPayload> TechnicalRecord(int version, string serialNumber) =>
        new()
        {
            SourceId = "technical",
            RecordId = "engine-record-1",
            RecordVersion = version,
            AsOf = new DateOnly(2026, 10, 1),
            CaseId = CaseId,
            Kind = "component",
            Stale = false,
            Payload = new TechnicalPayload(new Dictionary<string, string>
            {
                ["SerialNumber"] = "exact-case-key",
                ["serialNumber"] = serialNumber,
                ["component"] = "engine",
            }),
            Synthetic = true,
        };
    private static Task<GatheredSourceBasis> Gather(SourceSet sources) =>
        EvidenceIntake.GatherSourceBasisAsync(CaseId, sources.Technical, sources.Maintenance,
            sources.Finance, sources.Records, sources.Contracts);
    private static AssessmentBasis Basis(string id, int version) => new(id, version, ScenarioTime,
        new ProcessingCoverage(1, 1, 0, 0), false, new Dictionary<string, BasisDocument>());
    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string StatePath() => Path.Combine(Path.GetTempPath(), $"source-basis-pinning-{Guid.NewGuid():N}.json");
    private static void DeleteState(string path)
    {
        foreach (var file in new[] { path, path + ".lock", path + ".tmp" })
            if (File.Exists(file)) File.Delete(file);
    }
}
