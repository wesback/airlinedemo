using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.SourceSystems;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace AirlineDemo.Api.Tests;
public sealed class SourceReadBoundsTests
{
    private const string Case = "source-case";
    private const string OtherCase = "other-source-case";
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class AdjustableTime : TimeProvider
    {
        public DateTimeOffset Current { get; private set; } = Now;
        public override DateTimeOffset GetUtcNow() => Current;
        public void Advance(TimeSpan amount) => Current += amount;
    }
    private sealed class FakeSource<T>(IReadOnlyList<SourceRecord<T>> records, Task? wait = null) : ISourceSystem<T> where T : class
    {
        public IReadOnlyList<SourceRecord<T>> Records { get; set; } = records;
        public int Calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SourceReadResult<IReadOnlyList<SourceRecord<T>>>? ListResult { get; set; }
        public SourceReadResult<SourceRecord<T>>? GetResult { get; set; }
        public async Task<SourceReadResult<IReadOnlyList<SourceRecord<T>>>> ListAsync(string caseId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            if (wait is not null) await wait.WaitAsync(cancellationToken);
            return ListResult ?? new SourceReadSucceeded<IReadOnlyList<SourceRecord<T>>>(
                Records.Where(record => string.Equals(record.CaseId, caseId, StringComparison.Ordinal)).ToArray());
        }
        public async Task<SourceReadResult<SourceRecord<T>>> GetAsync(string caseId, string recordId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            if (wait is not null) await wait.WaitAsync(cancellationToken);
            if (GetResult is not null) return GetResult;
            var found = Records.FirstOrDefault(record => record.RecordId == recordId);
            return found is null ? new SourceRecordNotFound<SourceRecord<T>>(recordId) : new SourceReadSucceeded<SourceRecord<T>>(found);
        }
    }
    private sealed class WaitingAdmissionStore : ISourceReadAdmissionStore
    {
        public async Task<Guid?> TryAcquireAsync(string sessionId, string caseId, DateTimeOffset requestedAt, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
        public Task<bool> ReleaseAsync(Guid leaseId, DateTimeOffset releasedAt, CancellationToken cancellationToken) => Task.FromResult(false);
    }
    private sealed class TrackingAdmissionStore(ISourceReadAdmissionStore inner) : ISourceReadAdmissionStore
    {
        public List<Guid> Acquired { get; } = [];
        public Dictionary<string, Guid> AcquiredBySession { get; } = new(StringComparer.Ordinal);
        public List<Guid> Released { get; } = [];
        public int ActiveCount => ((InMemorySourceReadAdmissionStore)inner).ActiveCount(Case, Now);
        public async Task<Guid?> TryAcquireAsync(string sessionId, string caseId, DateTimeOffset requestedAt, CancellationToken cancellationToken)
        {
            var id = await inner.TryAcquireAsync(sessionId, caseId, requestedAt, cancellationToken);
            if (id is not null)
            {
                lock (Acquired)
                {
                    Acquired.Add(id.Value);
                    AcquiredBySession.Add(sessionId, id.Value);
                }
            }
            return id;
        }
        public async Task<bool> ReleaseAsync(Guid leaseId, DateTimeOffset releasedAt, CancellationToken cancellationToken)
        {
            var released = await inner.ReleaseAsync(leaseId, releasedAt, cancellationToken);
            if (released) lock (Released) Released.Add(leaseId);
            return released;
        }
    }
    private sealed class OccupancyRaceStore : ISourceReadAdmissionStore
    {
        private readonly InMemorySourceReadAdmissionStore _inner = new();
        private readonly TaskCompletionSource _firstAdmitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<Guid> AdmittedCandidates { get; } = [];
        public Guid OccupancyLease { get; private set; }
        public int ActiveCount(string caseId, DateTimeOffset now) => _inner.ActiveCount(caseId, now);
        public async Task<Guid?> TryAcquireAsync(string sessionId, string caseId, DateTimeOffset requestedAt, CancellationToken cancellationToken)
        {
            if (sessionId == "contender")
            {
                await _firstAdmitted.Task.WaitAsync(cancellationToken);
                OccupancyLease = (await _inner.TryAcquireAsync("occupant", caseId, requestedAt, cancellationToken))!.Value;
            }
            var lease = await _inner.TryAcquireAsync(sessionId, caseId, requestedAt, cancellationToken);
            if (lease is not null)
            {
                lock (AdmittedCandidates) AdmittedCandidates.Add(lease.Value);
                if (sessionId == "winner") _firstAdmitted.TrySetResult();
            }
            return lease;
        }
        public Task<bool> ReleaseAsync(Guid leaseId, DateTimeOffset releasedAt, CancellationToken cancellationToken) =>
            _inner.ReleaseAsync(leaseId, releasedAt, cancellationToken);
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
    private static FakeSource<T> TypedSource<T>(int count, Task? wait = null) where T : class =>
        new(Enumerable.Range(1, count).Select(id => Record<T>(id)).ToArray(), wait);
    private static WebApplicationFactory<Program> Factory(
        string path,
        FakeSource<TechnicalPayload> technical,
        ISourceReadAdmissionStore? admission = null,
        DemoSessionStore? sessions = null,
        FakeSource<MaintenancePayload>? maintenance = null,
        FakeSource<FinancePayload>? finance = null,
        FakeSource<RecordsPayload>? records = null,
        FakeSource<ContractsPayload>? contracts = null,
        TimeProvider? timeProvider = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["CaseState:Path"] = path, ["Demo:Passcode"] = "test-passcode" }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(timeProvider ?? new FixedTime());
                services.RemoveAll<ISourceSystem<TechnicalPayload>>();
                services.AddSingleton<ISourceSystem<TechnicalPayload>>(technical);
                services.RemoveAll<ISourceSystem<MaintenancePayload>>();
                services.AddSingleton<ISourceSystem<MaintenancePayload>>(maintenance ?? TypedSource<MaintenancePayload>(1));
                services.RemoveAll<ISourceSystem<FinancePayload>>();
                services.AddSingleton<ISourceSystem<FinancePayload>>(finance ?? TypedSource<FinancePayload>(1));
                services.RemoveAll<ISourceSystem<RecordsPayload>>();
                services.AddSingleton<ISourceSystem<RecordsPayload>>(records ?? TypedSource<RecordsPayload>(1));
                services.RemoveAll<ISourceSystem<ContractsPayload>>();
                services.AddSingleton<ISourceSystem<ContractsPayload>>(contracts ?? TypedSource<ContractsPayload>(1));
                if (admission is not null)
                {
                    services.RemoveAll<ISourceReadAdmissionStore>();
                    services.AddSingleton(admission);
                }
                if (sessions is not null)
                {
                    services.RemoveAll<DemoSessionStore>();
                    services.AddSingleton(sessions);
                }
            });
        });
    private static FakeSource<TechnicalPayload> Source(int count, Task? wait = null) =>
        TypedSource<TechnicalPayload>(count, wait);

    private static DemoSession Session(WebApplicationFactory<Program> factory, string role = "technical-reviewer", string[]? scopes = null) =>
        factory.Services.GetRequiredService<DemoSessionStore>().Create(new DemoUser("user-" + Guid.NewGuid(), "Test", role, scopes ?? [Case]));
    private static Task<HttpResponseMessage> Get(HttpClient client, DemoSession? session, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (session is not null) request.Headers.Add("Cookie", $"__Host-demo-session={session.Id}");
        return client.SendAsync(request);
    }

    private static void EnsureCase(WebApplicationFactory<Program> factory, string caseId = Case) { var cases = factory.Services.GetRequiredService<ICaseStateStore>(); if (cases.OwnerOf(caseId) is null) cases.CreateCase(caseId, "test-owner"); }
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
        Assert.Equal("{\"contractVersion\":1,\"error\":{\"code\":\"unauthenticated\"}}", await anonymous.Content.ReadAsStringAsync());
        using var roleDenied = await Get(client, Session(factory, "legal-reviewer"), $"/api/v1/cases/{Case}/sources/technical/records");
        Assert.Equal(HttpStatusCode.Forbidden, roleDenied.StatusCode);
        Assert.Equal("{\"contractVersion\":1,\"error\":{\"code\":\"forbidden\"}}", await roleDenied.Content.ReadAsStringAsync());
        using var scopeDenied = await Get(client, Session(factory, scopes: ["other-case"]), $"/api/v1/cases/{Case}/sources/technical/records");
        Assert.Equal(HttpStatusCode.NotFound, scopeDenied.StatusCode);
        Assert.Equal(string.Empty, await scopeDenied.Content.ReadAsStringAsync());
        var nonexistentSession = Session(factory, scopes: ["not-created"]);
        using var nonexistent = await Get(client, nonexistentSession, "/api/v1/cases/not-created/sources/technical/records");
        Assert.Equal(HttpStatusCode.NotFound, nonexistent.StatusCode);
        Assert.Equal(await scopeDenied.Content.ReadAsStringAsync(), await nonexistent.Content.ReadAsStringAsync());
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
    public async Task EndpointTranslatesSourceOutcomesAndAdmissionDeadlineExactly()
    {
        var source = Source(1);
        await using (var factory = Factory(TempPath(), source))
        {
            EnsureCase(factory);
            using var client = factory.CreateClient();
            var session = Session(factory);

            source.GetResult = new SourceRecordNotFound<SourceRecord<TechnicalPayload>>("missing");
            using var missing = await Get(client, session, $"/api/v1/cases/{Case}/sources/technical/records/missing");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal("{\"contractVersion\":1,\"error\":{\"code\":\"record_not_found\"}}", await missing.Content.ReadAsStringAsync());

            source.ListResult = new SourceUnavailable<IReadOnlyList<SourceRecord<TechnicalPayload>>>("technical");
            using var unavailableList = await Get(client, session, $"/api/v1/cases/{Case}/sources/technical/records");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailableList.StatusCode);
            Assert.Equal("{\"contractVersion\":1,\"error\":{\"code\":\"source_unavailable\"}}", await unavailableList.Content.ReadAsStringAsync());

            source.ListResult = new UnknownSource<IReadOnlyList<SourceRecord<TechnicalPayload>>>("technical");
            using var unknownListResult = await Get(client, session, $"/api/v1/cases/{Case}/sources/technical/records");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownListResult.StatusCode);
            Assert.Equal("{\"contractVersion\":1,\"error\":{\"code\":\"unknown_source\"}}", await unknownListResult.Content.ReadAsStringAsync());

            source.ListResult = new SourceReadSucceeded<IReadOnlyList<SourceRecord<TechnicalPayload>>>([Record<TechnicalPayload>(1, "different-case")]);
            using var mismatchedList = await Get(client, session, $"/api/v1/cases/{Case}/sources/technical/records");
            Assert.Equal(HttpStatusCode.BadGateway, mismatchedList.StatusCode);
            Assert.Equal("{\"contractVersion\":1,\"error\":{\"code\":\"source_case_mismatch\"}}", await mismatchedList.Content.ReadAsStringAsync());

            source.GetResult = new SourceUnavailable<SourceRecord<TechnicalPayload>>("technical");
            using var unavailableGet = await Get(client, session, $"/api/v1/cases/{Case}/sources/technical/records/record-1");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailableGet.StatusCode);
            Assert.Equal("{\"contractVersion\":1,\"error\":{\"code\":\"source_unavailable\"}}", await unavailableGet.Content.ReadAsStringAsync());

            source.GetResult = new UnknownSource<SourceRecord<TechnicalPayload>>("technical");
            using var unknownGetResult = await Get(client, session, $"/api/v1/cases/{Case}/sources/technical/records/record-1");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownGetResult.StatusCode);
            Assert.Equal("{\"contractVersion\":1,\"error\":{\"code\":\"unknown_source\"}}", await unknownGetResult.Content.ReadAsStringAsync());
        }

        var untouchedSource = Source(1);
        await using var timeoutFactory = Factory(TempPath(), untouchedSource, new WaitingAdmissionStore());
        EnsureCase(timeoutFactory);
        using var timeoutClient = timeoutFactory.CreateClient();
        using var admissionTimeout = await Get(timeoutClient, Session(timeoutFactory), $"/api/v1/cases/{Case}/sources/technical/records");
        Assert.Equal(HttpStatusCode.GatewayTimeout, admissionTimeout.StatusCode);
        Assert.Equal("{\"contractVersion\":1,\"error\":{\"code\":\"source_read_timeout\"}}", await admissionTimeout.Content.ReadAsStringAsync());
        Assert.Equal(0, untouchedSource.Calls);
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
        var releaseRace = await Task.WhenAll(first.ReleaseAsync(admitted[0], Now), second.ReleaseAsync(admitted[0], Now));
        Assert.Equal(1, releaseRace.Count(released => released));
        Assert.False(await first.ReleaseAsync(admitted[0], Now));
        Assert.Equal(1, store.ActiveCount("race-case", Now));
        Assert.Equal(0, store.ActiveCount("race-case", Now.AddSeconds(1)));
    }

    [Fact]
    public async Task CompetingAdmissionRejectsAgainstOccupancyInsertedByRaceFake()
    {
        var store = new OccupancyRaceStore();
        var budget = new SourceReadBudget(store);
        var competing = await Task.WhenAll(
            Task.Run(() => budget.TryAcquireAsync("winner", "race-case", Now)),
            Task.Run(() => budget.TryAcquireAsync("contender", "race-case", Now)));
        Assert.NotNull(competing[0]);
        Assert.Null(competing[1]);
        Assert.Equal(new[] { competing[0]!.Value }, store.AdmittedCandidates);
        Assert.Equal(SourceReadBudget.MaxInFlightPerCase, store.ActiveCount("race-case", Now));
        Assert.True(await budget.ReleaseAsync(competing[0]!.Value, Now));
        Assert.Equal(1, store.ActiveCount("race-case", Now));
        Assert.True(await budget.ReleaseAsync(store.OccupancyLease, Now));
        Assert.Equal(0, store.ActiveCount("race-case", Now));
    }

    [Fact]
    public async Task SqlAdmissionStoreUsesOneSerializableTransactionForCrossSourceRollingBudget()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlite(connection).Options;
        await using (var db = new CaseStateDbContext(options)) await db.Database.MigrateAsync();
        var firstInstance = new SourceReadBudget(new SqlSourceReadAdmissionStore(options));
        var secondInstance = new SourceReadBudget(new SqlSourceReadAdmissionStore(options));
        var sourceSequence = new[] { "technical", "maintenance", "finance", "records", "contracts" };
        var admitted = new List<Guid>();
        for (var request = 0; request < SourceReadBudget.MaxRequests; request++)
        {
            _ = sourceSequence[request / 4];
            var service = request % 2 == 0 ? firstInstance : secondInstance;
            var lease = await service.TryAcquireAsync("shared-session", Case, Now);
            Assert.True(lease.HasValue);
            admitted.Add(lease!.Value);
            Assert.True(await service.ReleaseAsync(lease.Value, Now));
        }
        Assert.Equal(SourceReadBudget.MaxRequests, admitted.Distinct().Count());
        Assert.Null(await firstInstance.TryAcquireAsync("shared-session", Case, Now));
        Assert.True((await secondInstance.TryAcquireAsync("other-session", Case, Now)).HasValue);
        Assert.True((await firstInstance.TryAcquireAsync("shared-session", "other-case", Now)).HasValue);
        Assert.True((await secondInstance.TryAcquireAsync("shared-session", Case, Now.AddSeconds(60))).HasValue);
    }

    [Fact]
    public async Task SqlAdmissionStoreSerializesCompetingRequestsAtRollingBudgetBoundary()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"source-read-race-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<CaseStateDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            var caseHash = SHA256.HashData(Encoding.UTF8.GetBytes(Case));
            var sessionHash = SHA256.HashData(Encoding.UTF8.GetBytes("shared-session"));
            await using (var db = new CaseStateDbContext(options))
            {
                await db.Database.MigrateAsync();
                db.SourceReadWindows.AddRange(Enumerable.Range(0, SourceReadBudget.MaxRequests - 1)
                    .Select(_ => new SourceReadWindowRow
                    {
                        LeaseId = Guid.NewGuid(),
                        CaseHash = caseHash,
                        SessionHash = sessionHash,
                        RequestedAtTicks = Now.UtcDateTime.Ticks,
                        ExpiresAtTicks = Now.UtcDateTime.Ticks + SourceReadBudget.ReadDeadline.Ticks,
                        ReleasedAtTicks = Now.UtcDateTime.Ticks,
                    }));
                await db.SaveChangesAsync();
            }

            var first = new SqlSourceReadAdmissionStore(options);
            var second = new SqlSourceReadAdmissionStore(options);
            var results = await Task.WhenAll(
                Task.Run(() => first.TryAcquireAsync("shared-session", Case, Now, CancellationToken.None)),
                Task.Run(() => second.TryAcquireAsync("shared-session", Case, Now, CancellationToken.None)));

            Assert.Single(results, result => result.HasValue);
            Assert.Single(results, result => result is null);
            await using var verification = new CaseStateDbContext(options);
            Assert.Equal(SourceReadBudget.MaxRequests, await verification.SourceReadWindows.CountAsync());
            var admittedRows = await verification.SourceReadWindows
                .Where(row => row.SessionHash == sessionHash && row.RequestedAtTicks == Now.UtcDateTime.Ticks
                    && row.ReleasedAtTicks == null)
                .ToListAsync();
            Assert.Single(admittedRows, row => row.LeaseId != Guid.Empty);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Theory]
    [InlineData("technical-reviewer", "technical", true)]
    [InlineData("technical-reviewer", "maintenance", true)]
    [InlineData("technical-reviewer", "records", true)]
    [InlineData("technical-reviewer", "finance", false)]
    [InlineData("technical-reviewer", "contracts", false)]
    [InlineData("finance-reviewer", "technical", true)]
    [InlineData("finance-reviewer", "finance", true)]
    [InlineData("finance-reviewer", "maintenance", false)]
    [InlineData("finance-reviewer", "records", false)]
    [InlineData("finance-reviewer", "contracts", false)]
    [InlineData("legal-reviewer", "contracts", true)]
    [InlineData("legal-reviewer", "records", true)]
    [InlineData("legal-reviewer", "technical", false)]
    [InlineData("legal-reviewer", "maintenance", false)]
    [InlineData("legal-reviewer", "finance", false)]
    [InlineData("presenter", "technical", false)]
    [InlineData("presenter", "maintenance", false)]
    [InlineData("presenter", "finance", false)]
    [InlineData("presenter", "records", false)]
    [InlineData("presenter", "contracts", false)]
    [InlineData("executive", "technical", false)]
    [InlineData("executive", "maintenance", false)]
    [InlineData("executive", "finance", false)]
    [InlineData("executive", "records", false)]
    [InlineData("executive", "contracts", false)]
    [InlineData("unrecognized-role", "technical", false)]
    [InlineData("unrecognized-role", "maintenance", false)]
    [InlineData("unrecognized-role", "finance", false)]
    [InlineData("unrecognized-role", "records", false)]
    [InlineData("unrecognized-role", "contracts", false)]
    public async Task DirectListAndGetEnforceCompleteRoleSourceMatrix(string role, string source, bool allowed)
    {
        var technical = Source(1);
        var maintenance = TypedSource<MaintenancePayload>(1);
        var finance = TypedSource<FinancePayload>(1);
        var records = TypedSource<RecordsPayload>(1);
        var contracts = TypedSource<ContractsPayload>(1);
        await using var factory = Factory(TempPath(), technical, maintenance: maintenance, finance: finance,
            records: records, contracts: contracts);
        EnsureCase(factory);
        using var client = factory.CreateClient();
        var session = Session(factory, role);
        foreach (var route in new[]
        {
            $"/api/v1/cases/{Case}/sources/{source}/records",
            $"/api/v1/cases/{Case}/sources/{source}/records/record-1",
        })
        {
            using var response = await Get(client, session, route);
            if (!allowed)
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("{\"contractVersion\":1,\"error\":{\"code\":\"forbidden\"}}", await response.Content.ReadAsStringAsync());
                Assert.Equal(0, technical.Calls + maintenance.Calls + finance.Calls + records.Calls + contracts.Calls);
                continue;
            }
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(1, body.RootElement.GetProperty("contractVersion").GetInt32());
            Assert.Equal(Case, body.RootElement.GetProperty("caseId").GetString());
            Assert.Equal(source, body.RootElement.GetProperty("sourceId").GetString());
            if (route.EndsWith("/records", StringComparison.Ordinal))
                Assert.Equal(1, body.RootElement.GetProperty("records").GetArrayLength());
            else
                Assert.Equal("record-1", body.RootElement.GetProperty("record").GetProperty("recordId").GetString());
        }
    }

    [Fact]
    public async Task HttpBudgetIsSharedAcrossAuthorizedSourcesAndHosts()
    {
        var admission = new InMemorySourceReadAdmissionStore();
        var clock = new AdjustableTime();
        var sessions = new DemoSessionStore(clock);
        var firstSources = (Technical: Source(1), Maintenance: TypedSource<MaintenancePayload>(1),
            Finance: TypedSource<FinancePayload>(1), Records: TypedSource<RecordsPayload>(1),
            Contracts: TypedSource<ContractsPayload>(1));
        var secondSources = (Technical: Source(1), Maintenance: TypedSource<MaintenancePayload>(1),
            Finance: TypedSource<FinancePayload>(1), Records: TypedSource<RecordsPayload>(1),
            Contracts: TypedSource<ContractsPayload>(1));
        await using var firstFactory = Factory(TempPath(), firstSources.Technical, admission, sessions,
            firstSources.Maintenance, firstSources.Finance, firstSources.Records, firstSources.Contracts, clock);
        await using var secondFactory = Factory(TempPath(), secondSources.Technical, admission, sessions,
            secondSources.Maintenance, secondSources.Finance, secondSources.Records, secondSources.Contracts, clock);
        EnsureCase(firstFactory);
        EnsureCase(secondFactory);
        EnsureCase(firstFactory, OtherCase);
        EnsureCase(secondFactory, OtherCase);
        using var firstClient = firstFactory.CreateClient();
        using var secondClient = secondFactory.CreateClient();
        var roleSources = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["technical-reviewer"] = ["technical", "maintenance", "records"],
            ["finance-reviewer"] = ["technical", "finance"],
            ["legal-reviewer"] = ["contracts", "records"],
        };
        var technicalSession = Session(firstFactory, "technical-reviewer", [Case, OtherCase]);
        foreach (var (role, allowedSources) in roleSources)
        {
            var session = Session(firstFactory, role, [Case, OtherCase]);
            for (var request = 0; request < SourceReadBudget.MaxRequests; request++)
            {
                var client = request % 2 == 0 ? firstClient : secondClient;
                var source = allowedSources[request % allowedSources.Length];
                using var response = await Get(client, session, $"/api/v1/cases/{Case}/sources/{source}/records");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            using var rejected = await Get(secondClient, session,
                $"/api/v1/cases/{Case}/sources/{allowedSources[0]}/records");
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.Equal("{\"contractVersion\":1,\"error\":{\"code\":\"source_read_limit\"}}", await rejected.Content.ReadAsStringAsync());
        }

        using var independentSession = await Get(firstClient, technicalSession, $"/api/v1/cases/{Case}/sources/technical/records");
        Assert.Equal(HttpStatusCode.OK, independentSession.StatusCode);
        using var independentCase = await Get(secondClient, technicalSession, $"/api/v1/cases/{OtherCase}/sources/technical/records");
        Assert.Equal(HttpStatusCode.OK, independentCase.StatusCode);
        clock.Advance(SourceReadBudget.RollingWindow);
        using var boundary = await Get(firstClient, technicalSession, $"/api/v1/cases/{Case}/sources/technical/records");
        Assert.Equal(HttpStatusCode.OK, boundary.StatusCode);
    }

    [Fact]
    public async Task ConcurrentRequestsHoldCaseLeasesAndTimeoutReleasesOnlyItsOwnLease()
    {
        var technicalGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenanceGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var technical = Source(1, technicalGate.Task);
        var maintenance = TypedSource<MaintenancePayload>(1, maintenanceGate.Task);
        var tracked = new TrackingAdmissionStore(new InMemorySourceReadAdmissionStore());
        await using var factory = Factory(TempPath(), technical, tracked, maintenance: maintenance);
        EnsureCase(factory);
        using var client = factory.CreateClient();
        var firstSession = Session(factory, "technical-reviewer");
        var secondSession = Session(factory, "technical-reviewer");
        var first = Get(client, firstSession, $"/api/v1/cases/{Case}/sources/technical/records");
        await technical.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var second = Get(client, secondSession, $"/api/v1/cases/{Case}/sources/maintenance/records");
        await maintenance.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, tracked.ActiveCount);
        using var rejected = await Get(client, Session(factory), $"/api/v1/cases/{Case}/sources/records/records");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("{\"contractVersion\":1,\"error\":{\"code\":\"source_read_limit\"}}", await rejected.Content.ReadAsStringAsync());

        using var timedOut = await first.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(HttpStatusCode.GatewayTimeout, timedOut.StatusCode);
        Assert.Equal("{\"contractVersion\":1,\"error\":{\"code\":\"source_read_timeout\"}}", await timedOut.Content.ReadAsStringAsync());
        Assert.Equal(1, tracked.ActiveCount);
        Assert.Equal(2, tracked.Acquired.Count);
        Assert.Equal(2, tracked.AcquiredBySession.Count);
        var firstLease = tracked.AcquiredBySession[firstSession.Id];
        var secondLease = tracked.AcquiredBySession[secondSession.Id];
        Assert.NotEqual(firstLease, secondLease);
        Assert.Single(tracked.Released);
        Assert.Equal(firstLease, tracked.Released[0]);
        var duplicateRelease = await Task.WhenAll(
            tracked.ReleaseAsync(firstLease, Now, CancellationToken.None),
            tracked.ReleaseAsync(firstLease, Now, CancellationToken.None));
        Assert.Equal(new[] { false, false }, duplicateRelease);
        Assert.Equal(new[] { firstLease }, tracked.Released);
        Assert.Equal(1, tracked.ActiveCount);

        maintenanceGate.TrySetResult();
        using var completed = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(0, tracked.ActiveCount);
        Assert.Equal(2, tracked.Acquired.Count);
        Assert.Equal(2, tracked.Released.Count);
        Assert.Equal(new[] { firstLease, secondLease }, tracked.Released);
    }

    [Fact]
    public async Task SqlRegistrationAndMigrationExposeWindowSchemaAndValidateConnectionConfiguration()
    {
        var model = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlite("Data Source=:memory:").Options;
        using (var db = new CaseStateDbContext(model))
        {
            var mapped = db.Model.FindEntityType(typeof(SourceReadWindowRow))!;
            Assert.Equal("source_read_windows", mapped.GetTableName());
            Assert.Equal("dbo", mapped.GetSchema());
            Assert.Contains("IX_source_read_windows_session_window", mapped.GetIndexes().Select(index => index.GetDatabaseName()));
            Assert.Contains("IX_source_read_windows_case_lease", mapped.GetIndexes().Select(index => index.GetDatabaseName()));
        }
        var migration = new AirlineDemo.Api.Persistence.Migrations.SourceReadWindows();
        Assert.Contains(migration.UpOperations.OfType<CreateTableOperation>(),
            operation => operation.Name == "source_read_windows" && operation.Schema == "dbo");

        var missingConnection = new ServiceCollection();
        var missingConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Persistence:CaseStateProvider"] = "Sql" }).Build();
        Assert.Throws<InvalidOperationException>(() => SourceReadAdmissionRegistration.AddSourceReadBudget(missingConnection, missingConfig));
        var sqlConnection = "Server=tcp:case-state.database.windows.net,1433;Database=casestate;Encrypt=True;Authentication=Active Directory Managed Identity;";
        var sqlConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:CaseStateProvider"] = "Sql",
            ["ConnectionStrings:CaseState"] = sqlConnection,
        }).Build();
        var sqlServices = new ServiceCollection();
        SourceReadAdmissionRegistration.AddSourceReadBudget(sqlServices, sqlConfig);
        Assert.IsType<SqlSourceReadAdmissionStore>(sqlServices.BuildServiceProvider().GetRequiredService<ISourceReadAdmissionStore>());
        var unsafeConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:CaseStateProvider"] = "Sql",
            ["ConnectionStrings:CaseState"] = sqlConnection + "User ID=app;",
        }).Build();
        Assert.Throws<InvalidOperationException>(() => SourceReadAdmissionRegistration.AddSourceReadBudget(new ServiceCollection(), unsafeConfig));
        var fileConfig = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var fileServices = new ServiceCollection();
        SourceReadAdmissionRegistration.AddSourceReadBudget(fileServices, fileConfig);
        Assert.IsType<InMemorySourceReadAdmissionStore>(fileServices.BuildServiceProvider().GetRequiredService<ISourceReadAdmissionStore>());
    }
}
