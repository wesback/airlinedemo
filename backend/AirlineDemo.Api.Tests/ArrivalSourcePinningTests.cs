using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.SourceSystems;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirlineDemo.Api.Tests;

public sealed class ArrivalSourcePinningTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arrival-source-pinning-" + Guid.NewGuid().ToString("N"));
    private string CasePath => Path.Combine(_directory, "cases.json");
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task RetrievalPinsExactSnapshotsAndPreservesOtherSources()
    {
        var sources = SourceSet.Create();
        using var factory = CreateFactory(sources);
        using var presenter = Session(factory, "presenter", "presenter-user");
        using var technical = Session(factory, "technical-reviewer", "technical-user");
        using var finance = Session(factory, "finance-reviewer", "finance-user");
        await Start(presenter);

        using var technicalRead = await Retrieve(technical, "technical", "technical-1", 2);
        Assert.Equal(HttpStatusCode.OK, technicalRead.StatusCode);
        var firstReceiptBody = await technicalRead.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("SN-technical", firstReceiptBody, StringComparison.Ordinal);
        using var firstReceipt = JsonDocument.Parse(firstReceiptBody);
        Assert.Equal(new[]
        {
            "contractVersion", "commandId", "caseId", "sourceId", "outcome", "priorRevision", "newRevision",
            "auditEventId", "recordRefs",
        }, firstReceipt.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("pinned", firstReceipt.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(2, firstReceipt.RootElement.GetProperty("priorRevision").GetInt64());

        using var financeRead = await Retrieve(finance, "finance", "finance-1", 3);
        Assert.Equal(HttpStatusCode.OK, financeRead.StatusCode);
        var store = factory.Services.GetRequiredService<ICaseStateStore>();
        var state = store.Read(store.AuthorizeCase("case-001", "presenter"));
        Assert.Equal(4, state.Revision);
        Assert.Equal(2, state.Basis.Count);
        Assert.Equal(CaseStateStore.CreateBasisVersion(2, state.Basis[1].Content, state.Basis[0].BasisDigest).BasisDigest,
            state.Basis[1].BasisDigest);

        using var firstSnapshot = JsonDocument.Parse(state.Basis[0].Content);
        var technicalPin = Assert.Single(firstSnapshot.RootElement.GetProperty("pins").EnumerateArray());
        Assert.Equal("technical", technicalPin.GetProperty("sourceId").GetString());
        Assert.Equal("technical-record-1", technicalPin.GetProperty("recordId").GetString());
        Assert.Equal(1, technicalPin.GetProperty("recordVersion").GetInt32());
        const string expectedTechnicalRecord =
            "{\"asOf\":\"2026-11-01\",\"caseId\":\"case-001\",\"kind\":\"technical\",\"payload\":{\"fields\":{\"serialNumber\":\"SN-technical\"}},\"recordId\":\"technical-record-1\",\"recordVersion\":1,\"sourceId\":\"technical\",\"stale\":false,\"synthetic\":true}";
        Assert.Equal(expectedTechnicalRecord, technicalPin.GetProperty("content").GetString());
        Assert.Equal(Digest(expectedTechnicalRecord), technicalPin.GetProperty("contentDigest").GetString());

        using var latestSnapshot = JsonDocument.Parse(state.Basis[^1].Content);
        var pins = latestSnapshot.RootElement.GetProperty("pins").EnumerateArray().ToArray();
        Assert.Equal(new[] { "finance", "technical" }, pins.Select(pin => pin.GetProperty("sourceId").GetString()).Order().ToArray());
        Assert.Equal("SN-finance", ReadField(pins.Single(pin => pin.GetProperty("sourceId").GetString() == "finance"), "serialNumber"));
        Assert.Equal("SN-technical", ReadField(pins.Single(pin => pin.GetProperty("sourceId").GetString() == "technical"), "serialNumber"));
        Assert.All(state.Audit.Where(item => item.Action == ArrivalJourneyService.SourceReadAction),
            item => Assert.Equal(FixedNow, item.At));
    }

    [Fact]
    public async Task NonReadyModesAndEmptyResultsNeverSatisfyArrival()
    {
        var sources = SourceSet.Create();
        sources.Maintenance.Records = [];
        using var factory = CreateFactory(sources);
        using var presenter = Session(factory, "presenter", "presenter-user");
        using var finance = Session(factory, "finance-reviewer", "finance-user");
        using var technical = Session(factory, "technical-reviewer", "technical-user");
        using var legal = Session(factory, "legal-reviewer", "legal-user");
        await Start(presenter);

        using var denied = await Retrieve(presenter, "technical", "denied-technical", 2);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var store = factory.Services.GetRequiredService<ICaseStateStore>();
        var scope = store.AuthorizeCase("case-001", "presenter");
        var modes = factory.Services.GetRequiredService<SourceModeStore>();
        Assert.Equal(SourceModeOutcome.Recorded, modes.Set(scope, "presenter-user", "set-stale", 3, "finance", "stale").Outcome);

        using var stale = await Retrieve(finance, "finance", "finance-stale", 4);
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        using var staleReceipt = JsonDocument.Parse(await stale.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("stale", staleReceipt.RootElement.GetProperty("outcome").GetString());
        Assert.Empty(staleReceipt.RootElement.GetProperty("recordRefs").EnumerateArray());

        using var empty = await Retrieve(technical, "maintenance", "maintenance-empty", 5);
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        using var emptyReceipt = JsonDocument.Parse(await empty.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("empty", emptyReceipt.RootElement.GetProperty("outcome").GetString());
        Assert.Empty(emptyReceipt.RootElement.GetProperty("recordRefs").EnumerateArray());

        Assert.Equal(SourceModeOutcome.Recorded, modes.Set(scope, "presenter-user", "set-conflicting", 6,
            "contracts", "conflicting").Outcome);
        using var conflicting = await Retrieve(legal, "contracts", "contracts-conflicting", 7);
        Assert.Equal(HttpStatusCode.OK, conflicting.StatusCode);
        using var conflictingReceipt = JsonDocument.Parse(await conflicting.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("conflicting", conflictingReceipt.RootElement.GetProperty("outcome").GetString());
        Assert.Empty(conflictingReceipt.RootElement.GetProperty("recordRefs").EnumerateArray());

        var state = store.Read(scope);
        Assert.Empty(state.Basis);
        var arrival = factory.Services.GetRequiredService<ArrivalJourneyService>().Status(state);
        Assert.False(arrival.CanComplete);
        Assert.Equal(SourceModes.Sources, arrival.MissingSources);
        Assert.Equal("stale", modes.ModesFor(state)["finance"]);
        Assert.Equal("conflicting", modes.ModesFor(state)["contracts"]);
        var deniedAudit = Assert.Single(state.Audit.Where(item =>
            item.Action == ArrivalJourneyService.SourceReadAction && item.Actor == "presenter-user"));
        using var deniedDetail = JsonDocument.Parse(deniedAudit.Detail);
        Assert.Equal("denied", deniedDetail.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("forbidden", deniedDetail.RootElement.GetProperty("errorCode").GetString());
        Assert.Empty(deniedDetail.RootElement.GetProperty("recordRefs").EnumerateArray());
        Assert.Equal(0, sources.Technical.Calls);
        Assert.Equal(1, sources.Finance.Calls);
        Assert.Equal(1, sources.Maintenance.Calls);
        Assert.Equal(1, sources.Contracts.Calls);
    }

    [Fact]
    public async Task CompletionNamesMissingSourcesAndFreezesExactBasis()
    {
        var sources = SourceSet.Create();
        using var factory = CreateFactory(sources);
        using var presenter = Session(factory, "presenter", "presenter-user");
        using var technical = Session(factory, "technical-reviewer", "technical-user");
        using var finance = Session(factory, "finance-reviewer", "finance-user");
        using var legal = Session(factory, "legal-reviewer", "legal-user");
        await Start(presenter);

        using var missing = await Complete(presenter, "finish-early", 2);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        using var missingBody = JsonDocument.Parse(await missing.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("arrival_sources_missing", missingBody.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(SourceModes.Sources,
            missingBody.RootElement.GetProperty("error").GetProperty("missingSources").EnumerateArray()
                .Select(item => item.GetString()).ToArray());

        var commands = new[]
        {
            (technical, "technical"), (technical, "maintenance"), (finance, "finance"),
            (technical, "records"), (legal, "contracts"),
        };
        var store = factory.Services.GetRequiredService<ICaseStateStore>();
        var scope = store.AuthorizeCase("case-001", "presenter");
        foreach (var (client, sourceId) in commands)
        {
            var revision = store.Read(scope).Revision;
            using var retrieved = await Retrieve(client, sourceId, "retrieve-" + sourceId, revision);
            Assert.Equal(HttpStatusCode.OK, retrieved.StatusCode);
        }

        var beforeComplete = store.Read(scope);
        var basis = beforeComplete.Basis[^1];
        var readyStatus = factory.Services.GetRequiredService<ArrivalJourneyService>().Status(beforeComplete);
        Assert.True(readyStatus.CanComplete);
        Assert.Empty(readyStatus.MissingSources);
        using var complete = await Complete(presenter, "finish-arrival", beforeComplete.Revision);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        using var receipt = JsonDocument.Parse(await complete.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("ready", receipt.RootElement.GetProperty("state").GetString());

        var completed = store.Read(scope);
        Assert.Equal(beforeComplete.Revision + 1, completed.Revision);
        Assert.Equal(basis, completed.Basis[^1]);
        var completion = Assert.Single(completed.Audit.Where(item => item.Action == ArrivalJourneyService.CompletionAction));
        using var detail = JsonDocument.Parse(completion.Detail);
        Assert.Equal(basis.Version, detail.RootElement.GetProperty("basisVersion").GetInt32());
        Assert.Equal(basis.BasisDigest, detail.RootElement.GetProperty("basisDigest").GetString());
        var status = factory.Services.GetRequiredService<ArrivalJourneyService>().Status(completed);
        Assert.Equal("ready", status.State);
        Assert.Empty(status.MissingSources);
        Assert.False(status.CanComplete);
        Assert.Equal(SourceModes.Sources.Length, sources.TotalCalls);

        using var replay = await Complete(presenter, "finish-arrival", beforeComplete.Revision);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(1, completed.Audit.Count(item => item.Action == ArrivalJourneyService.CompletionAction));
        var modes = factory.Services.GetRequiredService<SourceModeStore>();
        Assert.Equal(SourceModeOutcome.Recorded, modes.Set(scope, "presenter-user", "change-after-complete",
            completed.Revision, "finance", "stale").Outcome);
        var finishedState = store.Read(scope);
        Assert.Equal("ready", factory.Services.GetRequiredService<ArrivalJourneyService>().Status(finishedState).State);
        Assert.Empty(factory.Services.GetRequiredService<ArrivalJourneyService>().Status(finishedState).MissingSources);
        using var rejectedRetrieval = await Retrieve(technical, "technical", "after-complete", finishedState.Revision);
        Assert.Equal(HttpStatusCode.Conflict, rejectedRetrieval.StatusCode);
        using var rejectedBody = JsonDocument.Parse(await rejectedRetrieval.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("arrival_completed", rejectedBody.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ConcurrentRetrievalCannotOverwritePinsOrCompleteEarly()
    {
        var sources = SourceSet.Create();
        var racing = new RetrievalRaceStore(new CaseStateStore(CasePath), "finance");
        using var factory = CreateFactory(sources, racing);
        using var presenter = Session(factory, "presenter", "presenter-user");
        using var technical = Session(factory, "technical-reviewer", "technical-user");
        using var finance = Session(factory, "finance-reviewer", "finance-user");
        await Start(presenter);

        using var first = await Retrieve(technical, "technical", "pin-technical", 2);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var store = factory.Services.GetRequiredService<ICaseStateStore>();
        var scope = store.AuthorizeCase("case-001", "presenter");
        var priorBasis = Assert.Single(store.Read(scope).Basis);

        using var response = await Retrieve(finance, "finance", "raced-retrieval", 3);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("stale_revision", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, sources.Technical.Calls);
        Assert.Equal(1, sources.Finance.Calls);

        var state = store.Read(scope);
        Assert.Equal(priorBasis, Assert.Single(state.Basis));
        Assert.Contains(state.Audit, item => item.Action == "concurrent-update");
        var attempt = Assert.Single(state.Audit.Where(item => item.Action == ArrivalJourneyService.SourceReadAction
            && item.Detail.Contains("\"sourceId\":\"finance\"", StringComparison.Ordinal)));
        using var detail = JsonDocument.Parse(attempt.Detail);
        Assert.Equal("stale_revision", detail.RootElement.GetProperty("outcome").GetString());
        Assert.Empty(detail.RootElement.GetProperty("recordRefs").EnumerateArray());
        Assert.Equal(new[] { "maintenance", "finance", "records", "contracts" },
            factory.Services.GetRequiredService<ArrivalJourneyService>().Status(state).MissingSources);
        using var earlyCompletion = await Complete(presenter, "finish-after-race", state.Revision);
        Assert.Equal(HttpStatusCode.Conflict, earlyCompletion.StatusCode);
        using var earlyBody = JsonDocument.Parse(await earlyCompletion.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "maintenance", "finance", "records", "contracts" },
            earlyBody.RootElement.GetProperty("error").GetProperty("missingSources").EnumerateArray()
                .Select(item => item.GetString()).ToArray());
    }

    [Fact]
    public async Task ReplayDoesNotReadAgainOrAppendAnotherBasis()
    {
        var sources = SourceSet.Create();
        using var factory = CreateFactory(sources);
        using var presenter = Session(factory, "presenter", "presenter-user");
        using var technical = Session(factory, "technical-reviewer", "technical-user");
        using var finance = Session(factory, "finance-reviewer", "finance-user");
        await Start(presenter);

        using var first = await Retrieve(technical, "technical", "retrieve-once", 2);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var replay = await Retrieve(technical, "technical", "retrieve-once", 2);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, sources.Technical.Calls);

        using var changed = await Retrieve(finance, "finance", "retrieve-once", 2);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var changedBody = JsonDocument.Parse(await changed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("command_id_conflict", changedBody.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, sources.Finance.Calls);

        var state = factory.Services.GetRequiredService<ICaseStateStore>().Read(
            factory.Services.GetRequiredService<ICaseStateStore>().AuthorizeCase("case-001", "presenter"));
        Assert.Equal(3, state.Revision);
        Assert.Single(state.Basis);
        Assert.Single(state.Audit.Where(item => item.Action == ArrivalJourneyService.SourceReadAction));
    }

    private WebApplicationFactory<Program> CreateFactory(SourceSet sources, ICaseStateStore? store = null)
    {
        Directory.CreateDirectory(_directory);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Demo:ArrivalJourney"] = "true",
                    ["Persistence:CaseStateProvider"] = "File",
                    ["CaseState:Path"] = CasePath,
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider());
                if (store is not null)
                {
                    services.RemoveAll<ICaseStateStore>();
                    services.AddSingleton<ICaseStateStore>(store);
                }
                Replace(services, sources.Technical);
                Replace(services, sources.Maintenance);
                Replace(services, sources.Finance);
                Replace(services, sources.Records);
                Replace(services, sources.Contracts);
            });
        });
    }

    private static void Replace<TPayload>(IServiceCollection services, CountingSource<TPayload> source)
        where TPayload : class
    {
        services.RemoveAll<ISourceSystem<TPayload>>();
        services.AddSingleton<ISourceSystem<TPayload>>(source);
    }

    private static HttpClient Session(WebApplicationFactory<Program> factory, string role, string userId)
    {
        var session = factory.Services.GetRequiredService<DemoSessionStore>().Create(
            new DemoUser(userId, role, role, ["case-001"]));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={session.Id}");
        client.DefaultRequestHeaders.Add(DemoAuthEndpoints.CsrfHeaderName, session.CsrfToken);
        return client;
    }

    private static async Task Start(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases/case-001/arrival/start")
        {
            Content = JsonContent.Create(new { commandId = "start-arrival", expectedRevision = 1 }),
        };
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpResponseMessage> Retrieve(HttpClient client, string sourceId, string commandId, long revision) =>
        client.PostAsJsonAsync($"/api/v1/cases/case-001/arrival/sources/{sourceId}/retrieve",
            new { commandId, expectedRevision = revision }, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> Complete(HttpClient client, string commandId, long revision) =>
        client.PostAsJsonAsync("/api/v1/cases/case-001/arrival/complete",
            new { commandId, expectedRevision = revision }, TestContext.Current.CancellationToken);

    private static string ReadField(JsonElement pin, string field)
    {
        using var content = JsonDocument.Parse(pin.GetProperty("content").GetString()!);
        return content.RootElement.GetProperty("payload").GetProperty("fields").GetProperty(field).GetString()!;
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => FixedNow;
    }

    private sealed class CountingSource<TPayload> : ISourceSystem<TPayload> where TPayload : class
    {
        private readonly string _sourceId;
        private readonly TPayload _payload;
        public CountingSource(string sourceId, TPayload payload)
        {
            _sourceId = sourceId;
            _payload = payload;
            Records = [CreateRecord()];
        }

        public IReadOnlyList<SourceRecord<TPayload>> Records { get; set; }
        public int Calls { get; private set; }

        private SourceRecord<TPayload> CreateRecord() => new()
        {
            SourceId = _sourceId,
            RecordId = _sourceId + "-record-1",
            RecordVersion = 1,
            AsOf = new DateOnly(2026, 11, 1),
            CaseId = "case-001",
            Kind = _sourceId == "maintenance" ? "task_card" : _sourceId,
            Payload = _payload,
            Synthetic = true,
        };

        public Task<SourceReadResult<IReadOnlyList<SourceRecord<TPayload>>>> ListAsync(
            string caseId, CancellationToken cancellationToken = default, int? maxRecords = null)
        {
            Calls++;
            IReadOnlyList<SourceRecord<TPayload>> records = Records
                .Where(record => record.CaseId == caseId)
                .Take(maxRecords ?? int.MaxValue).ToArray();
            return Task.FromResult<SourceReadResult<IReadOnlyList<SourceRecord<TPayload>>>>(
                new SourceReadSucceeded<IReadOnlyList<SourceRecord<TPayload>>>(records));
        }

        public Task<SourceReadResult<SourceRecord<TPayload>>> GetAsync(
            string caseId, string recordId, CancellationToken cancellationToken = default)
        {
            Calls++;
            var record = Records.FirstOrDefault(item => item.CaseId == caseId && item.RecordId == recordId);
            return Task.FromResult<SourceReadResult<SourceRecord<TPayload>>>(record is null
                ? new SourceRecordNotFound<SourceRecord<TPayload>>(recordId)
                : new SourceReadSucceeded<SourceRecord<TPayload>>(record));
        }
    }

    private sealed class SourceSet(
        CountingSource<TechnicalPayload> technical,
        CountingSource<MaintenancePayload> maintenance,
        CountingSource<FinancePayload> finance,
        CountingSource<RecordsPayload> records,
        CountingSource<ContractsPayload> contracts)
    {
        public CountingSource<TechnicalPayload> Technical { get; } = technical;
        public CountingSource<MaintenancePayload> Maintenance { get; } = maintenance;
        public CountingSource<FinancePayload> Finance { get; } = finance;
        public CountingSource<RecordsPayload> Records { get; } = records;
        public CountingSource<ContractsPayload> Contracts { get; } = contracts;
        public int TotalCalls => Technical.Calls + Maintenance.Calls + Finance.Calls + Records.Calls + Contracts.Calls;

        public static SourceSet Create() => new(
            new CountingSource<TechnicalPayload>("technical", new TechnicalPayload(new Dictionary<string, string> { ["serialNumber"] = "SN-technical" })),
            new CountingSource<MaintenancePayload>("maintenance", new MaintenancePayload(new Dictionary<string, string> { ["serialNumber"] = "SN-maintenance" })),
            new CountingSource<FinancePayload>("finance", new FinancePayload(new Dictionary<string, string> { ["serialNumber"] = "SN-finance" })),
            new CountingSource<RecordsPayload>("records", new RecordsPayload(new Dictionary<string, string> { ["serialNumber"] = "SN-records" })),
            new CountingSource<ContractsPayload>("contracts", new ContractsPayload(new Dictionary<string, string> { ["serialNumber"] = "SN-contracts" })));
    }

    private sealed class RetrievalRaceStore(ICaseStateStore inner, string sourceId) : ICaseStateStore
    {
        private bool _injected;
        public CaseScope AuthorizeCase(string caseId, string owner) => inner.AuthorizeCase(caseId, owner);
        public string? OwnerOf(string caseId) => inner.OwnerOf(caseId);
        public CaseScope CreateCase(string caseId, string owner) => inner.CreateCase(caseId, owner);
        public CaseState Read(CaseScope scope) => inner.Read(scope);
        public BasisVersion GetBasisVersion(CaseScope scope, int version) => inner.GetBasisVersion(scope, version);
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => inner.ReadAudit(scope);
        public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit)
        {
            if (!_injected && commit.NewBasis is not null && commit.Action == ArrivalJourneyService.SourceReadAction
                && commit.Detail.Contains($"\"sourceId\":\"{sourceId}\"", StringComparison.Ordinal))
            {
                _injected = true;
                inner.Commit(scope, expectedRevision,
                    new CaseCommit(null, null, "concurrent-writer", "concurrent-update", "Intervening case change."));
            }
            return inner.Commit(scope, expectedRevision, commit);
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
