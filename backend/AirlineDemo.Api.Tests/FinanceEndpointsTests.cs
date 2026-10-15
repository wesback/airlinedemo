using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AirlineDemo.Api.Obligations;
using AirlineDemo.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class FinanceEndpointsTests
{
    private const string Passcode = "test-passcode";
    private const string CaseId = "case-001";
    private const string ReserveDecisionsUrl = "/api/cases/case-001/reserve/decisions";
    private const string ObligationDecisionsUrl = "/api/cases/case-001/obligations/obl-001/decisions";

    private sealed record Session(string Cookie, string Csrf);

    private sealed class TempCaseState : IDisposable
    {
        public string FilePath { get; } = Path.Combine(Path.GetTempPath(), $"finance-{Guid.NewGuid():N}.json");

        public TempCaseState()
        {
            new CaseStateStore(FilePath, null).CreateCase(CaseId, "presenter");
        }

        public void Dispose()
        {
            foreach (var path in new[] { FilePath, FilePath + ".lock", FilePath + ".tmp" })
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    private sealed class RecordingNoticeSender : INoticeSender
    {
        public List<NoticeDraft> Sent { get; } = [];

        public void Send(NoticeDraft draft) => Sent.Add(draft);
    }

    private static WebApplicationFactory<Program> CreateFactory(TempCaseState temp, RecordingNoticeSender sender) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Demo:Passcode"] = Passcode,
                    ["CaseState:Path"] = temp.FilePath,
                }));
            builder.ConfigureTestServices(services => services.AddSingleton<INoticeSender>(sender));
        });

    private static async Task<Session> SignInAsync(HttpClient client, string userId, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId, passcode = Passcode }, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new Session(cookie, body.GetProperty("csrfToken").GetString()!);
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, Session? session, bool withCsrf, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url);
        if (session is not null) request.Headers.Add("Cookie", session.Cookie);
        if (session is not null && withCsrf) request.Headers.Add("X-CSRF-Token", session.Csrf);
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request, ct);
    }

    private static async Task<int> RevisionAsync(HttpClient client, Session reader, CancellationToken ct)
    {
        using var response = await SendAsync(client, HttpMethod.Get, $"/api/cases/{CaseId}/reserve", reader, false, null, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("revision").GetInt32();
    }

    private static bool HasAuditEvent(string path, string action, string actor)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return Walk(doc.RootElement, action, actor);
    }

    private static bool Walk(JsonElement element, string action, string actor)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var props = element.EnumerateObject().ToList();
                var matches = props.Any(p => string.Equals(p.Name, "action", StringComparison.OrdinalIgnoreCase)
                                             && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == action)
                              && props.Any(p => string.Equals(p.Name, "actor", StringComparison.OrdinalIgnoreCase)
                                                && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == actor);
                return matches || props.Any(p => Walk(p.Value, action, actor));
            case JsonValueKind.Array:
                return element.EnumerateArray().Any(e => Walk(e, action, actor));
            default:
                return false;
        }
    }

    [Fact]
    public async Task Scoped_gets_return_reserve_and_obligation_data()
    {
        using var temp = new TempCaseState();
        using var factory = CreateFactory(temp, new RecordingNoticeSender());
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var presenter = await SignInAsync(client, "presenter", ct);

        using var reserve = await SendAsync(client, HttpMethod.Get, $"/api/cases/{CaseId}/reserve", presenter, false, null, ct);
        Assert.Equal(HttpStatusCode.OK, reserve.StatusCode);
        var reserveBody = await reserve.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(1500m, reserveBody.GetProperty("payableTotal").GetDecimal());
        Assert.Equal(0, reserveBody.GetProperty("blockers").GetArrayLength());

        using var obligations = await SendAsync(client, HttpMethod.Get, $"/api/cases/{CaseId}/obligations", presenter, false, null, ct);
        Assert.Equal(HttpStatusCode.OK, obligations.StatusCode);
        var obligationBody = await obligations.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("obl-001", obligationBody.GetProperty("obligationId").GetString());
        Assert.Equal(1, obligationBody.GetProperty("drafts").GetArrayLength());

        using var outOfScope = await SendAsync(client, HttpMethod.Get, "/api/cases/case-999/reserve", presenter, false, null, ct);
        Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);
    }

    [Fact]
    public async Task Decisions_append_audit_events_with_session_actors()
    {
        using var temp = new TempCaseState();
        using var factory = CreateFactory(temp, new RecordingNoticeSender());
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var finance = await SignInAsync(client, "fin-1", ct);
        var legal = await SignInAsync(client, "legal-1", ct);

        var reserveRevision = await RevisionAsync(client, finance, ct);
        using var reserve = await SendAsync(client, HttpMethod.Post, ReserveDecisionsUrl, finance, true,
            new { expectedRevision = reserveRevision, decision = "approve", reason = "Figures reconcile.", actorId = "presenter" }, ct);
        Assert.Equal(HttpStatusCode.OK, reserve.StatusCode);
        var reserveBody = await reserve.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(reserveBody.GetProperty("simulated").GetBoolean());
        Assert.False(reserveBody.GetProperty("invoiceIssued").GetBoolean());

        using var drafts = await SendAsync(client, HttpMethod.Get, $"/api/cases/{CaseId}/obligations", legal, false, null, ct);
        Assert.Equal(HttpStatusCode.OK, drafts.StatusCode);
        var obligationRevision = await RevisionAsync(client, legal, ct);
        using var obligation = await SendAsync(client, HttpMethod.Post, ObligationDecisionsUrl, legal, true,
            new { expectedRevision = obligationRevision, decision = "signed-off", reason = "Notice text approved." }, ct);
        Assert.Equal(HttpStatusCode.OK, obligation.StatusCode);

        Assert.True(HasAuditEvent(temp.FilePath, "reserve.decided", "fin-1"));
        Assert.True(HasAuditEvent(temp.FilePath, "obligation.decided", "legal-1"));
        Assert.False(HasAuditEvent(temp.FilePath, "reserve.decided", "presenter"));
    }

    [Fact]
    public async Task Invalid_decisions_do_not_change_case_state()
    {
        using var temp = new TempCaseState();
        using var factory = CreateFactory(temp, new RecordingNoticeSender());
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var finance = await SignInAsync(client, "fin-1", ct);
        var legal = await SignInAsync(client, "legal-1", ct);

        using (await SendAsync(client, HttpMethod.Get, $"/api/cases/{CaseId}/obligations", legal, false, null, ct))
        {
        }
        var revision = await RevisionAsync(client, finance, ct);
        var stateBefore = await File.ReadAllTextAsync(temp.FilePath, ct);

        using var badReserve = await SendAsync(client, HttpMethod.Post, ReserveDecisionsUrl, finance, true,
            new { expectedRevision = revision, decision = "maybe", reason = "Unclear." }, ct);
        using var blankReserve = await SendAsync(client, HttpMethod.Post, ReserveDecisionsUrl, finance, true,
            new { expectedRevision = revision, decision = "approve", reason = "   " }, ct);
        using var badObligation = await SendAsync(client, HttpMethod.Post, ObligationDecisionsUrl, legal, true,
            new { expectedRevision = revision, decision = "approved", reason = "Looks fine." }, ct);
        using var blankObligation = await SendAsync(client, HttpMethod.Post, ObligationDecisionsUrl, legal, true,
            new { expectedRevision = revision, decision = "rejected", reason = "" }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, badReserve.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, blankReserve.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badObligation.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, blankObligation.StatusCode);
        Assert.Equal(revision, await RevisionAsync(client, finance, ct));
        Assert.Equal(stateBefore, await File.ReadAllTextAsync(temp.FilePath, ct));
    }

    [Fact]
    public async Task Decisions_require_role_csrf_and_current_revision()
    {
        using var temp = new TempCaseState();
        using var factory = CreateFactory(temp, new RecordingNoticeSender());
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var finance = await SignInAsync(client, "fin-1", ct);
        var legal = await SignInAsync(client, "legal-1", ct);
        var presenter = await SignInAsync(client, "presenter", ct);
        var body = new { expectedRevision = 0L, decision = "approve", reason = "Checked." };

        using (var anonymous = await SendAsync(client, HttpMethod.Post, ReserveDecisionsUrl, null, false, body, ct))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var revision = await RevisionAsync(client, finance, ct);
        var stateBefore = await File.ReadAllTextAsync(temp.FilePath, ct);
        var denied = new object[] { new { expectedRevision = revision, decision = "approve", reason = "Checked." } };

        using (var wrongRole = await SendAsync(client, HttpMethod.Post, ReserveDecisionsUrl, presenter, true, denied[0], ct))
            Assert.Equal(HttpStatusCode.Forbidden, wrongRole.StatusCode);
        using (var legalOnReserve = await SendAsync(client, HttpMethod.Post, ReserveDecisionsUrl, legal, true, denied[0], ct))
            Assert.Equal(HttpStatusCode.Forbidden, legalOnReserve.StatusCode);
        using (var missingCsrf = await SendAsync(client, HttpMethod.Post, ReserveDecisionsUrl, finance, false, denied[0], ct))
            Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
        using (var badCsrf = await SendAsync(client, HttpMethod.Post, ReserveDecisionsUrl, new Session(finance.Cookie, "wrong"), true, denied[0], ct))
            Assert.Equal(HttpStatusCode.Forbidden, badCsrf.StatusCode);
        using (var stale = await SendAsync(client, HttpMethod.Post, ReserveDecisionsUrl, finance, true,
                   new { expectedRevision = revision + 1, decision = "approve", reason = "Checked." }, ct))
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        Assert.Equal(revision, await RevisionAsync(client, finance, ct));
        Assert.Equal(stateBefore, await File.ReadAllTextAsync(temp.FilePath, ct));
    }

    [Fact]
    public async Task Decisions_are_simulated_and_cannot_deliver()
    {
        using var temp = new TempCaseState();
        var sender = new RecordingNoticeSender();
        using var factory = CreateFactory(temp, sender);
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var legal = await SignInAsync(client, "legal-1", ct);

        using var drafts = await SendAsync(client, HttpMethod.Get, $"/api/cases/{CaseId}/obligations", legal, false, null, ct);
        var draftBody = await drafts.Content.ReadFromJsonAsync<JsonElement>(ct);
        var currentDraftId = draftBody.GetProperty("drafts")[0].GetProperty("id").GetString();
        var revision = await RevisionAsync(client, legal, ct);

        using var superseded = await SendAsync(client, HttpMethod.Post, ObligationDecisionsUrl, legal, true,
            new { expectedRevision = revision, decision = "signed-off", reason = "Notice text approved.", draftId = "superseded-draft" }, ct);
        Assert.Equal(HttpStatusCode.Conflict, superseded.StatusCode);

        using var decided = await SendAsync(client, HttpMethod.Post, ObligationDecisionsUrl, legal, true,
            new { expectedRevision = revision, decision = "signed-off", reason = "Notice text approved.", draftId = currentDraftId }, ct);
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        var decidedBody = await decided.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(decidedBody.GetProperty("simulated").GetBoolean());
        Assert.False(decidedBody.GetProperty("noticeSent").GetBoolean());
        Assert.Empty(sender.Sent);
    }
}
