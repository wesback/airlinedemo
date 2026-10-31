using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Copilot;
using AirlineDemo.Api.Decisions;
using AirlineDemo.Api.Finance;
using AirlineDemo.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirlineDemo.Api.Tests;

public sealed class CopilotProposalTests : IAsyncDisposable
{
    private const string Origin = "http://localhost";
    private const string CaseA = "cp-case-a";
    private static readonly string[] CpCases = ["cp-case-a", "cp-case-b", "cp-case-c"];
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 10, 0, 0, TimeSpan.Zero);
    private readonly List<string> _paths = [];
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly AdjustableTime _time = new();
    private WebApplicationFactory<Program> _factory;
    private HttpClient _client;

    private sealed class AdjustableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Reply(HttpStatusCode Status, string Body)
    {
        public JsonNode Json => JsonNode.Parse(Body)!;
    }

    public CopilotProposalTests() => (_factory, _client) = Build(null);

    private (WebApplicationFactory<Program>, HttpClient) Build(Action<IServiceCollection, string>? configure)
    {
        var path = Path.Combine(Path.GetTempPath(), $"copilot-proposal-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Passcode"] = "test-passcode", ["CaseState:Path"] = path, ["Demo:SeedCases"] = "true", ["Demo:SeedJourney"] = "true",
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(_time);
                configure?.Invoke(services, path);
            });
        });
        _factories.Add(factory);
        return (factory, factory.CreateClient());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories) await factory.DisposeAsync();
        foreach (var path in _paths) File.Delete(path);
    }

    private CopilotProposalService Service => _factory.Services.GetRequiredService<CopilotProposalService>();

    private DemoSession Session(string role, params string[] cases) =>
        _factory.Services.GetRequiredService<DemoSessionStore>().Create(new DemoUser($"cp-{role}-{Guid.NewGuid():N}", "Test", role, cases));

    private DemoSession SameUserNewSession(DemoSession session) => _factory.Services.GetRequiredService<DemoSessionStore>().Create(session.User);

    private (ICaseStateStore Store, CaseScope Scope) Scope(string caseId)
    {
        var store = _factory.Services.GetRequiredService<ICaseStateStore>();
        return (store, store.AuthorizeCase(caseId, store.OwnerOf(caseId)!));
    }

    private long Revision(string caseId) { var (store, scope) = Scope(caseId); return store.Read(scope).Revision; }

    private int Releases(string caseId)
    {
        var (store, scope) = Scope(caseId);
        return store.Read(scope).Audit.Count(item => item.Action == ReleaseEndpoints.ReleaseAction);
    }

    private static string Error(string code) => $$$"""{"contractVersion":1,"error":{"code":"{{{code}}}"}}""";

    private async Task<Reply> Send(HttpRequestMessage request, DemoSession? session, bool csrf = true)
    {
        if (session is not null)
        {
            request.Headers.Add("Cookie", $"__Host-demo-session={session.Id}");
            if (csrf) request.Headers.Add("X-CSRF-Token", session.CsrfToken);
        }
        var response = await _client.SendAsync(request);
        return new(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private Task<Reply> Get(DemoSession? session, string proposalId, string caseId = CaseA) =>
        Send(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/cases/{caseId}/copilot/proposals/{proposalId}"), session);

    private Task<Reply> Decide(DemoSession? session, string proposalId, string body, string caseId = CaseA, bool csrf = true,
        string origin = Origin, string mediaType = "application/json")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/cases/{caseId}/copilot/proposals/{proposalId}/decision")
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        };
        request.Headers.Add("Origin", origin);
        return Send(request, session, csrf);
    }

    private Task<Reply> Decision(DemoSession session, string proposalId, string decision) =>
        Decide(session, proposalId, $$"""{"decision":"{{decision}}"}""");

    private string Create(DemoSession session, string note = "Release to the next lessee.", string caseId = CaseA)
    {
        var created = Service.Create(session, caseId, Revision(caseId), note);
        Assert.Equal((200, "confirmation_required"), (created.Status, created.Code));
        return (string)created.Body["proposalId"]!;
    }

    private static string Decided(string proposalId, string state, DateTimeOffset expiresAt) =>
        new JsonObject
        {
            ["contractVersion"] = 1, ["proposalId"] = proposalId, ["caseId"] = CaseA, ["state"] = state, ["expiresAt"] = expiresAt,
        }.ToJsonString();

    [Fact]
    public async Task ProductionHost_ReleaseProposal_ExactCanonicalTuple()
    {
        var presenter = Session("presenter", CpCases);
        var revision = Revision(CaseA);
        var summary = await Send(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/cases/{CaseA}/copilot/summary"), presenter);
        var aircraftId = (string)summary.Json["aircraftId"]!;

        var created = Service.Create(presenter, CaseA, revision, "  Release to the next lessee.\n");
        Assert.Equal((200, "confirmation_required"), (created.Status, created.Code));
        var proposalId = (string)created.Body["proposalId"]!;
        Assert.True(Guid.TryParseExact(proposalId, "D", out _));
        var expected = new JsonObject
        {
            ["contractVersion"] = 1, ["proposalId"] = proposalId, ["caseId"] = CaseA, ["aircraftId"] = aircraftId, ["action"] = "final-release",
            ["expectedRevision"] = revision, ["note"] = "Release to the next lessee.", ["createdAt"] = Start, ["expiresAt"] = Start.AddMinutes(5),
            ["state"] = "pending", ["simulated"] = true, ["warning"] = CopilotProposalService.Warning,
            ["controls"] = new JsonObject
            {
                ["decision"] = $"/api/v1/cases/{CaseA}/copilot/proposals/{proposalId}/decision", ["advance"] = $"/api/v1/cases/{CaseA}/copilot/advance",
            },
        };
        Assert.Equal(expected.ToJsonString(), created.Body.ToJsonString());
        var read = await Get(presenter, proposalId);
        Assert.Equal(HttpStatusCode.OK, read.Status);
        Assert.Equal(expected.ToJsonString(), read.Body);
        Assert.Equal((revision, 0), (Revision(CaseA), Releases(CaseA)));

        // A raw 2000-unit note with a nonempty trim is accepted; the boundary and every empty form are refused without a proposal.
        Assert.Equal(200, Service.Create(presenter, CaseA, revision, new string('n', ReleaseEndpoints.MaxNoteLength)).Status);
        foreach (var (note, code) in new (string?, string)[]
        {
            (null, "release_note_required"), ("", "invalid_release_note"), (" \t\n ", "invalid_release_note"),
            (new string('n', ReleaseEndpoints.MaxNoteLength + 1), "invalid_release_note"),
            (" " + new string('n', ReleaseEndpoints.MaxNoteLength), "invalid_release_note"),
        })
        {
            var refused = Service.Create(presenter, CaseA, revision, note);
            Assert.Equal((400, Error(code)), (refused.Status, refused.Body.ToJsonString()));
        }
        Assert.Equal((409, Error("stale_revision")), Outcome(Service.Create(presenter, CaseA, revision + 1, "note")));
        Assert.Equal((403, Error("forbidden")), Outcome(Service.Create(Session("executive", CpCases), CaseA, revision, "note")));
        Assert.Equal((403, Error("forbidden")), Outcome(Service.Create(Session("legal-reviewer", CpCases), CaseA, revision, "note")));
        Assert.Equal((404, Error("case_not_found")), Outcome(Service.Create(Session("presenter", "cp-case-b"), CaseA, revision, "note")));
        Assert.Equal((revision, 0), (Revision(CaseA), Releases(CaseA)));
    }

    private static (int, string) Outcome(CopilotApiResult result) => (result.Status, result.Body.ToJsonString());

    [Fact]
    public async Task ProductionHost_ReleaseProposal_MutationAfterStorage()
    {
        var presenter = Session("presenter", CpCases);
        var buffer = "Release to the next lessee.".ToCharArray();
        var created = Service.Create(presenter, CaseA, Revision(CaseA), new string(buffer));
        var proposalId = (string)created.Body["proposalId"]!;
        var stored = (await Get(presenter, proposalId)).Body;

        // Mutating the original input buffer, the returned projection and a read projection changes nothing stored.
        Array.Fill(buffer, 'x');
        created.Body["note"] = "changed";
        created.Body["expiresAt"] = Start.AddHours(1);
        created.Body["expectedRevision"] = 99;
        var first = (await Get(presenter, proposalId)).Json.AsObject();
        first["note"] = "changed";
        first["caseId"] = "cp-case-b";
        Assert.Equal(stored, (await Get(presenter, proposalId)).Body);
        var entry = _factory.Services.GetRequiredService<ICopilotProposalStore>().Find(Guid.Parse(proposalId))!;
        Assert.Equal(("Release to the next lessee.", Start, Start.AddMinutes(5), CopilotProposalStates.Pending),
            (entry.Proposal.Note, entry.Proposal.CreatedAt, entry.Proposal.ExpiresAt, entry.State));
    }

    [Fact]
    public async Task ProductionHost_Confirmation_TrustedExplicitActions()
    {
        var presenter = Session("presenter", CpCases);
        var revision = Revision(CaseA);
        var proposalId = Create(presenter);
        var expiresAt = Start.AddMinutes(5);

        // Reading and navigation never change state.
        Assert.Equal(HttpStatusCode.OK, (await Get(presenter, proposalId)).Status);
        Assert.Equal("pending", (string)(await Get(presenter, proposalId)).Json["state"]!);

        // Untrusted or malformed decisions fail with the catalogue result and leave the proposal pending.
        foreach (var (reply, status, code) in new (Task<Reply>, HttpStatusCode, string)[]
        {
            (Decide(presenter, proposalId, """{"decision":"approve"}""", csrf: false), HttpStatusCode.Forbidden, "anti_forgery_failed"),
            (Decide(presenter, proposalId, """{"decision":"approve"}""", origin: "http://evil.test"), HttpStatusCode.Forbidden, "origin_forbidden"),
            (Decide(presenter, proposalId, """{"decision":"approve"}""", mediaType: "text/plain"), HttpStatusCode.UnsupportedMediaType, "unsupported_media_type"),
            (Decide(null, proposalId, """{"decision":"approve"}"""), HttpStatusCode.Unauthorized, "unauthenticated"),
            (Decide(presenter, proposalId, """{"approved":true}"""), HttpStatusCode.BadRequest, "invalid_request"),
            (Decide(presenter, proposalId, """{"decision":true}"""), HttpStatusCode.BadRequest, "invalid_request"),
            (Decide(presenter, proposalId, """{"decision":"approve","confirmed":true}"""), HttpStatusCode.BadRequest, "invalid_request"),
            (Decide(presenter, proposalId, """{"decision":"approve","decision":"approve"}"""), HttpStatusCode.BadRequest, "invalid_request"),
            (Decide(presenter, proposalId, """{"decision":"Approve"}"""), HttpStatusCode.BadRequest, "invalid_request"),
            (Decide(presenter, proposalId, "not json"), HttpStatusCode.BadRequest, "invalid_request"),
            (Decision(Session("executive", CpCases), proposalId, "approve"), HttpStatusCode.Forbidden, "forbidden"),
            (Decision(Session("presenter", CpCases), proposalId, "approve"), HttpStatusCode.NotFound, "confirmation_not_found"),
            (Decision(SameUserNewSession(presenter), proposalId, "approve"), HttpStatusCode.NotFound, "confirmation_not_found"),
            (Decision(presenter, Guid.NewGuid().ToString("D"), "approve"), HttpStatusCode.NotFound, "confirmation_not_found"),
            (Decision(presenter, "not-a-guid", "approve"), HttpStatusCode.NotFound, "confirmation_not_found"),
            (Decide(presenter, proposalId, """{"decision":"approve"}""", caseId: "cp-case-b"), HttpStatusCode.NotFound, "confirmation_not_found"),
        })
        {
            var result = await reply;
            Assert.Equal((status, Error(code)), (result.Status, result.Body));
        }
        Assert.Equal((HttpStatusCode.NotFound, Error("confirmation_not_found")), Status(await Get(SameUserNewSession(presenter), proposalId)));
        Assert.Equal((HttpStatusCode.Forbidden, Error("forbidden")), Status(await Get(Session("executive", CpCases), proposalId)));
        Assert.Equal("pending", (string)(await Get(presenter, proposalId)).Json["state"]!);

        // Approve, an idempotent repeat, then a terminal decline whose repeat is a no-op and which conflicts with other controls.
        Assert.Equal(Decided(proposalId, "approved", expiresAt), (await Decision(presenter, proposalId, "approve")).Body);
        Assert.Equal(Decided(proposalId, "approved", expiresAt), (await Decision(presenter, proposalId, "approve")).Body);
        Assert.Equal(Decided(proposalId, "declined", expiresAt), (await Decision(presenter, proposalId, "decline")).Body);
        Assert.Equal(Decided(proposalId, "declined", expiresAt), (await Decision(presenter, proposalId, "decline")).Body);
        Assert.Equal((HttpStatusCode.Conflict, Error("confirmation_state_conflict")), Status(await Decision(presenter, proposalId, "approve")));
        Assert.Equal((HttpStatusCode.Conflict, Error("confirmation_state_conflict")), Status(await Decision(presenter, proposalId, "cancel")));

        var cancelled = Create(presenter);
        Assert.Equal(Decided(cancelled, "cancelled", expiresAt), (await Decision(presenter, cancelled, "cancel")).Body);
        Assert.Equal(Decided(cancelled, "cancelled", expiresAt), (await Decision(presenter, cancelled, "cancel")).Body);
        Assert.Equal((HttpStatusCode.Conflict, Error("confirmation_state_conflict")), Status(await Decision(presenter, cancelled, "approve")));
        Assert.Equal((HttpStatusCode.Conflict, Error("confirmation_state_conflict")), Status(await Decision(presenter, cancelled, "decline")));
        Assert.Equal((revision, 0), (Revision(CaseA), Releases(CaseA)));

        // Every decision through the route is recorded as trusted UI, with the rejected body under its own operation.
        var records = _factory.Services.GetRequiredService<ICopilotInvocationStore>() as InMemoryCopilotInvocationStore;
        var outcomes = records!.Records.Where(item => item.Phase == "outcome" && item.AuthorizedCaseRef == CaseA).ToArray();
        Assert.All(outcomes, item => Assert.Equal("trusted-ui", item.Provenance));
        Assert.Equal(["approve", "cancel", "decline", "rejected-operation"], outcomes.Select(item => item.Operation).Distinct().Order(StringComparer.Ordinal));
    }

    private static (HttpStatusCode, string) Status(Reply reply) => (reply.Status, reply.Body);

    [Fact]
    public async Task ProductionHost_Confirmation_ExpiryBoundaryApprovalDoesNotExtend()
    {
        var presenter = Session("presenter", CpCases);
        var proposalId = Create(presenter);
        var expiresAt = Start.AddMinutes(5);

        _time.Now = expiresAt.AddMilliseconds(-1);
        Assert.Equal(Decided(proposalId, "approved", expiresAt), (await Decision(presenter, proposalId, "approve")).Body);
        Assert.Equal(Decided(proposalId, "approved", expiresAt), (await Decision(presenter, proposalId, "approve")).Body);

        _time.Now = expiresAt;
        Assert.Equal((HttpStatusCode.Gone, Error("confirmation_expired")), Status(await Decision(presenter, proposalId, "approve")));
        Assert.Equal((HttpStatusCode.Gone, Error("confirmation_expired")), Status(await Decision(presenter, proposalId, "cancel")));
        Assert.Equal("expired", (string)(await Get(presenter, proposalId)).Json["state"]!);
        _time.Now = expiresAt.AddMinutes(1);
        Assert.Equal((HttpStatusCode.Gone, Error("confirmation_expired")), Status(await Decision(presenter, proposalId, "approve")));

        // A pending proposal is not eligible at exactly its expiry either; a terminal decline stays declined past it.
        _time.Now = Start;
        var pending = Create(presenter);
        var declined = Create(presenter);
        Assert.Equal(Decided(declined, "declined", expiresAt), (await Decision(presenter, declined, "decline")).Body);
        _time.Now = expiresAt;
        Assert.Equal((HttpStatusCode.Gone, Error("confirmation_expired")), Status(await Decision(presenter, pending, "approve")));
        Assert.Equal(Decided(declined, "declined", expiresAt), (await Decision(presenter, declined, "decline")).Body);
        Assert.Equal((HttpStatusCode.Conflict, Error("confirmation_state_conflict")), Status(await Decision(presenter, declined, "approve")));
        Assert.Equal(0, Releases(CaseA));
    }

    // ---- Slice 5: atomic release, receipt and replay ----

    private const string Note = "Release to the next lessee.";

    public class ReleaseBarrierCaseStore : DispatchProxy
    {
        public ICaseStateStore Inner { get; set; } = null!;
        public Action? BeforeRelease;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(ICaseStateStore.CommitCopilotRelease)) Interlocked.Exchange(ref BeforeRelease, null)?.Invoke();
            try { return targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException exception) when (exception.InnerException is not null) { throw exception.InnerException; }
        }
    }

    // Edge ledger store that refuses the fresh release's outcome row while switched on.
    private sealed class ToggleLedger : ICopilotInvocationStore
    {
        public readonly InMemoryCopilotInvocationStore Inner = new();
        public volatile bool FailRelease;
        public Task AppendAsync(CopilotInvocationRecord record, CancellationToken cancellationToken) =>
            FailRelease && record is { Phase: "outcome", Operation: "advance_stage", ResultCode: "ok" }
                ? throw new InvalidOperationException("ledger offline") : Inner.AppendAsync(record, cancellationToken);
        public Task<IReadOnlyList<CopilotInvocationRecord>> ReadCaseAsync(string caseRef, CancellationToken cancellationToken) =>
            Inner.ReadCaseAsync(caseRef, cancellationToken);
    }

    private void Use(Action<IServiceCollection, string> configure) => (_factory, _client) = Build(configure);

    private DbContextOptions<CaseStateDbContext> UseSql()
    {
        var path = Path.Combine(Path.GetTempPath(), $"copilot-release-{Guid.NewGuid():N}.db");
        _paths.Add(path);
        var options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        using (var db = new CaseStateDbContext(options)) db.Database.Migrate();
        Use((services, _) =>
        {
            services.RemoveAll<ICaseStateStore>();
            services.AddSingleton<ICaseStateStore>(new SqlCaseStateStore(options));
            services.RemoveAll<ICopilotInvocationStore>();
            services.AddSingleton<ICopilotInvocationStore>(new SqlCopilotInvocationStore(options));
            services.RemoveAll<ICopilotProposalStore>();
            services.AddSingleton<ICopilotProposalStore>(new SqlCopilotProposalStore(options));
        });
        return options;
    }

    private static void Sql(DbContextOptions<CaseStateDbContext> options, string statement)
    {
        using var db = new CaseStateDbContext(options);
        db.Database.ExecuteSqlRaw(statement);
    }

    private CaseState State(string caseId = CaseA) { var (store, scope) = Scope(caseId); return store.Read(scope); }

    private void Gate(string caseId, string action, string decision)
    {
        var (store, scope) = Scope(caseId);
        store.Commit(scope, store.Read(scope).Revision, new CaseCommit(null, null, "presenter", action, JsonSerializer.Serialize(new { decision })));
    }

    private void Unblock(string caseId = CaseA)
    {
        Gate(caseId, FinanceEndpoints.ReserveDecisionAction, "approve");
        Gate(caseId, FinanceEndpoints.LegalDecisionAction, "signed-off");
        Assert.Empty(ReleaseEndpoints.Blockers(State(caseId)));
    }

    private string Approved(DemoSession session, string caseId = CaseA)
    {
        var proposalId = Create(session, Note, caseId);
        Assert.Equal(200, Service.Decide(session, caseId, proposalId, "approve").Status);
        return proposalId;
    }

    // Dispatches the release exactly as the advance tool does: one admitted invocation per call.
    private async Task<(int Status, string Body)> Advance(DemoSession? session, string proposalId, long revision, string? note = Note,
        string caseId = CaseA)
    {
        var services = _factory.Services;
        var result = await CopilotInvocation.RunAsync(services.GetRequiredService<ICopilotInvocationLedger>(),
            services.GetRequiredService<ICaseStateStore>(), session, "advance_stage", CopilotProvenance.TrustedUi, caseId,
            (admission, _) => Task.FromResult(Service.Release(admission, session, caseId, revision, note, proposalId).ToInvocation()),
            CancellationToken.None);
        var http = new DefaultHttpContext { RequestServices = services };
        using var stream = new MemoryStream();
        http.Response.Body = stream;
        await result.ExecuteAsync(http);
        return (http.Response.StatusCode, Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static string Receipt(string proposalId, long revision, int auditSequence, string caseId = CaseA) =>
        $$"""{"contractVersion":1,"proposalId":"{{proposalId}}","caseId":"{{caseId}}","decision":"released","revision":{{revision}},"auditSequence":{{auditSequence}},"simulated":true}""";

    private static string Blocked(params string[] blockers) =>
        new JsonObject
        {
            ["contractVersion"] = 1,
            ["error"] = new JsonObject { ["code"] = "release_blocked", ["blockers"] = new JsonArray([.. blockers.Select(item => (JsonNode)item)]) },
        }.ToJsonString();

    private async Task<CopilotInvocationRecord[]> ReleaseOutcomes(string caseId = CaseA) =>
        [.. (await _factory.Services.GetRequiredService<ICopilotInvocationStore>().ReadCaseAsync(caseId, CancellationToken.None))
            .Where(item => item is { Phase: "outcome", Operation: "advance_stage" })];

    private CopilotProposalEntry Entry(string proposalId) =>
        _factory.Services.GetRequiredService<ICopilotProposalStore>().Find(Guid.Parse(proposalId))!;

    [Fact]
    public async Task ProductionHost_AdvanceStage_ApprovalBinding()
    {
        Unblock();
        var presenter = Session("presenter", CpCases);
        var revision = Revision(CaseA);
        var pending = Create(presenter);
        var declined = Create(presenter);
        Assert.Equal(200, Service.Decide(presenter, CaseA, declined, "decline").Status);
        var cancelled = Create(presenter);
        Assert.Equal(200, Service.Decide(presenter, CaseA, cancelled, "cancel").Status);
        var approved = Approved(presenter);

        foreach (var (call, status, code) in new (Func<Task<(int, string)>>, int, string)[]
        {
            (() => Advance(presenter, pending, revision), 409, "confirmation_required"),
            (() => Advance(presenter, declined, revision), 409, "confirmation_declined"),
            (() => Advance(presenter, cancelled, revision), 409, "confirmation_cancelled"),
            (() => Advance(presenter, approved, revision, note: null), 400, "release_note_required"),
            (() => Advance(presenter, approved, revision, note: " "), 400, "invalid_release_note"),
            (() => Advance(presenter, approved, revision, note: "Another note."), 409, "confirmation_mismatch"),
            (() => Advance(presenter, approved, revision + 1), 409, "confirmation_mismatch"),
            (() => Advance(presenter, approved, revision, caseId: "cp-case-b"), 409, "confirmation_mismatch"),
            (() => Advance(SameUserNewSession(presenter), approved, revision), 404, "confirmation_not_found"),
            (() => Advance(Session("presenter", CpCases), approved, revision), 404, "confirmation_not_found"),
            (() => Advance(presenter, Guid.NewGuid().ToString("D"), revision), 404, "confirmation_not_found"),
            (() => Advance(presenter, "not-a-guid", revision), 404, "confirmation_not_found"),
            (() => Advance(Session("executive", CpCases), approved, revision), 403, "forbidden"),
            (() => Advance(null, approved, revision), 401, "unauthenticated"),
        })
        {
            Assert.Equal((status, Error(code)), await call());
        }
        Assert.Equal((revision, 0, CopilotProposalStates.Approved), (Revision(CaseA), Releases(CaseA), Entry(approved).State));

        // An approved proposal at exactly its expiry cannot release; the exact approved tuple releases once before it.
        _time.Now = Start.AddMinutes(5);
        Assert.Equal((410, Error("confirmation_expired")), await Advance(presenter, approved, revision));
        _time.Now = Start.AddMinutes(5).AddMilliseconds(-1);
        var auditSequence = State().Audit.Count + 1;
        Assert.Equal((200, Receipt(approved, revision + 1, auditSequence)), await Advance(presenter, approved, revision));
        Assert.Equal((revision + 1, 1), (Revision(CaseA), Releases(CaseA)));
    }

    [Fact]
    public async Task ProductionHost_AdvanceStage_ExistingReleasePolicy()
    {
        var presenter = Session("presenter", CpCases);
        Unblock();

        // Each gate alone blocks with the same blocker the existing release endpoint reports.
        foreach (var (action, decision, blocker) in new[]
        {
            (FinanceEndpoints.ReserveDecisionAction, "return", "reserve_not_approved"),
            (FinanceEndpoints.LegalDecisionAction, "rejected", "obligations_not_signed_off"),
        })
        {
            Gate(CaseA, action, decision);
            var revision = Revision(CaseA);
            Assert.Equal((409, Blocked(blocker)), await Advance(presenter, Approved(presenter), revision));
            Unblock();
        }

        // Case C's technical review is not accepted; with both other gates open all three block, in the existing order.
        Gate("cp-case-c", FinanceEndpoints.ReserveDecisionAction, "return");
        Gate("cp-case-c", FinanceEndpoints.LegalDecisionAction, "rejected");
        var caseC = Revision("cp-case-c");
        Assert.Equal((409, Blocked("technical_review_not_accepted", "reserve_not_approved", "obligations_not_signed_off")),
            await Advance(presenter, Approved(presenter, "cp-case-c"), caseC, caseId: "cp-case-c"));

        // A revision change after approval is stale; a release by another proposal makes a later one already released.
        var stale = Approved(presenter);
        var staleRevision = Revision(CaseA);
        Unblock();
        Assert.Equal((409, Error("stale_revision")), await Advance(presenter, stale, staleRevision));
        var first = Approved(presenter);
        var released = Revision(CaseA);
        Assert.Equal(200, (await Advance(presenter, first, released)).Status);
        var later = Approved(presenter);
        Assert.Equal((409, Error("already_released")), await Advance(presenter, later, released + 1));
        Assert.Equal((released + 1, 1, 0), (Revision(CaseA), Releases(CaseA), Releases("cp-case-c")));
        Assert.Equal((CopilotProposalStates.Approved, CopilotProposalStates.Approved), (Entry(stale).State, Entry(later).State));
    }

    [Fact]
    public async Task ProductionHost_ReleaseAtomicCommit_AllStoredInvariants()
    {
        foreach (var sql in new[] { false, true })
        {
            if (sql) UseSql();
            Unblock();
            var presenter = Session("presenter", CpCases);
            var before = State();
            var proposalId = Approved(presenter);
            var (status, body) = await Advance(presenter, proposalId, before.Revision);
            var receipt = Receipt(proposalId, before.Revision + 1, before.Audit.Count + 1);
            Assert.Equal((200, receipt), (status, body));

            var after = State();
            Assert.Equal(before.Revision + 1, after.Revision);
            Assert.Equal(before.Audit, after.Audit.Take(before.Audit.Count));
            Assert.Equal(new AuditEvent(before.Audit.Count + 1, before.Revision + 1, presenter.User.Id, ReleaseEndpoints.ReleaseAction,
                $$"""{"decision":"released","note":"{{Note}}"}""", Start), after.Audit[^1]);
            var entry = Entry(proposalId);
            var fingerprint = CopilotReleasePolicy.Fingerprint(entry.Proposal);
            Assert.Equal((CopilotProposalStates.Consumed, new CopilotReleaseReceipt(fingerprint, receipt)), (entry.State, entry.Receipt));
            var outcome = Assert.Single(await ReleaseOutcomes());
            Assert.Equal(("succeeded", 200, "ok", "trusted-ui"), (outcome.Outcome, outcome.HttpStatus, outcome.ResultCode, outcome.Provenance));
            if (!sql)
            {
                var reloaded = new CaseStateStore(_paths[^1]);
                Assert.Equal(after.Audit, reloaded.Read(reloaded.AuthorizeCase(CaseA, reloaded.OwnerOf(CaseA)!)).Audit);
            }
        }
    }

    [Fact]
    public async Task ProductionHost_ReleaseRace_ReadCheckVersusCommit()
    {
        ReleaseBarrierCaseStore? barrier = null;
        Use((services, _) =>
        {
            var descriptor = services.Single(item => item.ServiceType == typeof(ICaseStateStore));
            services.Remove(descriptor);
            services.AddSingleton<ICaseStateStore>(provider =>
            {
                var proxy = DispatchProxy.Create<ICaseStateStore, ReleaseBarrierCaseStore>();
                barrier = (ReleaseBarrierCaseStore)(object)proxy;
                barrier.Inner = (ICaseStateStore)descriptor.ImplementationFactory!.Invoke(provider);
                return proxy;
            });
        });
        Unblock();
        var presenter = Session("presenter", CpCases);

        // The revision changes between the service's checks and the store's commit boundary.
        var raced = Approved(presenter);
        var revision = Revision(CaseA);
        barrier!.BeforeRelease = () => Gate(CaseA, FinanceEndpoints.ReserveDecisionAction, "approve");
        Assert.Equal((409, Error("stale_revision")), await Advance(presenter, raced, revision));
        Assert.Equal((revision + 1, 0, CopilotProposalStates.Approved), (Revision(CaseA), Releases(CaseA), Entry(raced).State));

        // The session is revoked between the checks and the commit.
        var revoked = Approved(presenter);
        revision = Revision(CaseA);
        barrier.BeforeRelease = () => _factory.Services.GetRequiredService<DemoSessionStore>().Remove(presenter.Id);
        Assert.Equal((401, Error("unauthenticated")), await Advance(presenter, revoked, revision));
        Assert.Equal((revision, 0, CopilotProposalStates.Approved), (Revision(CaseA), Releases(CaseA), Entry(revoked).State));
        Assert.DoesNotContain(await ReleaseOutcomes(), item => item.ResultCode == "ok");
    }

    [Fact]
    public async Task ProductionHost_ReleaseParallelRetry_OneBusinessChange()
    {
        Unblock();
        var presenter = Session("presenter", CpCases);
        var before = State();
        var proposalId = Approved(presenter);
        var replies = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => Advance(presenter, proposalId, before.Revision))));
        var receipt = Receipt(proposalId, before.Revision + 1, before.Audit.Count + 1);
        Assert.Equal([(200, receipt), (200, receipt)], replies);
        Assert.Equal((before.Revision + 1, 1, before.Audit.Count + 1), (Revision(CaseA), Releases(CaseA), State().Audit.Count));
        var outcomes = await ReleaseOutcomes();
        Assert.Equal(2, outcomes.Length);
        Assert.Equal(2, outcomes.Select(item => item.InvocationId).Distinct().Count());
        Assert.All(outcomes, item => Assert.Equal((200, "ok"), (item.HttpStatus, item.ResultCode)));
    }

    [Fact]
    public async Task ProductionHost_ReleaseAtomicFailure_CommitReceiptAuditRollback()
    {
        var (failPersist, failConsume) = (false, false);
        var ledger = new ToggleLedger();
        Use((services, path) =>
        {
            services.RemoveAll<ICaseStateStore>();
            services.AddSingleton<ICaseStateStore>(new CaseStateStore(path, () => { if (failPersist) throw new IOException("disk full"); }));
            services.RemoveAll<ICopilotProposalStore>();
            services.AddSingleton<ICopilotProposalStore>(provider => new InMemoryCopilotProposalStore(
                ((CaseStateStore)provider.GetRequiredService<ICaseStateStore>()).SyncRoot,
                () => { if (failConsume) throw new InvalidOperationException("receipt store offline"); }));
            services.RemoveAll<ICopilotInvocationStore>();
            services.AddSingleton<ICopilotInvocationStore>(ledger);
        });
        Unblock();
        var presenter = Session("presenter", CpCases);
        var before = State();
        var proposalId = Approved(presenter);

        async Task Fails(Action arm, Action disarm, int status, string code)
        {
            arm();
            Assert.Equal((status, Error(code)), await Advance(presenter, proposalId, before.Revision));
            disarm();
            var reloaded = new CaseStateStore(_paths[^1]);
            Assert.Equal(before.Audit, reloaded.Read(reloaded.AuthorizeCase(CaseA, reloaded.OwnerOf(CaseA)!)).Audit);
            Assert.Equal(before.Revision, State().Revision);
            Assert.Equal(before.Audit, State().Audit);
            Assert.Equal((CopilotProposalStates.Approved, (CopilotReleaseReceipt?)null), (Entry(proposalId).State, Entry(proposalId).Receipt));
            Assert.DoesNotContain(await ReleaseOutcomes(), item => item.ResultCode == "ok");
        }

        await Fails(() => failPersist = true, () => failPersist = false, 503, "release_unavailable");
        await Fails(() => failConsume = true, () => failConsume = false, 503, "release_unavailable");
        await Fails(() => ledger.FailRelease = true, () => ledger.FailRelease = false, 503, "audit_unavailable");

        // Nothing partial remains, so the same approved proposal still releases exactly once.
        Assert.Equal((200, Receipt(proposalId, before.Revision + 1, before.Audit.Count + 1)), await Advance(presenter, proposalId, before.Revision));
        Assert.Equal(1, Releases(CaseA));
    }

    [Fact]
    public async Task ProductionHost_ReleaseAtomicFailure_SqlTransactionRollback()
    {
        var options = UseSql();
        Unblock();
        var presenter = Session("presenter", CpCases);
        var before = State();
        var proposalId = Approved(presenter);
        foreach (var (table, when, code) in new[]
        {
            ("copilot_release_receipts", "", "release_unavailable"),
            ("case_events", "", "release_unavailable"),
            ("copilot_invocations", "WHEN NEW.ResultCode = 'ok' AND NEW.Operation = 'advance_stage'", "audit_unavailable"),
        })
        {
            Sql(options, $"CREATE TRIGGER fault BEFORE INSERT ON {table} {when} BEGIN SELECT RAISE(ABORT, 'fault'); END;");
            Assert.Equal((503, Error(code)), await Advance(presenter, proposalId, before.Revision));
            Sql(options, "DROP TRIGGER fault;");
            Assert.Equal(before.Revision, State().Revision);
            Assert.Equal(before.Audit, State().Audit);
            Assert.Equal((CopilotProposalStates.Approved, (CopilotReleaseReceipt?)null), (Entry(proposalId).State, Entry(proposalId).Receipt));
            Assert.DoesNotContain(await ReleaseOutcomes(), item => item.ResultCode == "ok");
        }
        Assert.Equal((200, Receipt(proposalId, before.Revision + 1, before.Audit.Count + 1)), await Advance(presenter, proposalId, before.Revision));
    }

    [Fact]
    public async Task ProductionHost_ReleaseExactReplay_OriginalReceiptBytes()
    {
        Unblock();
        var presenter = Session("presenter", CpCases);
        var revision = Revision(CaseA);
        var proposalId = Approved(presenter);
        var original = await Advance(presenter, proposalId, revision);
        Assert.Equal(200, original.Status);

        // After expiry and a later revision change the exact retry returns the original bytes without a second release.
        _time.Now = Start.AddMinutes(30);
        Gate(CaseA, FinanceEndpoints.ReserveDecisionAction, "approve");
        var audit = State().Audit;
        Assert.Equal(original, await Advance(presenter, proposalId, revision));
        Assert.Equal((revision + 2, 1), (Revision(CaseA), Releases(CaseA)));
        Assert.Equal(audit, State().Audit);
        Assert.Equal(CopilotProposalStates.Consumed, Entry(proposalId).State);
        Assert.Equal(2, (await ReleaseOutcomes()).Count(item => item.ResultCode == "ok"));
    }

    [Fact]
    public async Task ProductionHost_ReleaseReplayMismatch_NoEffects()
    {
        Unblock();
        var presenter = Session("presenter", CpCases);
        var revision = Revision(CaseA);
        var proposalId = Approved(presenter);
        var original = await Advance(presenter, proposalId, revision);
        var audit = State().Audit;

        foreach (var (call, status, code) in new (Func<Task<(int, string)>>, int, string)[]
        {
            (() => Advance(presenter, proposalId, revision, note: "A different note."), 409, "replay_mismatch"),
            (() => Advance(presenter, proposalId, revision + 1), 409, "replay_mismatch"),
            (() => Advance(presenter, proposalId, revision, caseId: "cp-case-b"), 409, "replay_mismatch"),
            (() => Advance(SameUserNewSession(presenter), proposalId, revision), 404, "confirmation_not_found"),
            (() => Advance(Session("presenter", CpCases), proposalId, revision), 404, "confirmation_not_found"),
        })
        {
            Assert.Equal((status, Error(code)), await call());
        }
        Assert.Equal((revision + 1, 1, 0), (Revision(CaseA), Releases(CaseA), Releases("cp-case-b")));
        Assert.Equal(audit, State().Audit);
        Assert.Equal(original.Body, Entry(proposalId).Receipt!.Bytes);
        Assert.Equal(original, await Advance(presenter, proposalId, revision));
    }

    // ---- Slice 6: trusted UI-assisted advance route over the advance_stage tool ----

    private Task<Reply> PostAdvance(DemoSession? session, string body, bool csrf = true, string origin = Origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/cases/{CaseA}/copilot/advance")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", origin);
        return Send(request, session, csrf);
    }

    private static string ToolError(int status, string code) => $$$"""{"contractVersion":1,"httpStatus":{{{status}}},"error":{"code":"{{{code}}}"}}""";

    [Fact]
    public async Task ProductionHost_AdvanceRoute_ProposeApproveRetryReleasesOnce()
    {
        Unblock();
        var presenter = Session("presenter", CpCases);
        var before = State();
        var revision = before.Revision;

        foreach (var (reply, status, body) in new (Task<Reply>, HttpStatusCode, string)[]
        {
            (PostAdvance(presenter, $$"""{"expectedRevision":{{revision}},"releaseNote":"n","confirmed":true}"""), HttpStatusCode.BadRequest, ToolError(400, "invalid_request")),
            (PostAdvance(presenter, $$"""{"expectedRevision":{{revision}}}"""), HttpStatusCode.BadRequest, ToolError(400, "invalid_request")),
            (PostAdvance(presenter, $$"""{"expectedRevision":{{revision}},"releaseNote":7}"""), HttpStatusCode.BadRequest, ToolError(400, "invalid_request")),
            (PostAdvance(presenter, $$"""{"expectedRevision":"{{revision}}","releaseNote":"n"}"""), HttpStatusCode.BadRequest, ToolError(400, "invalid_request")),
            (PostAdvance(presenter, $$"""{"expectedRevision":{{revision}}.0,"releaseNote":"n"}"""), HttpStatusCode.BadRequest, ToolError(400, "invalid_request")),
            (PostAdvance(presenter, $$"""{"expectedRevision":{{revision}},"releaseNote":" "}"""), HttpStatusCode.BadRequest, ToolError(400, "invalid_release_note")),
            (PostAdvance(presenter, $$"""{"expectedRevision":{{revision}},"releaseNote":"{{new string('n', 2001)}}"}"""), HttpStatusCode.BadRequest, ToolError(400, "invalid_release_note")),
            (PostAdvance(Session("executive", CpCases), $$"""{"expectedRevision":{{revision}},"releaseNote":"n"}"""), HttpStatusCode.Forbidden, ToolError(403, "forbidden")),
            (PostAdvance(presenter, $$"""{"expectedRevision":{{revision}},"releaseNote":"n"}""", csrf: false), HttpStatusCode.Forbidden, Error("anti_forgery_failed")),
            (PostAdvance(presenter, $$"""{"expectedRevision":{{revision}},"releaseNote":"n"}""", origin: "http://evil.test"), HttpStatusCode.Forbidden, Error("origin_forbidden")),
        })
        {
            Assert.Equal((status, body), Status(await reply));
        }

        // Without a proposal id the route creates a pending proposal and returns its confirmation tuple for the trusted UI.
        var proposed = await PostAdvance(presenter, $$"""{"expectedRevision":{{revision}},"releaseNote":"  {{Note}}  "}""");
        Assert.Equal(HttpStatusCode.OK, proposed.Status);
        var uiOnly = proposed.Json["uiOnly"]!.AsObject();
        var proposalId = (string)uiOnly["proposalId"]!;
        Assert.True(JsonNode.DeepEquals(new JsonObject
        {
            ["contractVersion"] = 1, ["httpStatus"] = 200, ["resultCode"] = "confirmation_required", ["uiOnly"] = (await Get(presenter, proposalId)).Json,
        }, proposed.Json), proposed.Body);
        Assert.Equal((Note, "pending"), ((string)uiOnly["note"]!, (string)uiOnly["state"]!));

        var retry = $$"""{"expectedRevision":{{revision}},"releaseNote":"{{Note}}","proposalId":"{{proposalId}}"}""";
        Assert.Equal((HttpStatusCode.Conflict, ToolError(409, "confirmation_required")), Status(await PostAdvance(presenter, retry)));
        Assert.Equal(HttpStatusCode.OK, (await Decision(presenter, proposalId, "approve")).Status);

        var receipt = Receipt(proposalId, revision + 1, before.Audit.Count + 1);
        Assert.Equal((HttpStatusCode.OK, receipt), Status(await PostAdvance(presenter, retry)));
        Assert.Equal((HttpStatusCode.OK, receipt), Status(await PostAdvance(presenter, retry)));
        Assert.Equal((revision + 1, 1), (Revision(CaseA), Releases(CaseA)));

        var outcomes = await ReleaseOutcomes();
        Assert.All(outcomes, item => Assert.Equal("trusted-ui", item.Provenance));
        // Schema rejections happen before the case is authorized, so they are not case-scoped ledger rows. The fake clock is
        // frozen, so rows share a timestamp and are compared without order.
        Assert.Equal(["confirmation_required", "confirmation_required", "forbidden", "invalid_release_note", "invalid_release_note", "ok", "ok"],
            outcomes.Select(item => item.ResultCode).Order(StringComparer.Ordinal));
    }
}
