using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.SourceSystems;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirlineDemo.Api.Tests;

public sealed class SourceReadEndpointTests
{
    private const string Case = "source-case";
    private const string OtherCase = "other-source-case";

    private sealed class FakeSource<T>(IReadOnlyList<SourceRecord<T>> records) : ISourceSystem<T> where T : class
    {
        public int Calls;
        public SourceReadResult<IReadOnlyList<SourceRecord<T>>>? ListResult { get; set; }
        public SourceReadResult<SourceRecord<T>>? GetResult { get; set; }

        public Task<SourceReadResult<IReadOnlyList<SourceRecord<T>>>> ListAsync(
            string caseId, CancellationToken cancellationToken = default, int? maxRecords = null)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(ListResult ?? new SourceReadSucceeded<IReadOnlyList<SourceRecord<T>>>(
                records.Where(record => record.CaseId == caseId).Take(maxRecords ?? int.MaxValue).ToArray()));
        }

        public Task<SourceReadResult<SourceRecord<T>>> GetAsync(
            string caseId, string recordId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (GetResult is not null) return Task.FromResult(GetResult);
            var record = records.FirstOrDefault(item => item.CaseId == caseId && item.RecordId == recordId);
            return Task.FromResult<SourceReadResult<SourceRecord<T>>>(record is null
                ? new SourceRecordNotFound<SourceRecord<T>>(recordId)
                : new SourceReadSucceeded<SourceRecord<T>>(record));
        }
    }

    private sealed record Sources(
        FakeSource<TechnicalPayload> Technical,
        FakeSource<MaintenancePayload> Maintenance,
        FakeSource<FinancePayload> Finance,
        FakeSource<RecordsPayload> Records,
        FakeSource<ContractsPayload> Contracts)
    {
        public int Calls => Technical.Calls + Maintenance.Calls + Finance.Calls + Records.Calls + Contracts.Calls;
    }

    private static SourceRecord<T> Record<T>(int id, string caseId = Case) where T : class
    {
        var fields = new Dictionary<string, string> { ["value"] = id.ToString() };
        object payload = typeof(T) == typeof(TechnicalPayload) ? new TechnicalPayload(fields)
            : typeof(T) == typeof(MaintenancePayload) ? new MaintenancePayload(fields)
            : typeof(T) == typeof(FinancePayload) ? new FinancePayload(fields)
            : typeof(T) == typeof(RecordsPayload) ? new RecordsPayload(fields)
            : typeof(T) == typeof(ContractsPayload) ? new ContractsPayload(fields)
            : throw new InvalidOperationException($"Unsupported source payload: {typeof(T).Name}");
        var sourceId = typeof(T) == typeof(TechnicalPayload) ? "technical"
            : typeof(T) == typeof(MaintenancePayload) ? "maintenance"
            : typeof(T) == typeof(FinancePayload) ? "finance"
            : typeof(T) == typeof(RecordsPayload) ? "records" : "contracts";
        return new SourceRecord<T>
        {
            SourceId = sourceId, RecordId = $"record-{id}", RecordVersion = 1, AsOf = new DateOnly(2026, 10, 1),
            CaseId = caseId, Kind = "test", Payload = (T)payload, Synthetic = true,
        };
    }

    private static FakeSource<T> Source<T>(string caseId = Case) where T : class => new([Record<T>(1, caseId)]);

    private static Sources AllSources() => new(
        Source<TechnicalPayload>(), Source<MaintenancePayload>(), Source<FinancePayload>(),
        Source<RecordsPayload>(), Source<ContractsPayload>());

    private static WebApplicationFactory<Program> Factory(string path, Sources sources) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CaseState:Path"] = path,
                    ["Demo:Passcode"] = "test-passcode",
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISourceSystem<TechnicalPayload>>();
                services.AddSingleton<ISourceSystem<TechnicalPayload>>(sources.Technical);
                services.RemoveAll<ISourceSystem<MaintenancePayload>>();
                services.AddSingleton<ISourceSystem<MaintenancePayload>>(sources.Maintenance);
                services.RemoveAll<ISourceSystem<FinancePayload>>();
                services.AddSingleton<ISourceSystem<FinancePayload>>(sources.Finance);
                services.RemoveAll<ISourceSystem<RecordsPayload>>();
                services.AddSingleton<ISourceSystem<RecordsPayload>>(sources.Records);
                services.RemoveAll<ISourceSystem<ContractsPayload>>();
                services.AddSingleton<ISourceSystem<ContractsPayload>>(sources.Contracts);
            });
        });

    private static DemoSession Session(WebApplicationFactory<Program> factory, string role = "technical-reviewer",
        string[]? scopes = null) =>
        factory.Services.GetRequiredService<DemoSessionStore>()
            .Create(new DemoUser("source-reader", "Test", role, scopes ?? [Case]));

    private static void EnsureCases(WebApplicationFactory<Program> factory, params string[] caseIds)
    {
        var cases = factory.Services.GetRequiredService<ICaseStateStore>();
        foreach (var caseId in caseIds)
            if (cases.OwnerOf(caseId) is null) cases.CreateCase(caseId, "test-owner");
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"source-read-endpoint-{Guid.NewGuid():N}.json");

    private static HttpRequestMessage GetRequest(string path, string? sessionId, bool sentinels = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        var cookies = new List<string>();
        if (sessionId is not null) cookies.Add($"__Host-demo-session={sessionId}");
        if (sentinels) cookies.Add("probe=SYN-COOKIE-01");
        if (cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", cookies));
        if (sentinels) request.Headers.Authorization = new("Bearer", "SYN-AUTH-TOKEN-01");
        return request;
    }

    private static string Route(string source, bool get = false, string caseId = Case) =>
        $"/api/v1/cases/{caseId}/sources/{source}/records" + (get ? "/record-1" : "");

    private static void AssertJsonEqual(string expected, string actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
            $"Expected JSON: {expected}\nActual JSON: {actual}");

    [Fact]
    public async Task ListAndGetReturnVersionedRecordEnvelopes()
    {
        var sources = AllSources();
        await using var factory = Factory(TempPath(), sources);
        EnsureCases(factory, Case);
        using var client = factory.CreateClient();
        var session = Session(factory);

        const string expectedList = """{"contractVersion":1,"caseId":"source-case","sourceId":"technical","records":[{"sourceId":"technical","recordId":"record-1","recordVersion":1,"asOf":"2026-10-01","caseId":"source-case","kind":"test","stale":false,"payload":{"fields":{"value":"1"}},"synthetic":true}]}""";
        const string expectedGet = """{"contractVersion":1,"caseId":"source-case","sourceId":"technical","record":{"sourceId":"technical","recordId":"record-1","recordVersion":1,"asOf":"2026-10-01","caseId":"source-case","kind":"test","stale":false,"payload":{"fields":{"value":"1"}},"synthetic":true}}""";
        using (var listRequest = GetRequest(Route("technical"), session.Id))
        using (var list = await client.SendAsync(listRequest))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            AssertJsonEqual(expectedList, await list.Content.ReadAsStringAsync());
        }
        using (var getRequest = GetRequest(Route("technical", get: true), session.Id))
        using (var get = await client.SendAsync(getRequest))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            AssertJsonEqual(expectedGet, await get.Content.ReadAsStringAsync());
        }

        sources.Technical.ListResult = new SourceUnavailable<IReadOnlyList<SourceRecord<TechnicalPayload>>>("SYN-SOURCE-PAYLOAD-01");
        using (var unavailableRequest = GetRequest(Route("technical"), session.Id))
        using (var unavailable = await client.SendAsync(unavailableRequest))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            var body = await unavailable.Content.ReadAsStringAsync();
            AssertJsonEqual("""{"contractVersion":1,"error":{"code":"source_unavailable"}}""", body);
            Assert.DoesNotContain("records", body, StringComparison.Ordinal);
            Assert.DoesNotContain("SYN-SOURCE-PAYLOAD-01", body, StringComparison.Ordinal);
        }
        sources.Technical.GetResult = new SourceUnavailable<SourceRecord<TechnicalPayload>>("SYN-SOURCE-PAYLOAD-01");
        using (var unavailableRequest = GetRequest(Route("technical", get: true), session.Id))
        using (var unavailable = await client.SendAsync(unavailableRequest))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            var body = await unavailable.Content.ReadAsStringAsync();
            AssertJsonEqual("""{"contractVersion":1,"error":{"code":"source_unavailable"}}""", body);
            Assert.DoesNotContain("record", body, StringComparison.Ordinal);
            Assert.DoesNotContain("SYN-SOURCE-PAYLOAD-01", body, StringComparison.Ordinal);
        }

        using (var unsupportedListRequest = GetRequest("/api/v2/cases/source-case/sources/technical/records", session.Id))
        using (var unsupportedList = await client.SendAsync(unsupportedListRequest))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unsupportedList.StatusCode);
            AssertJsonEqual("""{"contractVersion":1,"error":{"code":"unsupported_contract_version"}}""",
                await unsupportedList.Content.ReadAsStringAsync());
        }
        using (var unsupportedGetRequest = GetRequest("/api/v2/cases/source-case/sources/technical/records/record-1", session.Id))
        using (var unsupportedGet = await client.SendAsync(unsupportedGetRequest))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unsupportedGet.StatusCode);
            AssertJsonEqual("""{"contractVersion":1,"error":{"code":"unsupported_contract_version"}}""",
                await unsupportedGet.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task CaseAndRoleMatrixEveryReadSurface()
    {
        var sources = AllSources();
        await using var factory = Factory(TempPath(), sources);
        EnsureCases(factory, Case, OtherCase);
        using var client = factory.CreateClient();

        foreach (var route in new[] { Route("technical"), Route("technical", get: true) })
        {
            using var request = GetRequest(route, null, sentinels: true);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            AssertJsonEqual("""{"contractVersion":1,"error":{"code":"unauthenticated"}}""", body);
            Assert.DoesNotContain("SYN-COOKIE-01", body, StringComparison.Ordinal);
            Assert.DoesNotContain("SYN-AUTH-TOKEN-01", body, StringComparison.Ordinal);
        }
        Assert.Equal(0, sources.Calls);

        var matrix = new (string Role, string[] Allowed)[]
        {
            ("technical-reviewer", ["technical", "maintenance", "records"]),
            ("finance-reviewer", ["technical", "finance"]),
            ("legal-reviewer", ["contracts", "records"]),
            ("presenter", []),
            ("executive", []),
            ("unknown-role", []),
        };
        foreach (var (role, allowedSources) in matrix)
        {
            var session = Session(factory, role);
            foreach (var source in new[] { "technical", "maintenance", "finance", "records", "contracts" })
            foreach (var route in new[] { Route(source), Route(source, get: true) })
            {
                var before = sources.Calls;
                using var request = GetRequest(route, session.Id);
                using var response = await client.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                if (!allowedSources.Contains(source, StringComparer.Ordinal))
                {
                    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                    AssertJsonEqual("""{"contractVersion":1,"error":{"code":"forbidden"}}""", body);
                    Assert.Equal(before, sources.Calls);
                }
                else
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal(before + 1, sources.Calls);
                    var json = JsonNode.Parse(body)!;
                    Assert.Equal(1, (int)json["contractVersion"]!);
                    Assert.Equal(Case, (string?)json["caseId"]);
                    Assert.Equal(source, (string?)json["sourceId"]);
                }
            }
        }

        var beforeScopeChecks = sources.Calls;
        var missing = Session(factory, scopes: ["missing-case"]);
        using var missingRequest = GetRequest(Route("technical", caseId: "missing-case"), missing.Id);
        using var missingResponse = await client.SendAsync(missingRequest);
        var missingBody = await missingResponse.Content.ReadAsStringAsync();

        var outOfScope = Session(factory, scopes: [Case]);
        using var outOfScopeRequest = GetRequest(Route("technical", caseId: OtherCase), outOfScope.Id, sentinels: true);
        using var outOfScopeResponse = await client.SendAsync(outOfScopeRequest);
        var outOfScopeBody = await outOfScopeResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outOfScopeResponse.StatusCode);
        Assert.Equal(missingBody, outOfScopeBody);
        Assert.DoesNotContain("SYN-COOKIE-01", outOfScopeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("SYN-AUTH-TOKEN-01", outOfScopeBody, StringComparison.Ordinal);
        Assert.Equal(beforeScopeChecks, sources.Calls);

        sources.Technical.GetResult = new SourceReadSucceeded<SourceRecord<TechnicalPayload>>(
            Record<TechnicalPayload>(1, OtherCase));
        using var mismatchedRequest = GetRequest(Route("technical", get: true), Session(factory).Id);
        using var mismatched = await client.SendAsync(mismatchedRequest);
        var mismatchBody = await mismatched.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadGateway, mismatched.StatusCode);
        AssertJsonEqual("""{"contractVersion":1,"error":{"code":"source_case_mismatch"}}""", mismatchBody);
        Assert.DoesNotContain(OtherCase, mismatchBody, StringComparison.Ordinal);
        Assert.DoesNotContain("record-1", mismatchBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostToRecordRoutesIsRejected()
    {
        var sources = AllSources();
        await using var factory = Factory(TempPath(), sources);
        EnsureCases(factory, Case);
        using var client = factory.CreateClient();
        var session = Session(factory);
        foreach (var route in new[] { Route("technical"), Route("technical", get: true) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, route);
            request.Headers.Add("Cookie", $"__Host-demo-session={session.Id}");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, sources.Calls);
    }

    [Fact]
    public async Task SequentialReadsMeetNfr001()
    {
        const string core = "case-001";
        string[] fixtureCases = [core, "case-a", "case-b", "case-c"];
        var records = fixtureCases.Select((caseId, index) => Record<TechnicalPayload>(index + 1, caseId)).ToArray();
        var sources = AllSources();
        var technical = new FakeSource<TechnicalPayload>(records);
        sources = sources with { Technical = technical };
        await using var factory = Factory(TempPath(), sources);
        EnsureCases(factory, fixtureCases);
        using var client = factory.CreateClient();
        var session = Session(factory, scopes: fixtureCases);
        var sessions = Enumerable.Range(0, 4)
            .Select(_ => Session(factory, scopes: fixtureCases)).Prepend(session).ToArray();
        var elapsed = new List<TimeSpan>(100);
        for (var i = 0; i < 100; i++)
        {
            var caseIndex = i % fixtureCases.Length;
            var caseId = fixtureCases[caseIndex];
            var started = Stopwatch.GetTimestamp();
            using var request = GetRequest(
                $"/api/v1/cases/{caseId}/sources/technical/records/record-{caseIndex + 1}",
                sessions[i % sessions.Length].Id);
            using var response = await client.SendAsync(request);
            elapsed.Add(Stopwatch.GetElapsedTime(started));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        elapsed.Sort();
        Assert.True(elapsed[(int)Math.Ceiling(0.95 * elapsed.Count) - 1] <= TimeSpan.FromSeconds(1),
            "The measured 95th-percentile source read latency exceeded one second.");

        using var unauthenticatedRequest = GetRequest($"/api/v1/cases/{core}/sources/technical/records", null);
        using var unauthenticated = await client.SendAsync(unauthenticatedRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        AssertJsonEqual("""{"contractVersion":1,"error":{"code":"unauthenticated"}}""",
            await unauthenticated.Content.ReadAsStringAsync());
    }
}
