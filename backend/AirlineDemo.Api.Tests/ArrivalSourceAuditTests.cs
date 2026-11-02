using System.Net;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.SourceSystems;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirlineDemo.Api.Tests;

public sealed class ArrivalSourceAuditTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arrival-source-audit-" + Guid.NewGuid().ToString("N"));
    private string CasePath => Path.Combine(_directory, "cases.json");
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task ReadsRecordServerIdentityAndExactOutcome()
    {
        using var factory = CreateFactory();
        using var client = Session(factory, "technical-reviewer", "technical-user");
        using var response = await client.GetAsync(SourceRoute, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var store = factory.Services.GetRequiredService<ICaseStateStore>();
        var state = store.Read(store.AuthorizeCase("case-001", "presenter"));
        var audit = Assert.Single(state.Audit.Where(item => item.Action == ArrivalJourneyService.SourceReadAction));
        Assert.Equal("technical-user", audit.Actor);
        Assert.Equal(FixedNow, audit.At);
        using var detail = JsonDocument.Parse(audit.Detail);
        Assert.Equal("technical", detail.RootElement.GetProperty("sourceId").GetString());
        Assert.Equal("technical-reviewer", detail.RootElement.GetProperty("role").GetString());
        Assert.Equal("success", detail.RootElement.GetProperty("outcome").GetString());
        var refs = detail.RootElement.GetProperty("recordRefs").EnumerateArray().ToArray();
        Assert.NotEmpty(refs);
        Assert.All(refs, item =>
        {
            Assert.Equal("technical", item.GetProperty("sourceId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("recordId").GetString()));
            Assert.True(item.GetProperty("recordVersion").GetInt32() > 0);
        });
        Assert.DoesNotContain("payload", audit.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllRolesReceiveOnlyPermittedAuditReferences()
    {
        using var factory = CreateFactory();
        using var technical = Session(factory, "technical-reviewer", "technical-user");
        using var technicalRead = await technical.GetAsync(SourceRoute, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, technicalRead.StatusCode);

        using var legal = Session(factory, "legal-reviewer", "legal-user");
        using var deniedRead = await legal.GetAsync(SourceRoute, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, deniedRead.StatusCode);

        using var finance = Session(factory, "finance-reviewer", "finance-user");
        using var financeResponse = await finance.GetAsync("/api/cases/case-001", TestContext.Current.CancellationToken);
        using var financeBody = JsonDocument.Parse(await financeResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var financeEvents = SourceAudits(financeBody.RootElement);
        Assert.NotEmpty(financeEvents);
        Assert.All(financeEvents.Where(item => item.GetProperty("detail").GetProperty("outcome").GetString() == "success"),
            item => Assert.NotEmpty(item.GetProperty("detail").GetProperty("recordRefs").EnumerateArray()));

        using var legalResponse = await legal.GetAsync("/api/cases/case-001", TestContext.Current.CancellationToken);
        using var legalBody = JsonDocument.Parse(await legalResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var legalEvents = SourceAudits(legalBody.RootElement);
        Assert.NotEmpty(legalEvents);
        Assert.All(legalEvents, item => Assert.False(item.GetProperty("detail").TryGetProperty("recordRefs", out _)));

        using var presenter = Session(factory, "presenter", "presenter-user");
        using var presenterResponse = await presenter.GetAsync("/api/cases/case-001", TestContext.Current.CancellationToken);
        using var presenterBody = JsonDocument.Parse(await presenterResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.All(SourceAudits(presenterBody.RootElement),
            item => Assert.False(item.GetProperty("detail").TryGetProperty("recordRefs", out _)));
    }

    [Fact]
    public async Task CaseProjectionDoesNotLeakPinsThroughBasisContent()
    {
        using var factory = CreateFactory();
        var store = factory.Services.GetRequiredService<ICaseStateStore>();
        var scope = store.AuthorizeCase("case-001", "presenter");
        var state = store.Read(scope);
        const string sentinel = "PRIVATE-RAW-SOURCE-BASIS-CONTENT";
        store.Commit(scope, state.Revision,
            new CaseCommit(CaseStateStore.CreateBasisVersion(state.Basis.Count + 1, sentinel,
                state.Basis.Count == 0 ? "" : state.Basis[^1].BasisDigest),
                null, "technical-user", "test-basis", "Add basis for projection test."));

        using var client = Session(factory, "presenter", "presenter-user");
        using var response = await client.GetAsync("/api/cases/case-001", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(sentinel, body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body);
        Assert.False(Assert.Single(json.RootElement.GetProperty("basis").EnumerateArray())
            .TryGetProperty("content", out _));
    }

    [Fact]
    public async Task AuditFailureDoesNotReturnSuccessfulRecords()
    {
        var source = new CountingTechnicalSource();
        using var factory = CreateFactory(source, failAudit: true);
        using var client = Session(factory, "technical-reviewer", "technical-user");
        using var response = await client.GetAsync(SourceRoute, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(source.Record.RecordId, body, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"audit_write_failed\"", body, StringComparison.Ordinal);
        Assert.Equal(1, source.Calls);

        var store = factory.Services.GetRequiredService<ICaseStateStore>();
        var state = store.Read(store.AuthorizeCase("case-001", "presenter"));
        Assert.DoesNotContain(state.Audit, item => item.Action == ArrivalJourneyService.SourceReadAction);
    }

    private const string SourceRoute = "/api/v1/cases/case-001/sources/technical/records";

    private WebApplicationFactory<Program> CreateFactory(CountingTechnicalSource? source = null, bool failAudit = false)
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
                if (source is not null)
                {
                    services.RemoveAll<ISourceSystem<TechnicalPayload>>();
                    services.AddSingleton<ISourceSystem<TechnicalPayload>>(source);
                }
                if (failAudit)
                {
                    services.RemoveAll<ICaseStateStore>();
                    services.AddSingleton<ICaseStateStore>(_ =>
                        new FailingAuditStore(new CaseStateStore(CasePath)));
                }
            });
        });
    }

    private static HttpClient Session(WebApplicationFactory<Program> factory, string role, string userId)
    {
        var session = factory.Services.GetRequiredService<DemoSessionStore>().Create(
            new DemoUser(userId, role, role, ["case-001"]));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={session.Id}");
        return client;
    }

    private static JsonElement[] SourceAudits(JsonElement body) => body.GetProperty("audit").EnumerateArray()
        .Where(item => item.GetProperty("action").GetString() == ArrivalJourneyService.SourceReadAction)
        .ToArray();

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => FixedNow;
    }

    private sealed class CountingTechnicalSource : ISourceSystem<TechnicalPayload>
    {
        public SourceRecord<TechnicalPayload> Record { get; } = new()
        {
            SourceId = "technical",
            RecordId = "technical-record-1",
            RecordVersion = 2,
            AsOf = new DateOnly(2026, 11, 1),
            CaseId = "case-001",
            Kind = "technical",
            Payload = new TechnicalPayload(new Dictionary<string, string> { ["serialNumber"] = "TEST-SERIAL" }),
            Synthetic = true,
        };
        public int Calls { get; private set; }

        public Task<SourceReadResult<IReadOnlyList<SourceRecord<TechnicalPayload>>>> ListAsync(
            string caseId, CancellationToken cancellationToken = default, int? maxRecords = null)
        {
            Calls++;
            IReadOnlyList<SourceRecord<TechnicalPayload>> records = Record.CaseId == caseId ? [Record] : [];
            return Task.FromResult<SourceReadResult<IReadOnlyList<SourceRecord<TechnicalPayload>>>>(
                new SourceReadSucceeded<IReadOnlyList<SourceRecord<TechnicalPayload>>>(records));
        }

        public Task<SourceReadResult<SourceRecord<TechnicalPayload>>> GetAsync(
            string caseId, string recordId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<SourceReadResult<SourceRecord<TechnicalPayload>>>(Record.RecordId == recordId && Record.CaseId == caseId
                ? new SourceReadSucceeded<SourceRecord<TechnicalPayload>>(Record)
                : new SourceRecordNotFound<SourceRecord<TechnicalPayload>>(recordId));
        }
    }

    private sealed class FailingAuditStore(ICaseStateStore inner) : ICaseStateStore
    {
        public CaseScope AuthorizeCase(string caseId, string owner) => inner.AuthorizeCase(caseId, owner);
        public string? OwnerOf(string caseId) => inner.OwnerOf(caseId);
        public CaseScope CreateCase(string caseId, string owner) => inner.CreateCase(caseId, owner);
        public CaseState Read(CaseScope scope) => inner.Read(scope);
        public BasisVersion GetBasisVersion(CaseScope scope, int version) => inner.GetBasisVersion(scope, version);
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => inner.ReadAudit(scope);
        public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit) =>
            commit.Action == ArrivalJourneyService.SourceReadAction
                ? throw new IOException("Injected source audit persistence failure.")
                : inner.Commit(scope, expectedRevision, commit);
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
