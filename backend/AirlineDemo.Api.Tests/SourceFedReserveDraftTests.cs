using System.Text.Json;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Finance;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Requests;
using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Tests;

public sealed class SourceFedReserveDraftTests
{
    private const string CaseId = "reserve-source-case";
    private const string Period = "2026-09";
    private static readonly DateOnly AsOf = new(2026, 10, 1);

    private sealed class FakeSource<T>(string id, IReadOnlyList<SourceRecord<T>> records) : ISourceSystem<T> where T : class
    {
        public IReadOnlyList<SourceRecord<T>> Records { get; set; } = records;
        public int Calls { get; private set; }
        public void ResetCalls() => Calls = 0;
        public Task<SourceReadResult<IReadOnlyList<SourceRecord<T>>>> ListAsync(
            string caseId, CancellationToken cancellationToken = default, int? maxRecords = null)
        {
            Calls++;
            return Task.FromResult<SourceReadResult<IReadOnlyList<SourceRecord<T>>>>(new SourceReadSucceeded<IReadOnlyList<SourceRecord<T>>>(
                Records.Where(record => record.CaseId == caseId && record.SourceId == id).Take(maxRecords ?? int.MaxValue).ToArray()));
        }
        public Task<SourceReadResult<SourceRecord<T>>> GetAsync(
            string caseId, string recordId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class Scenario(string path) : IDisposable
    {
        public required CaseStateStore Store { get; init; }
        public required CaseScope Scope { get; init; }
        public required FakeSource<TechnicalPayload> Technical { get; init; }
        public required FakeSource<FinancePayload> Finance { get; init; }
        public required SourceModeStore Modes { get; init; }
        public required MockRequestInbox Inbox { get; init; }
        public void Dispose() { foreach (var file in new[] { path, path + ".lock", path + ".tmp" }) if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public async Task LinesMatchPinnedTechnicalAndFinanceInputsAndHandCalculation()
    {
        using var scenario = await CreateScenario(Technical("1.25", "1"),
            Rate("rate-hour", "per_flight_hour", "0.10", 3),
            Rate("rate-cycle", "per_cycle", "12.345", 4), Reimbursement());
        var basis = scenario.Store.Read(scenario.Scope).Basis[^1].Content;
        scenario.Technical.Records = [Technical("99", "99", 8)];
        scenario.Finance.Records =
        [
            Rate("changed-hour", "per_flight_hour", "99", 9),
            Rate("changed-cycle", "per_cycle", "99", 10),
        ];

        var draft = Evaluate(scenario, basis);
        Assert.Equal(ReserveDraftStatus.ReadyForReview, draft.Status);
        Assert.Equal(
        [
            new ReserveLine("per_flight_hour", 1.25m, 0.10m, 0.13m, null)
            {
                UtilizationSource = new("technical", "tech-utilization", 1), RateSource = new("finance", "rate-hour", 3),
            },
            new ReserveLine("per_cycle", 1m, 12.345m, 12.35m, null)
            {
                UtilizationSource = new("technical", "tech-utilization", 1), RateSource = new("finance", "rate-cycle", 4),
            },
        ], draft.Lines);
        Assert.Equal(12.48m, draft.PayableTotal);
        Assert.Equal(new FinanceReserveLine("claim-1", "reserve_reimbursement", "USD", 25m, 5m, 20m, 0m, 25m)
            { Source = new("finance", "claim-1", 1) }, Assert.Single(draft.FinanceLines));
        Assert.Equal(20m, draft.FinanceTotal);
        using var pins = JsonDocument.Parse(basis);
        var values = pins.RootElement.GetProperty("pins").EnumerateArray().ToArray();
        AssertPinned(values, "tech-utilization", "technical", 1, ("hours", "1.25"), ("cycles", "1"));
        AssertPinned(values, "rate-hour", "finance", 3, ("rateBasis", "per_flight_hour"), ("rate", "0.10"));
        Assert.DoesNotContain(values, pin => pin.GetProperty("recordId").GetString() == "changed-hour");
    }

    [Fact]
    public async Task MissingInputsBlockAndFinanceKindsRemainSeparate()
    {
        var cases = new[]
        {
            ("rent", new[] { Rate("rate-hour", "per_flight_hour", "0.10", full: true), Rent() }),
            ("security_deposit", new[] { Rate("rate-hour", "per_flight_hour", "0.10", full: true), Deposit() }),
            ("maintenance_reserve", new[] { Rate("rate-hour", "per_flight_hour", "0.10", full: true) }),
            ("reserve_reimbursement", new[] { Rate("rate-hour", "per_flight_hour", "0.10", full: true), Reimbursement() }),
        };
        foreach (var (kind, finance) in cases)
        {
            using var scenario = await CreateScenario(Technical("5", "1"), finance);
            var draft = Evaluate(scenario);
            Assert.Equal(ReserveDraftStatus.ReadyForReview, draft.Status);
            Assert.Equal(0.50m, draft.PayableTotal);
            Assert.Equal([new ReserveLine("per_flight_hour", 5m, 0.10m, 0.50m, null)
                { UtilizationSource = new("technical", "tech-utilization", 1), RateSource = new("finance", "rate-hour", 1) }], draft.Lines);
            var expected = new List<FinanceReserveLine>
            {
                new("rate-hour", "maintenance_reserve", "USD", 100m, 70m, 30m, 30m, 0m)
                    { Source = new("finance", "rate-hour", 1) },
            };
            if (kind == "reserve_reimbursement")
                expected.Add(new("claim-1", "reserve_reimbursement", "USD", 25m, 5m, 20m, 0m, 25m)
                    { Source = new("finance", "claim-1", 1) });
            Assert.Equal(expected, draft.FinanceLines);
            Assert.Equal(kind == "reserve_reimbursement" ? 50m : 30m, draft.FinanceTotal);
            Assert.Equal(ReserveCalculator.SourceFedCalculationBasis, draft.CalculationBasis);
        }

        var rejected = new[]
        {
            (Technical("5", "1"), Rate("bad-rate", "per_flight_hour", null),
                "finance-record-invalid", "Record bad-rate has an invalid or missing rate."),
            (Technical("5", "1"), Deposit() with { Kind = "maintenance_reserve", Payload = new(CommonFields()) },
                "finance-record-invalid", "Record deposit-1 has an invalid or missing rateBasis."),
            (Technical(null, "1"), Rate("rate-hour", "per_flight_hour", "0.10"),
                "usage-missing", "Required technical utilization field 'hours' is missing or invalid."),
        };
        foreach (var (technical, finance, code, detail) in rejected)
        {
            using var scenario = await CreateScenario(technical, finance);
            var draft = Evaluate(scenario);
            Assert.Equal(ReserveDraftStatus.Blocked, draft.Status);
            Assert.Equal(new BlockReason(code, detail), Assert.Single(draft.Blockers));
            Assert.Empty(draft.Lines); Assert.Empty(draft.FinanceLines);
            Assert.Null(draft.PayableTotal); Assert.Null(draft.FinanceTotal);
        }
    }

    [Fact]
    public async Task NonAvailableSourceBlocksWithoutRequestOrAcceptance()
    {
        var cases = new[]
        {
            ("unavailable", "available", "source_unavailable", "technical"),
            ("stale", "available", "source_stale", "technical"),
            ("available", "unavailable", "source_unavailable", "finance"),
            ("available", "stale", "source_stale", "finance"),
            ("available", "available", "", ""),
        };
        foreach (var (technicalMode, financeMode, reason, changedSource) in cases)
        {
            using var scenario = await CreateScenario(Technical(), Rate("rate-hour", "per_flight_hour", "0.10"));
            scenario.Technical.ResetCalls();
            scenario.Finance.ResetCalls();
            if (changedSource.Length > 0)
            {
                var state = scenario.Store.Read(scenario.Scope);
                var mode = changedSource == "technical" ? technicalMode : financeMode;
                Assert.Equal(SourceModeOutcome.Recorded, scenario.Modes.Set(scenario.Scope, "presenter",
                    $"mode-{changedSource}-{mode}", state.Revision, changedSource, mode).Outcome);
            }

            await AssertModeRead(scenario.Technical, "technical", technicalMode);
            await AssertModeRead(scenario.Finance, "finance", financeMode);

            var draft = Evaluate(scenario);
            if (reason.Length == 0)
            {
                Assert.Equal((ReserveDraftStatus.ReadyForReview, 0.50m), (draft.Status, draft.PayableTotal));
            }
            else
            {
                Assert.Equal(ReserveDraftStatus.Blocked, draft.Status);
                Assert.Equal(reason, Assert.Single(draft.Blockers).Code);
                Assert.NotEmpty(draft.Blockers[0].Detail);
                Assert.Empty(draft.Lines);
                Assert.Null(draft.PayableTotal);
            }
            Assert.Empty(scenario.Inbox.Requests);
            Assert.DoesNotContain(scenario.Store.ReadAudit(scenario.Scope), audit =>
                audit.Action == MockResponseService.AcceptedAction);
        }
    }

    private static void AssertPinned(JsonElement[] pins, string id, string source, int version,
        params (string Field, string Value)[] expected)
    {
        var pin = Assert.Single(pins.Where(item => item.GetProperty("recordId").GetString() == id));
        Assert.Equal((source, id, version), (pin.GetProperty("sourceId").GetString(),
            pin.GetProperty("recordId").GetString(), pin.GetProperty("recordVersion").GetInt32()));
        using var record = JsonDocument.Parse(pin.GetProperty("content").GetString()!);
        var fields = record.RootElement.GetProperty("payload").GetProperty("fields");
        foreach (var (field, value) in expected) Assert.Equal(value, fields.GetProperty(field).GetString());
    }

    private static async Task AssertModeRead<T>(FakeSource<T> source, string id, string mode) where T : class
    {
        var result = await SourceModeFindingPolicy.Apply(source, id, mode).ListAsync(CaseId);
        if (mode == "unavailable") Assert.IsType<SourceUnavailable<IReadOnlyList<SourceRecord<T>>>>(result);
        if (mode == "stale")
            Assert.True(Assert.Single(Assert.IsType<SourceReadSucceeded<IReadOnlyList<SourceRecord<T>>>>(result).Value).Stale);
        Assert.Equal(mode == "unavailable" ? 0 : 1, source.Calls);
    }

    private static SourceFedReserveDraft Evaluate(Scenario scenario, string? content = null)
    {
        var state = scenario.Store.Read(scenario.Scope);
        return ReserveCalculator.CalculateFromPinnedSources("RSV-test", 1, Period, CaseId,
            content ?? state.Basis[^1].Content, scenario.Modes.ModesFor(state));
    }

    private static async Task<Scenario> CreateScenario(SourceRecord<TechnicalPayload> technical, params SourceRecord<FinancePayload>[] finance)
    {
        var path = Path.Combine(Path.GetTempPath(), $"source-fed-reserve-{Guid.NewGuid():N}.json");
        var store = new CaseStateStore(path); var scope = store.CreateCase(CaseId, "presenter");
        var tech = new FakeSource<TechnicalPayload>("technical", [technical]); var fin = new FakeSource<FinancePayload>("finance", finance);
        var gathered = await EvidenceIntake.GatherSourceBasisAsync(CaseId, tech, new FakeSource<MaintenancePayload>("maintenance", []),
            fin, new FakeSource<RecordsPayload>("records", []), new FakeSource<ContractsPayload>("contracts", []));
        store.Commit(scope, 0, new CaseCommit(CaseStateStore.CreateBasisVersion(1, gathered.Content, string.Empty),
            null, "presenter", "source-basis.gathered", "reserve test"));
        return new Scenario(path) { Store = store, Scope = scope, Technical = tech, Finance = fin, Modes = new SourceModeStore(store, TimeProvider.System), Inbox = new MockRequestInbox() };
    }

    private static SourceRecord<TechnicalPayload> Technical(string? hours = "5", string? cycles = "1", int version = 1)
    {
        var fields = new Dictionary<string, string> { ["periodStart"] = "2026-09-01", ["periodEnd"] = "2026-09-30" };
        if (hours is not null) fields["hours"] = hours;
        if (cycles is not null) fields["cycles"] = cycles;
        return new() { SourceId = "technical", RecordId = "tech-utilization", RecordVersion = version, AsOf = AsOf, CaseId = CaseId,
            Kind = "utilization", Payload = new(fields), Synthetic = true };
    }

    private static SourceRecord<FinancePayload> Rate(string id, string basis, string? rate, int version = 1, bool full = false)
    {
        var fields = full ? CommonFields() : new Dictionary<string, string> { ["period"] = Period };
        fields["rateBasis"] = basis;
        if (rate is not null) fields["rate"] = rate;
        return Finance(id, "maintenance_reserve", fields, version);
    }

    private static SourceRecord<FinancePayload> Reimbursement() => Finance("claim-1", "reserve_reimbursement",
        CommonFields(("claimId", "claim-1"), ("status", "approved"), ("recordedAmount", "25.00"), ("collectedAmount", "5.00"), ("dueDate", "2026-09-30")));
    private static SourceRecord<FinancePayload> Rent() => Finance("rent-1", "rent", CommonFields());
    private static SourceRecord<FinancePayload> Deposit() => Finance("deposit-1", "security_deposit", CommonFields());
    private static Dictionary<string, string> CommonFields(params (string Key, string Value)[] overrides)
    {
        var fields = new Dictionary<string, string> { ["recordedAmount"] = "100.00", ["collectedAmount"] = "70.00",
            ["currency"] = "USD", ["period"] = Period, ["dueDate"] = "2026-09-20" };
        foreach (var (key, value) in overrides) fields[key] = value;
        return fields;
    }
    private static SourceRecord<FinancePayload> Finance(string id, string kind, IReadOnlyDictionary<string, string> fields, int version = 1) =>
        new() { SourceId = "finance", RecordId = id, RecordVersion = version, AsOf = AsOf, CaseId = CaseId,
            Kind = kind, Payload = new(fields), Synthetic = true };
}
