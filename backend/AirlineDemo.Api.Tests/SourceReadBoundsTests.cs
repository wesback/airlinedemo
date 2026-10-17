using System.Net;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.SourceSystems;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirlineDemo.Api.Tests;
public sealed class SourceReadBoundsTests
{
    private const string Case = "source-case";
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class FakeSource<T>(IReadOnlyList<SourceRecord<T>> records, Task? wait = null) : ISourceSystem<T> where T : class
    {
        public IReadOnlyList<SourceRecord<T>> Records { get; set; } = records;
        public int Calls;
        public async Task<SourceReadResult<IReadOnlyList<SourceRecord<T>>>> ListAsync(string caseId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (wait is not null) await wait.WaitAsync(cancellationToken);
            return new SourceReadSucceeded<IReadOnlyList<SourceRecord<T>>>(Records);
        }
        public async Task<SourceReadResult<SourceRecord<T>>> GetAsync(string caseId, string recordId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (wait is not null) await wait.WaitAsync(cancellationToken);
            var found = Records.FirstOrDefault(record => record.RecordId == recordId);
            return found is null ? new SourceRecordNotFound<SourceRecord<T>>(recordId) : new SourceReadSucceeded<SourceRecord<T>>(found);
        }
    }
    private static SourceRecord<T> Record<T>(int id, string caseId = Case) where T : class => new()
    {
        SourceId = "technical", RecordId = $"record-{id}", RecordVersion = 1, AsOf = new DateOnly(2026, 10, 1),
        CaseId = caseId, Kind = "test", Payload = (T)(object)new TechnicalPayload(new Dictionary<string, string> { ["value"] = id.ToString() }), Synthetic = true,
    };
    private static WebApplicationFactory<Program> Factory(string path, FakeSource<TechnicalPayload> technical) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["CaseState:Path"] = path, ["Demo:Passcode"] = "test-passcode" }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTime());
                services.RemoveAll<ISourceSystem<TechnicalPayload>>();
                services.AddSingleton<ISourceSystem<TechnicalPayload>>(technical);
            });
        });
    private static FakeSource<TechnicalPayload> Source(int count, Task? wait = null) =>
        new(Enumerable.Range(1, count).Select(id => Record<TechnicalPayload>(id)).ToArray(), wait);

    private static DemoSession Session(WebApplicationFactory<Program> factory, string role = "technical-reviewer", string[]? scopes = null) =>
        factory.Services.GetRequiredService<DemoSessionStore>().Create(new DemoUser("user-" + Guid.NewGuid(), "Test", role, scopes ?? [Case]));
    private static Task<HttpResponseMessage> Get(HttpClient client, DemoSession? session, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (session is not null) request.Headers.Add("Cookie", $"__Host-demo-session={session.Id}");
        return client.SendAsync(request);
    }

    private static void EnsureCase(WebApplicationFactory<Program> factory) { var cases = factory.Services.GetRequiredService<ICaseStateStore>(); if (cases.OwnerOf(Case) is null) cases.CreateCase(Case, "test-owner"); }
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"source-read-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task ListEnforcesRecordCapAndReturnsNoContinuationFields()
    {
        var source = Source(100);
        await using var factory = Factory(TempPath(), source);
        EnsureCase(factory);
        using var client = factory.CreateClient();
        var session = Session(factory);
        using var ok = await Get(client, session, $"/api/v1/cases/{Case}/sources/technical/records");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var body = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "contractVersion", "caseId", "sourceId", "records" }, body.RootElement.EnumerateObject().Select(x => x.Name));
        Assert.Equal(100, body.RootElement.GetProperty("records").GetArrayLength());
        using var single = await Get(client, session, $"/api/v1/cases/{Case}/sources/technical/records/record-1");
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        using var singleBody = JsonDocument.Parse(await single.Content.ReadAsStringAsync());
        Assert.True(singleBody.RootElement.TryGetProperty("record", out _));

        source.Records = [Record<TechnicalPayload>(1, "different-case")];
        using var mismatched = await Get(client, session, $"/api/v1/cases/{Case}/sources/technical/records/record-1");
        Assert.Equal(HttpStatusCode.BadGateway, mismatched.StatusCode);
        Assert.Equal("{\"contractVersion\":1,\"error\":{\"code\":\"source_case_mismatch\"}}", await mismatched.Content.ReadAsStringAsync());
        source.Records = Enumerable.Range(1, 101).Select(id => Record<TechnicalPayload>(id)).ToArray();
        using var tooMany = await Get(client, session, $"/api/v1/cases/{Case}/sources/technical/records");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.StatusCode);
        Assert.Equal("{\"contractVersion\":1,\"error\":{\"code\":\"source_record_limit\"}}", await tooMany.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task AuthenticationRoleScopeAndRequestBudgetAreEnforcedBeforeSourceCalls()
    {
        var source = Source(1);
        await using var factory = Factory(TempPath(), source);
        EnsureCase(factory);
        using var client = factory.CreateClient();
        using var anonymous = await Get(client, null, $"/api/v1/cases/{Case}/sources/technical/records");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var roleDenied = await Get(client, Session(factory, "legal-reviewer"), $"/api/v1/cases/{Case}/sources/technical/records");
        Assert.Equal(HttpStatusCode.Forbidden, roleDenied.StatusCode);
        using var scopeDenied = await Get(client, Session(factory, scopes: ["other-case"]), $"/api/v1/cases/{Case}/sources/technical/records");
        Assert.Equal(HttpStatusCode.NotFound, scopeDenied.StatusCode);
        using var paged = await Get(client, Session(factory), $"/api/v1/cases/{Case}/sources/technical/records?offset=0");
        Assert.Equal(HttpStatusCode.BadRequest, paged.StatusCode);
        Assert.Equal(0, source.Calls);
        var session = Session(factory);
        for (var i = 0; i < SourceReadBudget.MaxRequests; i++)
        {
            using var response = await Get(client, session, $"/api/v1/cases/{Case}/sources/technical/records");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var rejected = await Get(client, session, $"/api/v1/cases/{Case}/sources/technical/records");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("{\"contractVersion\":1,\"error\":{\"code\":\"source_read_limit\"}}", await rejected.Content.ReadAsStringAsync());
        Assert.Equal(SourceReadBudget.MaxRequests, source.Calls);
    }
    [Fact]
    public async Task DeadlineReturnsTimeoutWithoutRecords()
    {
        var source = Source(1, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        await using var factory = Factory(TempPath(), source);
        EnsureCase(factory);
        using var client = factory.CreateClient();
        using var response = await Get(client, Session(factory), $"/api/v1/cases/{Case}/sources/technical/records");
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("{\"contractVersion\":1,\"error\":{\"code\":\"source_read_timeout\"}}", await response.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task SharedAdmissionStoreAppliesRollingBudgetsAndMatchesLeaseReleases()
    {
        var store = new InMemorySourceReadAdmissionStore();
        var first = new SourceReadBudget(store);
        var second = new SourceReadBudget(store);
        for (var i = 0; i < SourceReadBudget.MaxRequests; i++)
        {
            var lease = await (i % 2 == 0 ? first : second).TryAcquireAsync("session", Case, Now);
            Assert.NotNull(lease);
            Assert.True(await first.ReleaseAsync(lease.Value, Now));
        }
        Assert.Null(await second.TryAcquireAsync("session", Case, Now));
        Assert.NotNull(await second.TryAcquireAsync("other-session", Case, Now));
        Assert.NotNull(await second.TryAcquireAsync("session", "other-case", Now));
        Assert.NotNull(await second.TryAcquireAsync("session", Case, Now.AddSeconds(60)));

        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => first.TryAcquireAsync("race-session", "race-case", Now)));
        var admitted = attempts.Where(id => id is not null).Select(id => id!.Value).ToArray();
        Assert.Equal(SourceReadBudget.MaxInFlightPerCase, admitted.Length);
        Assert.Equal(SourceReadBudget.MaxInFlightPerCase, store.ActiveCount("race-case", Now));
        Assert.True(await first.ReleaseAsync(admitted[0], Now));
        Assert.False(await first.ReleaseAsync(admitted[0], Now));
        Assert.Equal(1, store.ActiveCount("race-case", Now));
        Assert.Equal(0, store.ActiveCount("race-case", Now.AddSeconds(1)));
    }
}
