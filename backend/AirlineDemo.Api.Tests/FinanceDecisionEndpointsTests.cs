using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AirlineDemo.Api.Finance;
using AirlineDemo.Api.Obligations;
using AirlineDemo.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

/// <summary>
/// Criterion-to-test map (issue #245):
/// AC1 session actors and one audit event per decision: Valid_decisions_append_audit_events_with_session_actors.
/// AC2 role, CSRF and case scope: Decisions_require_session_role_csrf_and_case_scope.
/// AC3 invalid and stale input leaves state unchanged: Invalid_and_stale_decisions_do_not_change_case_state.
/// AC4 simulated flags and no delivery: Decisions_are_simulated_and_cannot_deliver.
/// </summary>
public sealed class FinanceDecisionEndpointsTests
{
    private const string Passcode = "test-passcode";
    private const string Owner = "demo-owner";
    private const string ReservePath = "/api/cases/case-001/reserve/decisions";
    private const string LegalPath = "/api/cases/case-001/obligations/OBL-1/decisions";
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 1, 0, 0, TimeSpan.Zero);

    private sealed class RecordingNoticeSender : INoticeSender
    {
        public int Calls { get; private set; }

        public void Send(NoticeDraft draft) => Calls++;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public readonly string FilePath = Path.Combine(Path.GetTempPath(), $"finance-decisions-{Guid.NewGuid():N}.json");
        public readonly RecordingNoticeSender Sender = new();
        public readonly WebApplicationFactory<Program> Factory;
        public readonly HttpClient Client;

        public Harness()
        {
            var seed = new CaseStateStore(FilePath);
            seed.CreateCase("case-001", Owner);
            seed.CreateCase("case-002", Owner);

            var path = FilePath;
            var sender = Sender;
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Demo:Passcode"] = Passcode,
                        ["CaseState:Path"] = path,
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
                    services.AddSingleton<INoticeSender>(sender);
                });
            });
            Client = Factory.CreateClient();
        }

        public Harness Seed(string caseId, params NoticeDraft[] drafts)
        {
            Factory.Services.GetRequiredService<FinanceInputStore>().Set(caseId,
                new FinanceInputs(null, null, Obligation(), drafts));
            return this;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
            File.Delete(FilePath);
        }
    }

    private static Obligation Obligation() => new(
        "OBL-1",
        2,
        new DateOnly(2026, 10, 10),
        new DateOnly(2026, 10, 14),
        new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero),
        [new LinkedRequirement("REQ-1", RequirementEvidence.Satisfied, 2)],
        new TechnicalReview("reviewer-1", true, 2));

    private static NoticeDraft Draft(string id, NoticeDraftStatus status) =>
        new(id, "OBL-1", 2, ["REQ-1"], Now, status, "Synthetic notice body.");

    private static async Task<(string Cookie, string Csrf)> SignInAsync(HttpClient client, string userId)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId, passcode = Passcode });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (cookie, json.RootElement.GetProperty("csrfToken").GetString()!);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body, string? cookie = null, string? csrf = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        if (csrf is not null) request.Headers.Add("X-CSRF-Token", csrf);
        return await client.SendAsync(request);
    }

    private static CaseState State(Harness h, string caseId)
    {
        var store = h.Factory.Services.GetRequiredService<ICaseStateStore>();
        return store.Read(store.AuthorizeCase(caseId, Owner));
    }

    private static (long Revision, string Audit) Snapshot(Harness h, string caseId)
    {
        var state = State(h, caseId);
        return (state.Revision, JsonSerializer.Serialize(state.Audit));
    }

    [Fact]
    public async Task Valid_decisions_append_audit_events_with_session_actors()
    {
        await using var h = new Harness().Seed("case-001", Draft("DRAFT-1", NoticeDraftStatus.Current));
        var fin = await SignInAsync(h.Client, "fin-1");
        var legal = await SignInAsync(h.Client, "legal-1");

        var responses = new[]
        {
            await PostAsync(h.Client, ReservePath, new { expectedRevision = 0, decision = "approve", reason = "Reserve ok.", actorId = "someone-else" }, fin.Cookie, fin.Csrf),
            await PostAsync(h.Client, ReservePath, new { expectedRevision = 1, decision = "return", reason = "Recheck usage." }, fin.Cookie, fin.Csrf),
            await PostAsync(h.Client, LegalPath, new { expectedRevision = 2, decision = "signed-off", reason = "Notice approved." }, legal.Cookie, legal.Csrf),
            await PostAsync(h.Client, LegalPath, new { expectedRevision = 3, decision = "rejected", reason = "Notice rejected.", actorId = "fin-1" }, legal.Cookie, legal.Csrf),
        };
        foreach (var response in responses) Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var audit = State(h, "case-001").Audit;
        Assert.Equal(4, audit.Count);
        Assert.Equal(["fin-1", "fin-1", "legal-1", "legal-1"], audit.Select(a => a.Actor).ToArray());
        Assert.Equal(["reserve.decided", "reserve.decided", "obligation.legal-decided", "obligation.legal-decided"],
            audit.Select(a => a.Action).ToArray());
        Assert.Equal(4, State(h, "case-001").Revision);
    }

    [Fact]
    public async Task Decisions_require_session_role_csrf_and_case_scope()
    {
        await using var h = new Harness().Seed("case-001", Draft("DRAFT-1", NoticeDraftStatus.Current));
        var presenter = await SignInAsync(h.Client, "presenter");
        var fin = await SignInAsync(h.Client, "fin-1");
        var legal = await SignInAsync(h.Client, "legal-1");
        var before = Snapshot(h, "case-001");

        var reserveBody = new { expectedRevision = 0, decision = "approve", reason = "Reserve ok." };
        var legalBody = new { expectedRevision = 0, decision = "signed-off", reason = "Notice approved." };

        // No session.
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(h.Client, ReservePath, reserveBody)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(h.Client, LegalPath, legalBody)).StatusCode);

        // Wrong role.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(h.Client, ReservePath, reserveBody, presenter.Cookie, presenter.Csrf)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(h.Client, LegalPath, legalBody, presenter.Cookie, presenter.Csrf)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(h.Client, ReservePath, reserveBody, legal.Cookie, legal.Csrf)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(h.Client, LegalPath, legalBody, fin.Cookie, fin.Csrf)).StatusCode);

        // Missing or invalid CSRF token.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(h.Client, ReservePath, reserveBody, fin.Cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(h.Client, ReservePath, reserveBody, fin.Cookie, "bad-token")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(h.Client, LegalPath, legalBody, legal.Cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(h.Client, LegalPath, legalBody, legal.Cookie, "bad-token")).StatusCode);

        // Out-of-scope case: fin-1 and legal-1 are scoped to case-001 only.
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(h.Client, "/api/cases/case-002/reserve/decisions", reserveBody, fin.Cookie, fin.Csrf)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(h.Client, "/api/cases/case-002/obligations/OBL-1/decisions", legalBody, legal.Cookie, legal.Csrf)).StatusCode);

        Assert.Equal(before, Snapshot(h, "case-001"));
    }

    [Fact]
    public async Task Invalid_and_stale_decisions_do_not_change_case_state()
    {
        await using var h = new Harness().Seed("case-001", Draft("DRAFT-1", NoticeDraftStatus.Current));
        var fin = await SignInAsync(h.Client, "fin-1");
        var legal = await SignInAsync(h.Client, "legal-1");
        var before = Snapshot(h, "case-001");
        var longReason = new string('x', 2001);

        var invalidReserve = new object[]
        {
            new { expectedRevision = 0, decision = "approved", reason = "Reason." },
            new { expectedRevision = 0, decision = (string?)null, reason = "Reason." },
            new { expectedRevision = 0, decision = "approve", reason = (string?)null },
            new { expectedRevision = 0, decision = "approve", reason = "   " },
            new { expectedRevision = 0, decision = "approve", reason = longReason },
        };
        foreach (var body in invalidReserve)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(h.Client, ReservePath, body, fin.Cookie, fin.Csrf)).StatusCode);
            Assert.Equal(before, Snapshot(h, "case-001"));
        }

        var invalidLegal = new object[]
        {
            new { expectedRevision = 0, decision = "approve", reason = "Reason." },
            new { expectedRevision = 0, decision = (string?)null, reason = "Reason." },
            new { expectedRevision = 0, decision = "signed-off", reason = (string?)null },
            new { expectedRevision = 0, decision = "signed-off", reason = "   " },
            new { expectedRevision = 0, decision = "signed-off", reason = longReason },
        };
        foreach (var body in invalidLegal)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(h.Client, LegalPath, body, legal.Cookie, legal.Csrf)).StatusCode);
            Assert.Equal(before, Snapshot(h, "case-001"));
        }

        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(h.Client, ReservePath, new { expectedRevision = 5, decision = "approve", reason = "Reason." }, fin.Cookie, fin.Csrf)).StatusCode);
        Assert.Equal(before, Snapshot(h, "case-001"));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(h.Client, LegalPath, new { expectedRevision = 5, decision = "signed-off", reason = "Reason." }, legal.Cookie, legal.Csrf)).StatusCode);
        Assert.Equal(before, Snapshot(h, "case-001"));
    }

    [Fact]
    public async Task Decisions_are_simulated_and_cannot_deliver()
    {
        await using var h = new Harness().Seed("case-001", Draft("DRAFT-1", NoticeDraftStatus.Current));
        var fin = await SignInAsync(h.Client, "fin-1");
        var legal = await SignInAsync(h.Client, "legal-1");

        var legalResponse = await PostAsync(h.Client, LegalPath, new { expectedRevision = 0, decision = "signed-off", reason = "Notice approved." }, legal.Cookie, legal.Csrf);
        Assert.Equal(HttpStatusCode.OK, legalResponse.StatusCode);
        using (var json = JsonDocument.Parse(await legalResponse.Content.ReadAsStringAsync()))
        {
            Assert.True(json.RootElement.GetProperty("simulated").GetBoolean());
            Assert.False(json.RootElement.GetProperty("noticeSent").GetBoolean());
        }

        var reserveResponse = await PostAsync(h.Client, ReservePath, new { expectedRevision = 1, decision = "approve", reason = "Reserve ok." }, fin.Cookie, fin.Csrf);
        Assert.Equal(HttpStatusCode.OK, reserveResponse.StatusCode);
        using (var json = JsonDocument.Parse(await reserveResponse.Content.ReadAsStringAsync()))
        {
            Assert.True(json.RootElement.GetProperty("simulated").GetBoolean());
            Assert.False(json.RootElement.GetProperty("invoiceIssued").GetBoolean());
        }

        // A superseded-only draft and a missing draft both make the legal route unavailable.
        h.Seed("case-001", Draft("DRAFT-OLD", NoticeDraftStatus.Superseded));
        var before = Snapshot(h, "case-001");
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(h.Client, LegalPath, new { expectedRevision = 2, decision = "signed-off", reason = "Notice approved." }, legal.Cookie, legal.Csrf)).StatusCode);
        Assert.Equal(before, Snapshot(h, "case-001"));

        h.Seed("case-001");
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(h.Client, LegalPath, new { expectedRevision = 2, decision = "signed-off", reason = "Notice approved." }, legal.Cookie, legal.Csrf)).StatusCode);
        Assert.Equal(before, Snapshot(h, "case-001"));

        Assert.Equal(0, h.Sender.Calls);
    }
}
