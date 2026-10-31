using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Copilot;
using AirlineDemo.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AirlineDemo.Api.Tests;

public class CommitCountingCaseStore : DispatchProxy
{
    public ICaseStateStore Inner { get; set; } = null!;
    public int Commits;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name.StartsWith("Commit", StringComparison.Ordinal)) Interlocked.Increment(ref Commits);
        try { return targetMethod.Invoke(Inner, args); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null) { throw exception.InnerException; }
    }
}

public sealed class CopilotInvocationLedgerTests : IAsyncDisposable
{
    private static readonly byte[] AuditKey = Enumerable.Repeat((byte)7, 32).ToArray();
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 10, 0, 0, TimeSpan.Zero);
    private const string AdmissionDiagnostic = "Copilot invocation admission could not be recorded; the invocation was stopped before dispatch.";
    private const string OutcomeDiagnostic = "Copilot invocation outcome could not be recorded after admission; further dispatch was stopped.";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"copilot-ledger-{Guid.NewGuid():N}.json");
    private readonly SettableTime _time = new();
    private readonly CaptureLogs _logs = new();
    private WebApplicationFactory<Program>? _factory;
    private CommitCountingCaseStore? _counter;

    private sealed class SettableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CaptureLogs : ILoggerProvider
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Category, string Message)> Entries = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void Dispose() { }
        private sealed class Logger(CaptureLogs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Entries.Enqueue((logLevel, category, formatter(state, exception) + exception));
        }
    }

    // Fault-injecting edge store: fails every append from the given ordinal on.
    private sealed class FaultingStore(int failFrom) : ICopilotInvocationStore
    {
        public readonly InMemoryCopilotInvocationStore Inner = new();
        private int _appends;
        public Task AppendAsync(CopilotInvocationRecord record, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _appends) >= failFrom ? throw new InvalidOperationException("store offline") : Inner.AppendAsync(record, cancellationToken);
        public Task<IReadOnlyList<CopilotInvocationRecord>> ReadCaseAsync(string caseRef, CancellationToken cancellationToken) =>
            failFrom == 1 ? throw new InvalidOperationException("store offline") : Inner.ReadCaseAsync(caseRef, cancellationToken);
    }

    private WebApplicationFactory<Program> Factory(ICopilotInvocationStore? store = null)
    {
        var path = _path;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Passcode"] = "test-passcode", ["CaseState:Path"] = path, ["Demo:SeedCases"] = "true", ["Demo:SeedJourney"] = "true",
                ["Copilot:AuditKey"] = Convert.ToBase64String(AuditKey),
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(_time);
                services.AddSingleton<ILoggerProvider>(_logs);
                if (store is not null) services.AddSingleton(store);
                var descriptor = services.Last(item => item.ServiceType == typeof(ICaseStateStore));
                services.Remove(descriptor);
                services.AddSingleton<ICaseStateStore>(provider =>
                {
                    var proxy = DispatchProxy.Create<ICaseStateStore, CommitCountingCaseStore>();
                    _counter = (CommitCountingCaseStore)(object)proxy;
                    _counter.Inner = (ICaseStateStore)descriptor.ImplementationFactory!.Invoke(provider);
                    return proxy;
                });
            });
        });
        _ = _factory.Services.GetRequiredService<ICaseStateStore>();
        return _factory;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        File.Delete(_path);
    }

    private DemoSession Session(string id, string role, params string[] cases) =>
        _factory!.Services.GetRequiredService<DemoSessionStore>().Create(new DemoUser(id, "Test", role, cases));

    private static string Ref(string purpose, string value) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(AuditKey, Encoding.UTF8.GetBytes(purpose + "\0" + value)));

    private long Revision(string caseId)
    {
        var store = _factory!.Services.GetRequiredService<ICaseStateStore>();
        return store.Read(store.AuthorizeCase(caseId, store.OwnerOf(caseId)!)).Revision;
    }

    private async Task<(int Status, string Body)> Run(DemoSession? session, string operation, string? caseId,
        Func<CopilotInvocationResult> outcome, Action? onWork = null, CancellationToken cancellationToken = default)
    {
        var services = _factory!.Services;
        var result = await CopilotInvocation.RunAsync(services.GetRequiredService<ICopilotInvocationLedger>(), services.GetRequiredService<ICaseStateStore>(),
            session, operation, CopilotProvenance.CopilotClient, caseId, _ =>
            {
                onWork?.Invoke();
                _time.Now = _time.Now.AddSeconds(1);
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(outcome());
            }, cancellationToken);
        var http = new DefaultHttpContext { RequestServices = services };
        http.Response.Body = new MemoryStream();
        await result.ExecuteAsync(http);
        return (http.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray()));
    }

    private static CopilotInvocationResult Ok() => new(Results.Ok(new { done = true }), 200, "ok", CopilotOutcome.Succeeded);
    private static CopilotInvocationResult Forbidden() => new(Results.StatusCode(403), 403, "forbidden", CopilotOutcome.Denied);

    private async Task<(HttpStatusCode Status, string Body)> Get(string path, DemoSession session)
    {
        using var client = _factory!.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={session.Id}");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private IReadOnlyList<CopilotInvocationRecord> Stored() =>
        ((InMemoryCopilotInvocationStore)_factory!.Services.GetRequiredService<ICopilotInvocationStore>()).Records;

    [Fact]
    public async Task ProductionHost_InvocationLedger_DestinationAndFields()
    {
        Factory();
        var reviewer = Session("ledger-user-a", "technical-reviewer", "cp-case-a");
        var unknownRole = Session("ledger-user-b", "auditor", "cp-case-a");
        var revision = Revision("cp-case-a");
        var commits = _counter!.Commits;

        Assert.Equal(200, (await Run(reviewer, "get_case_summary", "cp-case-a", Ok)).Status);
        Assert.Equal(403, (await Run(unknownRole, "approve", "cp-case-a", Forbidden)).Status);
        await Run(reviewer, "list_my_cases", null, Ok);
        await Run(null, "get_case_summary", "cp-case-a", Forbidden);
        await Run(reviewer, "get_open_points", "cp-case-b", Forbidden);
        await Run(reviewer, "get_open_points", "cp-case-missing-9", Forbidden);
        await Run(reviewer, "drop_everything", "cp-case-a", Forbidden);

        var rows = Stored();
        Assert.Equal(14, rows.Count);
        var first = rows[0].InvocationId;
        Assert.Equal(new CopilotInvocationRecord(first, "admit", Start, "get_case_summary", "copilot-client", Ref("caller", "ledger-user-a"),
            Ref("session", reviewer.Id), "technical-reviewer", "cp-case-a", "admitted", null, null), rows[0]);
        Assert.Equal(rows[0] with { Phase = "outcome", At = Start.AddSeconds(1), Outcome = "succeeded", HttpStatus = 200, ResultCode = "ok" }, rows[1]);
        Assert.Equal(("unknown", "cp-case-a", "denied", 403, "forbidden"), (rows[3].Role, rows[3].AuthorizedCaseRef, rows[3].Outcome, rows[3].HttpStatus, rows[3].ResultCode));
        Assert.Equal(("list_my_cases", (string?)null), (rows[5].Operation, rows[5].AuthorizedCaseRef));
        Assert.Equal(("anonymous", (string?)null, (string?)null, (string?)null), (rows[7].Role, rows[7].CallerRef, rows[7].SessionRef, rows[7].AuthorizedCaseRef));
        Assert.Null(rows[9].AuthorizedCaseRef);
        Assert.Null(rows[11].AuthorizedCaseRef);
        Assert.Equal(("rejected-operation", "cp-case-a"), (rows[13].Operation, rows[13].AuthorizedCaseRef));
        Assert.All(rows.Chunk(2), pair => Assert.Equal(pair[0].InvocationId, pair[1].InvocationId));
        var serialized = JsonSerializer.Serialize(rows);
        Assert.DoesNotContain("cp-case-b", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("cp-case-missing-9", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("drop_everything", serialized, StringComparison.Ordinal);
        Assert.Equal(revision, Revision("cp-case-a"));
        Assert.Equal(commits, _counter.Commits);
    }

    [Fact]
    public async Task ProductionHost_AuditAdmissionFailure_ZeroDownstream()
    {
        var store = new FaultingStore(failFrom: 1);
        Factory(store);
        var reviewer = Session("ledger-user-a", "technical-reviewer", "cp-case-a");
        var dispatched = 0;

        var (status, body) = await Run(reviewer, "read_source", "cp-case-a", Ok, () => dispatched++);

        Assert.Equal((503, """{"contractVersion":1,"error":{"code":"audit_unavailable"}}"""), (status, body));
        Assert.Equal(0, dispatched);
        Assert.Empty(store.Inner.Records);
        Assert.Equal([(LogLevel.Error, AdmissionDiagnostic)],
            _logs.Entries.Where(entry => entry.Category == typeof(CopilotInvocationLedger).FullName).Select(entry => (entry.Level, entry.Message)));
        Assert.Equal((HttpStatusCode.ServiceUnavailable, """{"contractVersion":1,"error":{"code":"audit_unavailable"}}"""),
            await Get("/api/v1/cases/cp-case-a/copilot/activity", reviewer));
    }

    [Fact]
    public async Task ProductionHost_AuditOutcomeFailure_HonestStop()
    {
        var store = new FaultingStore(failFrom: 2);
        Factory(store);
        var reviewer = Session("ledger-user-a", "technical-reviewer", "cp-case-a");
        var dispatched = 0;

        var (status, body) = await Run(reviewer, "get_case_summary", "cp-case-a", Ok, () => dispatched++);

        Assert.Equal((503, """{"contractVersion":1,"error":{"code":"audit_unavailable"}}"""), (status, body));
        Assert.Equal(1, dispatched);
        Assert.Equal(["admit"], store.Inner.Records.Select(record => record.Phase));
        Assert.Equal([(LogLevel.Error, OutcomeDiagnostic)],
            _logs.Entries.Where(entry => entry.Category == typeof(CopilotInvocationLedger).FullName).Select(entry => (entry.Level, entry.Message)));
        var activity = JsonNode.Parse((await Get("/api/v1/cases/cp-case-a/copilot/activity", reviewer)).Body)!["items"]!.AsArray();
        Assert.Equal("admitted", activity.Single()!["outcome"]!.GetValue<string>());
        Assert.Null(activity.Single()!["resultCode"]);
    }

    [Fact]
    public async Task ProductionHost_CopilotActivity_IndependentBusinessHistory()
    {
        Factory();
        var reviewer = Session("ledger-user-a", "technical-reviewer", "cp-case-a");
        var other = Session("ledger-user-c", "finance-reviewer", "cp-case-b");
        var historyBefore = await Get("/api/cases/cp-case-a/history", reviewer);
        var revision = Revision("cp-case-a");

        await Run(reviewer, "get_case_summary", "cp-case-a", Ok);
        await Run(reviewer, "advance_stage", "cp-case-a", Forbidden);
        using var disconnect = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Run(reviewer, "question", "cp-case-a", Ok, disconnect.Cancel, disconnect.Token));
        await Run(reviewer, "get_open_points", "cp-case-b", Ok);
        await Run(other, "list_my_cases", null, Ok);

        var ids = Stored().Select(record => record.InvocationId).Distinct().ToArray();
        var (status, body) = await Get("/api/v1/cases/cp-case-a/copilot/activity", reviewer);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonSerializer.Serialize(new
        {
            contractVersion = 1, caseId = "cp-case-a", items = new object[]
            {
                new { invocationId = ids[0], operation = "get_case_summary", at = Start.AddSeconds(1), outcome = "succeeded", httpStatus = 200, resultCode = "ok", source = "from copilot" },
                new { invocationId = ids[1], operation = "advance_stage", at = Start.AddSeconds(2), outcome = "denied", httpStatus = 403, resultCode = "forbidden", source = "from copilot" },
                new { invocationId = ids[2], operation = "question", at = Start.AddSeconds(3), outcome = "cancelled", httpStatus = 499, resultCode = "cancelled", source = "from copilot" },
            },
        }, JsonSerializerOptions.Web), body);
        Assert.Equal("""{"contractVersion":1,"caseId":"cp-case-b","items":[]}""", (await Get("/api/v1/cases/cp-case-b/copilot/activity", other)).Body);
        Assert.Equal((HttpStatusCode.NotFound, ""), await Get("/api/v1/cases/cp-case-b/copilot/activity", reviewer));
        Assert.Equal(historyBefore, await Get("/api/cases/cp-case-a/history", reviewer));
        Assert.Equal(revision, Revision("cp-case-a"));
    }

    [Fact]
    public async Task ProductionHost_CopilotPrivacy_NoContentCredentialTelemetry()
    {
        Factory();
        var reviewer = Session("ledger-private-user", "legal-reviewer", "cp-case-a");
        const string question = "What is the reserve for tail N123ZZ? secret-question-text";
        string[] forbidden = ["ledger-private-user", reviewer.Id, reviewer.CsrfToken, question, DemoAuthEndpoints.CookieName];

        await Run(reviewer, "question", "cp-case-a", () => new(Results.Ok(new { answer = question }), 200, "ok", CopilotOutcome.Succeeded));

        var serialized = JsonSerializer.Serialize(Stored());
        Assert.Equal(["at", "authorizedCaseRef", "callerRef", "httpStatus", "invocationId", "operation", "outcome", "phase", "provenance", "resultCode", "role", "sessionRef"],
            JsonNode.Parse(JsonSerializer.Serialize(Stored()[0], JsonSerializerOptions.Web))!.AsObject().Select(item => item.Key).Order(StringComparer.Ordinal));
        var logged = string.Join('\n', _logs.Entries.Select(entry => entry.Message));
        var activity = (await Get("/api/v1/cases/cp-case-a/copilot/activity", reviewer)).Body;
        Assert.All(forbidden, value =>
        {
            Assert.DoesNotContain(value, serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(value, logged, StringComparison.Ordinal);
            Assert.DoesNotContain(value, activity, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task SqlStore_InvocationLedger_AppendOnlyRoundTrip()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlite(connection).Options;
        await using (var db = new CaseStateDbContext(options))
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = new SqlCopilotInvocationStore(options);
        var ledger = new CopilotInvocationLedger(store, _time, new ConfigurationBuilder().Build(), NullLogger<CopilotInvocationLedger>.Instance);
        var session = new DemoSession("sql-session", new DemoUser("sql-user", "Sql", "executive", ["cp-case-a"]), "csrf", Start.AddHours(1));

        var admission = await ledger.Admit("read_source", CopilotProvenance.McpDirect, session, "cp-case-a", TestContext.Current.CancellationToken);
        _time.Now = Start.AddSeconds(5);
        await ledger.Outcome(admission, 429, "source_read_limit", CopilotOutcome.Denied, TestContext.Current.CancellationToken);
        await ledger.Admit("list_my_cases", CopilotProvenance.McpDirect, session, null, TestContext.Current.CancellationToken);

        var rows = await store.ReadCaseAsync("cp-case-a", TestContext.Current.CancellationToken);
        Assert.Equal([admission.Record, admission.Record with { Phase = "outcome", At = Start.AddSeconds(5), Outcome = "denied", HttpStatus = 429, ResultCode = "source_read_limit" }], rows);
        await Assert.ThrowsAsync<CopilotAuditUnavailableException>(() =>
            ledger.Outcome(admission, 200, "ok", CopilotOutcome.Succeeded, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ledger.Outcome(admission, 500, "raw upstream text", CopilotOutcome.Failed, TestContext.Current.CancellationToken));
    }
}
