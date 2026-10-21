using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirlineDemo.Api.Tests;

public sealed class CaseHistoryEndpointTests : IDisposable
{
    private const string Passcode = "history-test-passcode";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"case-history-api-{Guid.NewGuid():N}");

    private sealed record Session(string Cookie, string Csrf);

    private static async Task<Session> SignInAsync(HttpClient client, string userId, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId, passcode = Passcode }, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return new Session(cookie, body.RootElement.GetProperty("csrfToken").GetString()!);
    }

    private WebApplicationFactory<Program> CreateFactory(ICaseStateStore store) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Demo:Passcode"] = Passcode }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICaseStateStore>();
                services.AddSingleton(store);
                services.AddSingleton<ICaseStateStore>(store);
            });
        });

    private CaseStateStore CreateStore()
    {
        Directory.CreateDirectory(_directory);
        var store = new CaseStateStore(Path.Combine(_directory, "case-state.json"));
        var scope = store.CreateCase("case-001", "presenter");
        var basis1 = CaseStateStore.CreateBasisVersion(1, "first basis", string.Empty);
        store.Commit(scope, 0, new CaseCommit(
            basis1, null, "reviewer", "history-test", "prior run",
            JourneyEvent: Event("case-001", "run-older", "event-older-1", "basis-older", 1,
                [new CaseJourneyWitness("witness-older-1", "document-001", 1, 1, "basis-older", 1)])));
        store.Commit(scope, 1, new CaseCommit(
            null, null, "reviewer", "history-test", "prior run",
            JourneyEvent: Event("case-001", "run-older", "event-older-2", "basis-older", 1,
                [new CaseJourneyWitness("witness-older-2", "document-001", 1, 2, "basis-older", 1)])));
        var basis2 = CaseStateStore.CreateBasisVersion(2, "second basis", basis1.BasisDigest);
        store.Commit(scope, 2, new CaseCommit(
            basis2, null, "reviewer", "history-test", "current run",
            JourneyEvent: Event("case-001", "run-current", "event-current-1", "basis-current", 2,
                [new CaseJourneyWitness("witness-current-1", "document-001", 2, 1, "basis-current", 2)])));
        store.Commit(scope, 3, new CaseCommit(
            null, null, "reviewer", "history-test", "current run",
            JourneyEvent: Event("case-001", "run-current", "event-current-2", "basis-current", 2,
                [new CaseJourneyWitness("witness-current-2", "document-001", 2, 2, "basis-current", 2)])));
        store.CreateCase("case-002", "presenter");
        return store;
    }

    private static CaseJourneyEventDraft Event(
        string caseId,
        string runId,
        string eventId,
        string basisId,
        int basisVersion,
        IReadOnlyList<CaseJourneyWitness> witnesses) =>
        new(eventId, runId, caseId, "evidence-reviewed", basisId, basisVersion, "technical-reviewer",
            "Evidence reviewed", "Rules-based", witnesses);

    private static Task<HttpResponseMessage> GetHistoryAsync(
        HttpClient client, string path, Session? session = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (session is not null) request.Headers.Add("Cookie", session.Cookie);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task HistorySelectsExactAuthorizedCaseRunAndBasis()
    {
        using var factory = CreateFactory(CreateStore());
        using var client = factory.CreateClient();
        var reviewer = await SignInAsync(client, "reviewer", TestContext.Current.CancellationToken);

        using var selected = await GetHistoryAsync(client,
            "/api/cases/case-001/history?runId=run-older&basisId=basis-older", reviewer);
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        using var selectedJson = JsonDocument.Parse(await selected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = selectedJson.RootElement;
        Assert.Equal("case-001", root.GetProperty("caseId").GetString());
        Assert.Equal("run-older", root.GetProperty("runId").GetString());
        Assert.Equal("basis-older", root.GetProperty("basisId").GetString());
        var olderEvents = root.GetProperty("events");
        Assert.Equal(["event-older-1", "event-older-2"],
            olderEvents.EnumerateArray().Select(item => item.GetProperty("eventId").GetString()).ToArray());
        var olderWitness = olderEvents[0].GetProperty("witnesses")[0];
        Assert.Equal("witness-older-1", olderWitness.GetProperty("witnessId").GetString());
        Assert.Equal("document-001", olderWitness.GetProperty("documentId").GetString());
        Assert.Equal(1, olderWitness.GetProperty("documentVersion").GetInt32());
        Assert.Equal(1, olderWitness.GetProperty("page").GetInt32());
        Assert.Equal("basis-older", olderWitness.GetProperty("basisId").GetString());
        Assert.Equal(1, olderWitness.GetProperty("basisVersion").GetInt32());
        var secondOlderWitness = olderEvents[1].GetProperty("witnesses")[0];
        Assert.Equal("witness-older-2", secondOlderWitness.GetProperty("witnessId").GetString());
        Assert.Equal("document-001", secondOlderWitness.GetProperty("documentId").GetString());
        Assert.Equal(1, secondOlderWitness.GetProperty("documentVersion").GetInt32());
        Assert.Equal(2, secondOlderWitness.GetProperty("page").GetInt32());
        Assert.Equal("basis-older", secondOlderWitness.GetProperty("basisId").GetString());
        Assert.Equal(1, secondOlderWitness.GetProperty("basisVersion").GetInt32());

        using var current = await GetHistoryAsync(client, "/api/cases/case-001/history", reviewer);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        using var currentJson = JsonDocument.Parse(await current.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("run-current", currentJson.RootElement.GetProperty("runId").GetString());
        Assert.Equal("basis-current", currentJson.RootElement.GetProperty("basisId").GetString());
        Assert.Equal(["event-current-1", "event-current-2"],
            currentJson.RootElement.GetProperty("events").EnumerateArray()
                .Select(item => item.GetProperty("eventId").GetString()).ToArray());
    }

    [Fact]
    public async Task HistoryHidesUnknownAndOutOfScopeTuples()
    {
        using var factory = CreateFactory(CreateStore());
        using var client = factory.CreateClient();
        var reviewer = await SignInAsync(client, "reviewer", TestContext.Current.CancellationToken);

        using var unknown = await GetHistoryAsync(client, "/api/cases/unknown/history", reviewer);
        var unknownBody = await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Empty(unknownBody);

        foreach (var path in new[]
        {
            "/api/cases/case-002/history",
            "/api/cases/case-001/history?runId=missing-run",
            "/api/cases/case-001/history?runId=run-older&basisId=missing-basis",
            "/api/cases/case-001/history?runId=run-current&basisId=basis-older",
        })
        {
            using var response = await GetHistoryAsync(client, path, reviewer);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(unknownBody, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task HistoryFailsClosedForMissingSessionAndStoreFailure()
    {
        var store = new FailingReadCaseStateStore();
        using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        using var unauthenticated = await GetHistoryAsync(client, "/api/cases/case-001/history");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(0, store.ReadCalls);

        var reviewer = await SignInAsync(client, "reviewer", TestContext.Current.CancellationToken);
        using var failed = await GetHistoryAsync(client, "/api/cases/case-001/history", reviewer);
        var failureBody = await failed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal("{\"error\":\"Case history could not be read.\"}", failureBody);
        Assert.DoesNotContain("stack", failureBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("event-older", failureBody, StringComparison.Ordinal);
        Assert.Equal(1, store.ReadCalls);
        Assert.Equal(0, store.MutationCalls);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FailingReadCaseStateStore : ICaseStateStore
    {
        public int ReadCalls { get; private set; }
        public int MutationCalls { get; private set; }

        public CaseScope AuthorizeCase(string caseId, string owner) => new(caseId);
        public string? OwnerOf(string caseId) => "reviewer";
        public CaseScope CreateCase(string caseId, string owner) => throw new NotSupportedException();
        public CaseState Read(CaseScope scope)
        {
            ReadCalls++;
            throw new InvalidDataException("private store details");
        }
        public BasisVersion GetBasisVersion(CaseScope scope, int version) => throw new NotSupportedException();
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => throw new NotSupportedException();
        public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit)
        {
            MutationCalls++;
            throw new NotSupportedException();
        }
        public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope) => throw new NotSupportedException();
        public CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor) =>
            throw new NotSupportedException();
        public CommitResult ChangeWorkItemState(CaseScope scope, long expectedRevision, string itemId, string state, string actor) =>
            throw new NotSupportedException();
        public void AppendTrace(CaseScope scope, InvestigationTrace trace) => throw new NotSupportedException();
        public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId) =>
            throw new NotSupportedException();
    }
}
