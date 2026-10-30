using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AirlineDemo.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

/// <summary>
/// The full demo journey through the production host: the seeded aircraft reach legal sign-off, and the transition lead
/// (presenter) releases an aircraft only once every gate is passed.
/// </summary>
public sealed class ReleaseEndpointsTests : IAsyncDisposable
{
    private const string Passcode = "test-passcode";
    private const string ReleasePath = "/api/cases/cp-case-a/release";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"release-{Guid.NewGuid():N}.json");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ReleaseEndpointsTests()
    {
        var path = _path;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Passcode"] = Passcode,
                ["CaseState:Path"] = path,
                ["Demo:SeedCases"] = "true",
                ["Demo:SeedJourney"] = "true",
            })));
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        File.Delete(_path);
    }

    private async Task<(string Cookie, string Csrf)> SignInAsync(string userId)
    {
        using var response = await _client.PostAsJsonAsync("/api/auth/sign-in", new { userId, passcode = Passcode });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (cookie, json.RootElement.GetProperty("csrfToken").GetString()!);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, (string Cookie, string Csrf)? session, bool csrf = true)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (session is { } s)
        {
            request.Headers.Add("Cookie", s.Cookie);
            if (csrf) request.Headers.Add("X-CSRF-Token", s.Csrf);
        }
        return await _client.SendAsync(request);
    }

    private CaseState State(string caseId)
    {
        var store = _factory.Services.GetRequiredService<ICaseStateStore>();
        return store.Read(store.AuthorizeCase(caseId, "presenter"));
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task Presenter_releases_the_aircraft_only_after_legal_sign_off()
    {
        var presenter = await SignInAsync("presenter");
        var legal = await SignInAsync("legal-1");
        var revision = State("cp-case-a").Revision;

        // Seeded at the legal gate: release is refused and nothing is written.
        using var blocked = await SendAsync(HttpMethod.Post, ReleasePath, new { expectedRevision = revision, note = "Ready." }, presenter);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal(["obligations_not_signed_off"], (await JsonAsync(blocked)).GetProperty("blockers").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(revision, State("cp-case-a").Revision);

        // The legal reviewer finds a current notice draft and signs it off.
        using var obligations = await SendAsync(HttpMethod.Get, "/api/cases/cp-case-a/obligations", null, legal);
        Assert.Equal(HttpStatusCode.OK, obligations.StatusCode);
        var draft = (await JsonAsync(obligations)).GetProperty("drafts").EnumerateArray().Single();
        Assert.Equal(0, draft.GetProperty("status").GetInt32());
        Assert.Equal("OBL-1", draft.GetProperty("obligationId").GetString());
        using var signOff = await SendAsync(HttpMethod.Post, "/api/cases/cp-case-a/obligations/OBL-1/decisions",
            new { expectedRevision = revision, decision = "signed-off", reason = "Return conditions met." }, legal);
        Assert.Equal(HttpStatusCode.OK, signOff.StatusCode);
        revision++;

        using var released = await SendAsync(HttpMethod.Post, ReleasePath, new { expectedRevision = revision, note = "  Handed over to the next lessee.  " }, presenter);
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        var body = await JsonAsync(released);
        Assert.Equal("released", body.GetProperty("decision").GetString());
        Assert.True(body.GetProperty("simulated").GetBoolean());
        Assert.Equal(revision + 1, body.GetProperty("revision").GetInt64());

        var last = State("cp-case-a").Audit[^1];
        Assert.Equal("aircraft.released", last.Action);
        Assert.Equal("presenter", last.Actor);
        Assert.Equal("""{"decision":"released","note":"Handed over to the next lessee."}""", last.Detail);

        // A second release is refused and leaves the case unchanged.
        using var again = await SendAsync(HttpMethod.Post, ReleasePath, new { expectedRevision = revision + 1, note = "Again." }, presenter);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("Aircraft is already released.", (await JsonAsync(again)).GetProperty("error").GetString());
        Assert.Equal(revision + 1, State("cp-case-a").Revision);
    }

    [Fact]
    public async Task Release_rejects_missing_session_role_csrf_note_scope_and_stale_revision()
    {
        var presenter = await SignInAsync("presenter");
        var legal = await SignInAsync("legal-1");
        var revision = State("cp-case-a").Revision;
        var valid = new { expectedRevision = revision, note = "Ready." };

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, ReleasePath, valid, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, ReleasePath, valid, presenter, csrf: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, ReleasePath, valid, legal)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, "/api/cases/case-003/release", valid, presenter)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, ReleasePath, new { expectedRevision = revision, note = "   " }, presenter)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, ReleasePath, new { expectedRevision = revision, note = new string('x', 2001) }, presenter)).StatusCode);
        using var stale = await SendAsync(HttpMethod.Post, ReleasePath, new { expectedRevision = revision - 1, note = "Ready." }, presenter);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("Case revision is stale.", (await JsonAsync(stale)).GetProperty("error").GetString());

        // Case B is still at the finance gate, so both later gates block it.
        using var caseB = await SendAsync(HttpMethod.Post, "/api/cases/cp-case-b/release",
            new { expectedRevision = State("cp-case-b").Revision, note = "Ready." }, presenter);
        Assert.Equal(HttpStatusCode.Conflict, caseB.StatusCode);
        Assert.Equal(["reserve_not_approved", "obligations_not_signed_off"],
            (await JsonAsync(caseB)).GetProperty("blockers").EnumerateArray().Select(e => e.GetString()).ToArray());

        Assert.Equal(revision, State("cp-case-a").Revision);
        Assert.DoesNotContain(State("cp-case-a").Audit, item => item.Action == "aircraft.released");
    }
}
