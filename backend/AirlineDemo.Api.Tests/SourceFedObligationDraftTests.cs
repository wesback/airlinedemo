using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Obligations;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Tests;

public sealed class SourceFedObligationDraftTests
{
    private const string CaseId = "obligation-source-case";
    private static readonly DateOnly ScenarioDate = new(2026, 10, 1);
    private static readonly DateTimeOffset DueAfterScenario = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    private sealed class FakeSource<TPayload>(string sourceId, IReadOnlyList<SourceRecord<TPayload>> records)
        : ISourceSystem<TPayload> where TPayload : class
    {
        public Task<SourceReadResult<IReadOnlyList<SourceRecord<TPayload>>>> ListAsync(
            string caseId, CancellationToken cancellationToken = default, int? maxRecords = null) =>
            Task.FromResult<SourceReadResult<IReadOnlyList<SourceRecord<TPayload>>>>(
                new SourceReadSucceeded<IReadOnlyList<SourceRecord<TPayload>>>(records
                    .Where(record => record.CaseId == caseId && record.SourceId == sourceId)
                    .Take(maxRecords ?? int.MaxValue).ToArray()));

        public Task<SourceReadResult<SourceRecord<TPayload>>> GetAsync(
            string caseId, string recordId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class Scenario(string path, CaseStateStore store, CaseScope scope) : IDisposable
    {
        public CaseStateStore Store { get; } = store;
        public CaseScope Scope { get; } = scope;

        public void Dispose()
        {
            foreach (var file in new[] { path, path + ".lock", path + ".tmp" })
                if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task SourceOutcomesMatchExactStateTableAndPinnedCitation()
    {
        using var scenario = await CreateScenario(Contract());
        var citation = new ObligationSourceCitation("contracts", "contract-clause", 7);
        var cases = new[]
        {
            (Make([]), ObligationState.Blocked, new[] { "no linked requirements" }, false),
            (Make([new LinkedRequirement("REQ-A", RequirementEvidence.Ambiguous, 1)]),
                ObligationState.Blocked, new[] { "REQ-A: ambiguous evidence" }, false),
            (Make([Satisfied("REQ-STALE", 0)]), ObligationState.NeedsReview,
                new[] { "REQ-STALE: evidence is not on current basis v1" }, false),
            (Make([Missing("REQ-OVERDUE")], due: new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero)),
                ObligationState.NeedsReview,
                new[] { "REQ-OVERDUE: evidence missing", "due 2026-09-30T00:00:00.0000000+00:00 passed with requirements unresolved" }, false),
            (Make([Missing("REQ-MISSING")]), ObligationState.AtRisk,
                new[] { "REQ-MISSING: evidence missing" }, true),
            (Make([Satisfied("REQ-EVIDENCED")]), ObligationState.Evidenced,
                new[] { "awaiting authorized technical review on current basis" }, false),
            (Make([Satisfied("REQ-ACCEPTED")], review: new TechnicalReview("reviewer-1", true, 1)),
                ObligationState.Accepted, Array.Empty<string>(), false),
        };

        foreach (var (obligation, expectedState, expectedReasons, createsDraft) in cases)
        {
            var result = Evaluate(scenario, obligation);

            Assert.Equal(expectedState, result.State);
            Assert.Equal(expectedReasons, result.Reasons);
            Assert.Equal(citation, result.Citation);
            Assert.Equal(createsDraft, result.NewDraft is not null);
            Assert.Equal(createsDraft ? 1 : 0, result.Drafts.Count);
            if (createsDraft)
            {
                Assert.Equal(citation, result.NewDraft!.Citation);
                Assert.Equal((citation.SourceId, citation.RecordId, citation.RecordVersion),
                    (result.NewDraft.Citation!.SourceId, result.NewDraft.Citation.RecordId, result.NewDraft.Citation.RecordVersion));
            }
        }
    }

    [Fact]
    public async Task ScenarioDateAndInclusiveUtcWindowUseExactBoundaries()
    {
        var cases = new[]
        {
            ("2026-10-02", "2026-10-31", false),
            ("2026-10-01", "2026-10-31", true),
            ("2026-09-01", "2026-10-01", true),
            ("2026-09-01", "2026-09-30", false),
        };

        foreach (var (start, end, createsDraft) in cases)
        {
            using var scenario = await CreateScenario(Contract(start: start, end: end));
            var result = Evaluate(scenario, Make([Missing()]));

            Assert.Equal(ObligationState.AtRisk, result.State);
            Assert.Equal(createsDraft, result.NewDraft is not null);
            Assert.Equal(createsDraft ? 1 : 0, result.Drafts.Count);
            if (createsDraft)
                Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), result.NewDraft!.CreatedUtc);
        }
    }

    [Fact]
    public async Task MissingContractOrWindowDateBlocksEvaluation()
    {
        var cases = new[]
        {
            (new[] { Contract(kind: "specialist_flag") }, "contracts_record_missing"),
            (new[] { Contract(start: null) }, "notice_window_start_missing_or_invalid"),
            (new[] { Contract(end: null) }, "notice_window_end_missing_or_invalid"),
        };

        foreach (var (records, reason) in cases)
        {
            using var scenario = await CreateScenario(records);
            var result = Evaluate(scenario, Make([Missing()]));

            Assert.Equal(ObligationState.Blocked, result.State);
            Assert.Equal(new[] { reason }, result.Reasons);
            Assert.Null(result.Citation);
            Assert.Null(result.NewDraft);
            Assert.Empty(result.Drafts);
        }
    }

    [Fact]
    public async Task SourceFailureNeverCreatesOrSendsNotice()
    {
        using var scenario = await CreateScenario(Contract());
        var sender = new CountingNoticeSender();
        var obligation = Make([Missing()]);
        var cases = new[]
        {
            ("unavailable", "source_unavailable"),
            ("stale", "source_stale"),
            ("conflicting", "source_conflict"),
        };

        foreach (var (mode, reason) in cases)
        {
            var result = ObligationEvaluator.EvaluateFromPinnedContracts(obligation, CaseId, 1,
                scenario.Store.Read(scenario.Scope).Basis[^1].Content, Modes(mode), [], sender);

            Assert.Equal(ObligationState.Blocked, result.State);
            Assert.Equal(new[] { reason }, result.Reasons);
            Assert.Null(result.NewDraft);
            Assert.Empty(result.Drafts);
            Assert.Empty(sender.Sent);
        }

        var available = ObligationEvaluator.EvaluateFromPinnedContracts(obligation, CaseId, 1,
            scenario.Store.Read(scenario.Scope).Basis[^1].Content, Modes("available"), [], sender);
        Assert.Equal(ObligationState.AtRisk, available.State);
        Assert.NotNull(available.NewDraft);
        Assert.Empty(sender.Sent);
    }

    private static Obligation Make(IReadOnlyList<LinkedRequirement> requirements,
        TechnicalReview? review = null, DateTimeOffset? due = null) =>
        new("OBL-1", 1, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31),
            due ?? DueAfterScenario, requirements, review);

    private static LinkedRequirement Missing(string id = "REQ-1") =>
        new(id, RequirementEvidence.Missing, null);

    private static LinkedRequirement Satisfied(string id, int basisVersion = 1) =>
        new(id, RequirementEvidence.Satisfied, basisVersion);

    private static ObligationEvaluation Evaluate(Scenario scenario, Obligation obligation) =>
        ObligationEvaluator.EvaluateFromPinnedContracts(obligation, CaseId, 1,
            scenario.Store.Read(scenario.Scope).Basis[^1].Content, Modes("available"), []);

    private static IReadOnlyDictionary<string, string> Modes(string contractsMode) =>
        SourceModes.Sources.ToDictionary(source => source,
            source => source == "contracts" ? contractsMode : SourceModes.Available, StringComparer.Ordinal);

    private static async Task<Scenario> CreateScenario(params SourceRecord<ContractsPayload>[] contracts)
    {
        var path = Path.Combine(Path.GetTempPath(), $"source-fed-obligation-{Guid.NewGuid():N}.json");
        var store = new CaseStateStore(path);
        var scope = store.CreateCase(CaseId, "presenter");
        var gathered = await EvidenceIntake.GatherSourceBasisAsync(CaseId,
            new FakeSource<TechnicalPayload>("technical", []),
            new FakeSource<MaintenancePayload>("maintenance", []),
            new FakeSource<FinancePayload>("finance", []),
            new FakeSource<RecordsPayload>("records", []),
            new FakeSource<ContractsPayload>("contracts", contracts));
        store.Commit(scope, 0, new CaseCommit(CaseStateStore.CreateBasisVersion(1, gathered.Content, string.Empty),
            null, "presenter", "source-basis.gathered", "obligation test"));
        return new Scenario(path, store, scope);
    }

    private static SourceRecord<ContractsPayload> Contract(
        string? start = "2026-10-01", string? end = "2026-10-31", string kind = "obligation_clause")
    {
        var fields = new Dictionary<string, string>
        {
            ["clauseId"] = "SYN-CLAUSE-1",
            ["obligation"] = "return_condition_report",
            ["noticeWindowDays"] = "30",
        };
        if (start is not null) fields["noticeWindowStart"] = start;
        if (end is not null) fields["noticeWindowEnd"] = end;
        return new SourceRecord<ContractsPayload>
        {
            SourceId = "contracts",
            RecordId = "contract-clause",
            RecordVersion = 7,
            AsOf = ScenarioDate,
            CaseId = CaseId,
            Kind = kind,
            Payload = new ContractsPayload(fields),
            Synthetic = true,
        };
    }

    private sealed class CountingNoticeSender : INoticeSender
    {
        public List<NoticeDraft> Sent { get; } = [];
        public void Send(NoticeDraft draft) => Sent.Add(draft);
    }
}
