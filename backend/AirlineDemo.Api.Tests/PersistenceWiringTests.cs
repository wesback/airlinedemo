using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AirlineDemo.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class PersistenceWiringTests
{
    private const string Passcode = "test-passcode";
    private const string SqlConnection =
        "Server=tcp:case-state.database.windows.net,1433;Database=casestate;Encrypt=True;Authentication=Active Directory Managed Identity;";

    private sealed record Session(string Cookie, string Csrf);

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static WebApplicationFactory<Program> CreateFactory(string file) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Demo:Passcode"] = Passcode,
                    ["CaseState:Path"] = file,
                })));

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

    [Fact]
    public void Provider_selection_defaults_and_invalid_values()
    {
        var file = CaseSeed.CreateFile();

        var defaults = new ServiceCollection()
            .AddCaseStateStore(Config(("CaseState:Path", file)))
            .AddEvidenceBlobStore(Config())
            .BuildServiceProvider();
        Assert.IsType<CaseStateStore>(defaults.GetRequiredService<ICaseStateStore>());
        Assert.Equal("InMemoryEvidenceBlobStore", defaults.GetRequiredService<IEvidenceBlobStore>().GetType().Name);

        var file1 = new ServiceCollection()
            .AddCaseStateStore(Config(("Persistence:CaseStateProvider", "File"), ("CaseState:Path", file)))
            .AddEvidenceBlobStore(Config(("Persistence:EvidenceProvider", "Memory")))
            .BuildServiceProvider();
        Assert.IsType<CaseStateStore>(file1.GetRequiredService<ICaseStateStore>());

        var sql = new ServiceCollection()
            .AddCaseStateStore(Config(("Persistence:CaseStateProvider", "Sql"), ("ConnectionStrings:CaseState", SqlConnection)))
            .BuildServiceProvider();
        Assert.Equal("SqlCaseStateStore", sql.GetRequiredService<ICaseStateStore>().GetType().Name);

        var blob = new ServiceCollection()
            .AddEvidenceBlobStore(Config(
                ("Persistence:EvidenceProvider", "Blob"),
                ("Persistence:EvidenceBlobContainerUri", "https://account.blob.core.windows.net/evidence")))
            .BuildServiceProvider();
        Assert.Equal("AzureEvidenceBlobStore", blob.GetRequiredService<IEvidenceBlobStore>().GetType().Name);

        var unknownCase = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddCaseStateStore(Config(("Persistence:CaseStateProvider", "Cosmos"))));
        Assert.Contains("Persistence:CaseStateProvider", unknownCase.Message);
        Assert.Contains("Cosmos", unknownCase.Message);

        var unknownEvidence = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddEvidenceBlobStore(Config(("Persistence:EvidenceProvider", "Disk"))));
        Assert.Contains("Persistence:EvidenceProvider", unknownEvidence.Message);
        Assert.Contains("Disk", unknownEvidence.Message);

        Assert.ThrowsAny<Exception>(() => new ServiceCollection().AddCaseStateStore(Config(
            ("Persistence:CaseStateProvider", "Sql"),
            ("ConnectionStrings:CaseState", SqlConnection + "User ID=app;Password=secret;"))));
    }

    [Fact]
    public void Program_registers_both_providers_and_endpoints_use_the_case_state_store()
    {
        var program = File.ReadAllText(SourcePath("Program.cs"));
        Assert.Contains("AddCaseStateStore(builder.Configuration)", program);
        Assert.Contains("AddEvidenceBlobStore(builder.Configuration)", program);

        var endpoints = File.ReadAllText(SourcePath("Auth", "CaseEndpoints.cs"));
        Assert.Contains("ICaseStateStore cases", endpoints);
        Assert.Contains("cases.Read(scope)", endpoints);
        Assert.Contains("Decide(cases,", endpoints);
        Assert.DoesNotContain("new CaseStore", endpoints);
    }

    [Fact]
    public async Task Case_endpoints_preserve_state_across_host_restart()
    {
        var file = CaseSeed.CreateFile();
        var ct = TestContext.Current.CancellationToken;

        using (var first = CreateFactory(file))
        {
            using var client = first.CreateClient();
            var reviewer = await SignInAsync(client, "reviewer", ct);
            using var read = await SendAsync(client, HttpMethod.Get, "/api/cases/case-001", reviewer, false, null, ct);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal(1, (await read.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("revision").GetInt32());

            using var review = await SendAsync(client, HttpMethod.Post, "/api/cases/case-001/reviews", reviewer, true,
                new { expectedRevision = 1, expectedBasisVersion = 1, decision = "accept", reason = "basis reviewed" }, ct);
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
            Assert.Contains("accept", await review.Content.ReadAsStringAsync(ct));
        }

        using (var second = CreateFactory(file))
        {
            using var client = second.CreateClient();
            var reviewer = await SignInAsync(client, "reviewer", ct);
            using var read = await SendAsync(client, HttpMethod.Get, "/api/cases/case-001", reviewer, false, null, ct);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal(2, (await read.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("revision").GetInt32());
        }

        var store = new CaseStateStore(file);
        var scope = store.AuthorizeCase("case-001", "presenter");
        Assert.Equal(2L, store.Read(scope).Revision);
        var reviewAudit = Assert.Single(store.ReadAudit(scope), e => e.Revision == 2);
        Assert.Equal("reviewer", reviewAudit.Actor);
        Assert.Contains("\"accept\"", reviewAudit.Detail);
    }

    [Fact]
    public async Task Out_of_scope_case_request_returns_no_case_data()
    {
        using var factory = CreateFactory(CaseSeed.CreateFile());
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var unauthenticated = await client.GetAsync("/api/cases/case-001", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        var presenter = await SignInAsync(client, "presenter", ct);
        using var outOfScope = await SendAsync(client, HttpMethod.Get, "/api/cases/case-003", presenter, false, null, ct);
        Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);
        Assert.Equal(string.Empty, await outOfScope.Content.ReadAsStringAsync(ct));
    }

    private static string SourcePath(params string[] segments)
    {
        var directory = Path.GetDirectoryName(SourceFile())!;
        return Path.Combine(new[] { directory, "..", "AirlineDemo.Api" }.Concat(segments).ToArray());
    }

    private static string SourceFile([CallerFilePath] string path = "") => path;
}
