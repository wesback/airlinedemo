using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Copilot;
using AirlineDemo.Api.Decisions;
using AirlineDemo.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

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

    private (WebApplicationFactory<Program>, HttpClient) Build(Action<IServiceCollection>? configure)
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
                configure?.Invoke(services);
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
}
