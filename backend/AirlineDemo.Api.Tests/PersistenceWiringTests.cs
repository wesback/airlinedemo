using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class PersistenceWiringTests
{
    private const string Passcode = "test-passcode";

    private sealed record Session(string Cookie, string Csrf);

    private static WebApplicationFactory<Program> CreateFactory(string caseStatePath, params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?> { ["CaseState:Path"] = caseStatePath };
                foreach (var (key, value) in settings)
                {
                    values[key] = value;
                }
                config.AddInMemoryCollection(values);
            }));

    private static string NewCaseStatePath() =>
        Path.Combine(Path.GetTempPath(), $"persistence-wiring-{Guid.NewGuid():N}", "cases.json");

    private static async Task<Session> SignInAsync(HttpClient client, string userId, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId, passcode = Passcode }, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new Session(cookie, body.GetProperty("csrfToken").GetString()!);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, Session session, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", session.Cookie);
        request.Headers.Add("X-CSRF-Token", session.Csrf);
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request, ct);
    }

    // Endpoint-level restart: the review is recorded through POST /api/cases/{id}/reviews on one host,
    // and the decision, revision and audit event are read back through the endpoints and store of a new host.
    [Fact]
    public async Task Endpoint_decisions_and_audit_events_survive_host_restart_with_the_same_file_store()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = NewCaseStatePath();
        using (var first = CreateFactory(path, ("Demo:Passcode", Passcode)))
        using (var client = first.CreateClient())
        {
            var session = await SignInAsync(client, "reviewer", ct);
            using var review = await SendAsync(client, HttpMethod.Post, "/api/cases/case-001/reviews",
                session, new { expectedRevision = 1, decision = "accept" }, ct);
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        }

        using var second = CreateFactory(path, ("Demo:Passcode", Passcode));
        using var secondClient = second.CreateClient();
        var restored = await SignInAsync(secondClient, "reviewer", ct);
        using var read = await SendAsync(secondClient, HttpMethod.Get, "/api/cases/case-001", restored, null, ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var body = await read.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(2, body.GetProperty("revision").GetInt32());
        Assert.Equal("accepted", body.GetProperty("status").GetString());

        // The reviewer's scope is case-001 only; case-002 must be denied with no data, even after restart.
        using var outOfScope = await SendAsync(secondClient, HttpMethod.Get, "/api/cases/case-002", restored, null, ct);
        Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);
        Assert.DoesNotContain("case-002", await outOfScope.Content.ReadAsStringAsync(ct));

        var store = second.Services.GetRequiredService<ICaseStateStore>();
        var audit = store.ReadAudit(store.AuthorizeCase("case-001", CaseStore.SeedOwner));
        Assert.Contains(audit, e => e.Action == CaseStore.AcceptAction);
    }

    [Fact]
    public void Sql_case_provider_selects_the_sql_store()
    {
        using var factory = CreateFactory(NewCaseStatePath(),
            ("Persistence:CaseStateProvider", "Sql"),
            ("Persistence:CaseStateConnectionString", "Server=localhost;Database=cases;Trusted_Connection=True;"));

        Assert.IsType<SqlCaseStateStore>(factory.Services.GetRequiredService<ICaseStateStore>());
    }

    [Fact]
    public void Sql_case_provider_without_connection_string_fails_startup()
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            using var factory = CreateFactory(NewCaseStatePath(), ("Persistence:CaseStateProvider", "Sql"));
            _ = factory.Services.GetRequiredService<ICaseStateStore>();
        });

        Assert.Contains("Persistence:CaseStateConnectionString is required", ex.ToString());
    }

    [Fact]
    public void Case_state_survives_host_restart_with_the_same_file_store()
    {
        var path = NewCaseStatePath();
        using (var first = CreateFactory(path))
        {
            first.Services.GetRequiredService<ICaseStateStore>().CreateCase("case-restart", "presenter");
        }

        using var second = CreateFactory(path);
        Assert.Equal("presenter", second.Services.GetRequiredService<ICaseStateStore>().OwnerOf("case-restart"));
    }

    [Fact]
    public void Defaults_are_file_for_case_state_and_memory_for_evidence()
    {
        using var factory = CreateFactory(NewCaseStatePath());

        Assert.IsType<CaseStateStore>(factory.Services.GetRequiredService<ICaseStateStore>());
        Assert.IsType<InMemoryEvidenceBlobStore>(factory.Services.GetRequiredService<IEvidenceBlobStore>());
    }

    [Fact]
    public void Unknown_case_provider_fails_startup_with_a_clear_message()
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            using var factory = CreateFactory(NewCaseStatePath(), ("Persistence:CaseStateProvider", "Bogus"));
            _ = factory.Services.GetRequiredService<ICaseStateStore>();
        });

        Assert.Contains("Persistence:CaseStateProvider 'Bogus' is not supported", ex.ToString());
    }

    [Fact]
    public void Unknown_evidence_provider_fails_startup_with_a_clear_message()
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            using var factory = CreateFactory(NewCaseStatePath(), ("Persistence:EvidenceProvider", "Bogus"));
            _ = factory.Services.GetRequiredService<IEvidenceBlobStore>();
        });

        Assert.Contains("Persistence:EvidenceProvider 'Bogus' is not supported", ex.ToString());
    }
}
