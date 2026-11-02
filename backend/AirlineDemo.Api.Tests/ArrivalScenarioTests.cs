using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.SourceSystems;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirlineDemo.Api.Tests;

public sealed class ArrivalScenarioTests : IDisposable
{
    private static readonly string[] Sources = ["technical", "maintenance", "finance", "records", "contracts"];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arrival-scenario-" + Guid.NewGuid().ToString("N"));
    private string CasePath => Path.Combine(_directory, "cases.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task FixturesSupportArrivalToHandoffWithoutExternalCalls()
    {
        var model = new CountingChatClient();
        var network = new CountingNetworkHandler();
        using var factory = CreateFactory(model, network);
        using var client = factory.CreateClient();
        var presenter = await SignIn(client, "presenter");

        using (var summary = await GetJson(client, presenter, "/api/v1/cases/case-001/sources"))
        {
            var sourceRows = summary.RootElement.GetProperty("sources").EnumerateArray().ToArray();
            Assert.Equal(Sources, sourceRows.Select(row => row.GetProperty("sourceId").GetString()).ToArray());
            Assert.All(sourceRows, row =>
            {
                Assert.Equal("Simulated", row.GetProperty("capabilityLabel").GetString());
                Assert.True(row.GetProperty("coverage").ValueKind is JsonValueKind.True or JsonValueKind.False);
                Assert.False(row.TryGetProperty("recordCount", out _));
            });
        }

        using var initialStatus = await GetJson(client, presenter, "/api/v1/cases/case-001/arrival");
        Assert.Equal("not_started", initialStatus.RootElement.GetProperty("state").GetString());
        Assert.Equal(Sources, ReadStrings(initialStatus.RootElement.GetProperty("missingSources")));
        using (var start = await PostJson(client, presenter, "/api/v1/cases/case-001/arrival/start",
            new { commandId = "scenario-start", expectedRevision = initialStatus.RootElement.GetProperty("caseRevision").GetInt64() }))
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        using (var blocked = await PostJson(client, presenter, "/api/v1/cases/case-001/arrival/complete",
            new { commandId = "scenario-blocked-complete", expectedRevision = await CurrentRevision(client, presenter) }))
        {
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            using var body = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("arrival_sources_missing", body.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(Sources, ReadStrings(body.RootElement.GetProperty("error").GetProperty("missingSources")));
        }

        using (var denied = await Retrieve(client, presenter, "technical", "scenario-denied",
            await CurrentRevision(client, presenter)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            var body = await denied.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("\"code\":\"forbidden\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("recordId", body, StringComparison.Ordinal);
        }
        await AssertAuditAsync(client, presenter, "technical", "presenter", "denied", expectedRefs: []);

        var unavailableRevision = await CurrentRevision(client, presenter);
        using (var mode = await PostJson(client, presenter, "/api/v1/cases/case-001/sources/maintenance/mode",
            new { commandId = "scenario-maintenance-down", expectedRevision = unavailableRevision, mode = "unavailable" }))
            Assert.Equal(HttpStatusCode.OK, mode.StatusCode);
        using (var unavailableSummary = await GetJson(client, presenter, "/api/v1/cases/case-001/sources"))
        {
            var maintenance = Assert.Single(unavailableSummary.RootElement.GetProperty("sources").EnumerateArray()
                .Where(row => row.GetProperty("sourceId").GetString() == "maintenance"));
            Assert.Equal("unavailable", maintenance.GetProperty("mode").GetString());
            Assert.False(maintenance.GetProperty("coverage").GetBoolean());
        }

        var technical = await SwitchUser(client, presenter, "reviewer");
        using (var unavailableRead = await Retrieve(client, technical, "maintenance", "scenario-maintenance-unavailable",
            await CurrentRevision(client, technical)))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailableRead.StatusCode);
            using var body = JsonDocument.Parse(await unavailableRead.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("source_unavailable", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        await AssertAuditAsync(client, technical, "maintenance", "technical-reviewer", "unavailable", expectedRefs: []);

        presenter = await SwitchUser(client, technical, "presenter");
        using (var restored = await PostJson(client, presenter, "/api/v1/cases/case-001/sources/maintenance/mode",
            new
            {
                commandId = "scenario-maintenance-restored",
                expectedRevision = await CurrentRevision(client, presenter),
                mode = "available",
            }))
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        technical = await SwitchUser(client, presenter, "reviewer");

        foreach (var sourceId in new[] { "technical", "maintenance", "records" })
            await RetrieveAndAssertAudit(client, technical, sourceId, "technical-reviewer");

        var finance = await SwitchUser(client, technical, "fin-1");
        using (var financeSummary = await GetJson(client, finance, "/api/v1/cases/case-001/sources"))
        {
            var financeSource = Assert.Single(financeSummary.RootElement.GetProperty("sources").EnumerateArray()
                .Where(row => row.GetProperty("sourceId").GetString() == "finance"));
            Assert.True(financeSource.GetProperty("recordCount").GetInt32() > 0);
        }
        await RetrieveAndAssertAudit(client, finance, "finance", "finance-reviewer");

        var legal = await SwitchUser(client, finance, "legal-1");
        using (var legalSummary = await GetJson(client, legal, "/api/v1/cases/case-001/sources"))
        {
            var contracts = Assert.Single(legalSummary.RootElement.GetProperty("sources").EnumerateArray()
                .Where(row => row.GetProperty("sourceId").GetString() == "contracts"));
            Assert.True(contracts.GetProperty("recordCount").GetInt32() > 0);
        }
        await RetrieveAndAssertAudit(client, legal, "contracts", "legal-reviewer");

        presenter = await SwitchUser(client, legal, "presenter");
        using (var readyStatus = await GetJson(client, presenter, "/api/v1/cases/case-001/arrival"))
        {
            Assert.Equal(Array.Empty<string>(), ReadStrings(readyStatus.RootElement.GetProperty("missingSources")));
            Assert.True(readyStatus.RootElement.GetProperty("canComplete").GetBoolean());
            using var completion = await PostJson(client, presenter, "/api/v1/cases/case-001/arrival/complete",
                new
                {
                    commandId = "scenario-complete",
                    expectedRevision = readyStatus.RootElement.GetProperty("caseRevision").GetInt64(),
                });
            Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        }

        using (var completed = await GetJson(client, presenter, "/api/v1/cases/case-001/arrival"))
            Assert.Equal("ready", completed.RootElement.GetProperty("state").GetString());

        var store = factory.Services.GetRequiredService<ICaseStateStore>();
        var state = store.Read(store.AuthorizeCase("case-001", "presenter"));
        Assert.Equal(Sources.Length, state.Basis.Count);
        using (var pins = JsonDocument.Parse(state.Basis[^1].Content))
        {
            var pinnedSources = pins.RootElement.GetProperty("pins").EnumerateArray()
                .Select(pin => pin.GetProperty("sourceId").GetString()).Distinct(StringComparer.Ordinal).ToArray();
            Assert.Equal(Sources.Order(StringComparer.Ordinal), pinnedSources.Order(StringComparer.Ordinal));
        }

        using (var projectedCase = await GetJson(client, presenter, "/api/cases/case-001"))
        {
            var readEvents = projectedCase.RootElement.GetProperty("audit").EnumerateArray()
                .Where(item => item.GetProperty("action").GetString() == ArrivalJourneyService.SourceReadAction)
                .ToArray();
            Assert.All(readEvents, item => Assert.False(item.GetProperty("detail").TryGetProperty("recordRefs", out _)));
            var serialized = projectedCase.RootElement.GetRawText();
            Assert.DoesNotContain("technical-record", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("maintenance-record", serialized, StringComparison.Ordinal);
        }

        Assert.Equal(0, model.Calls);
        Assert.Equal(0, network.Calls);
    }

    private WebApplicationFactory<Program> CreateFactory(CountingChatClient model, CountingNetworkHandler network)
    {
        Directory.CreateDirectory(_directory);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Demo:ArrivalJourney"] = "true",
                    ["Demo:Passcode"] = "scenario-passcode",
                    ["Persistence:CaseStateProvider"] = "File",
                    ["CaseState:Path"] = CasePath,
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatClient>();
                services.AddSingleton<IChatClient>(model);
                services.AddSingleton<HttpMessageHandler>(network);
            });
        });
    }

    private static async Task<ScenarioSession> SignIn(HttpClient client, string userId)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/sign-in",
            new { userId, passcode = "scenario-passcode" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadSession(response);
    }

    private static async Task<ScenarioSession> SwitchUser(HttpClient client, ScenarioSession current, string userId)
    {
        using var request = Request(HttpMethod.Post, "/api/auth/switch-user", current, new { userId });
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadSession(response);
    }

    private static async Task<ScenarioSession> ReadSession(HttpResponseMessage response)
    {
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(DemoAuthEndpoints.CookieName + "=", StringComparison.Ordinal))
            .Split(';', 2)[0];
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return new ScenarioSession(body.RootElement.GetProperty("id").GetString()!,
            body.RootElement.GetProperty("role").GetString()!,
            cookie,
            body.RootElement.GetProperty("csrfToken").GetString()!);
    }

    private static async Task<JsonDocument> GetJson(HttpClient client, ScenarioSession session, string path)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Get, path, session), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static Task<HttpResponseMessage> PostJson(HttpClient client, ScenarioSession session, string path, object body) =>
        client.SendAsync(Request(HttpMethod.Post, path, session, body), TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> Retrieve(HttpClient client, ScenarioSession session, string sourceId,
        string commandId, long revision) =>
        PostJson(client, session, $"/api/v1/cases/case-001/arrival/sources/{sourceId}/retrieve",
            new { commandId, expectedRevision = revision });

    private static HttpRequestMessage Request(HttpMethod method, string path, ScenarioSession session, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={session.Cookie.Split('=', 2)[1]}");
        request.Headers.Add(DemoAuthEndpoints.CsrfHeaderName, session.CsrfToken);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<long> CurrentRevision(HttpClient client, ScenarioSession session)
    {
        using var status = await GetJson(client, session, "/api/v1/cases/case-001/arrival");
        return status.RootElement.GetProperty("caseRevision").GetInt64();
    }

    private static async Task RetrieveAndAssertAudit(HttpClient client, ScenarioSession session, string sourceId, string role)
    {
        using var response = await Retrieve(client, session, sourceId, "scenario-retrieve-" + sourceId,
            await CurrentRevision(client, session));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("pinned", receipt.RootElement.GetProperty("outcome").GetString());
        Assert.DoesNotContain("payload", receipt.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var refs = receipt.RootElement.GetProperty("recordRefs").EnumerateArray().ToArray();
        Assert.NotEmpty(refs);
        Assert.All(refs, reference =>
        {
            Assert.Equal(sourceId, reference.GetProperty("sourceId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(reference.GetProperty("recordId").GetString()));
            Assert.True(reference.GetProperty("recordVersion").GetInt32() > 0);
        });
        await AssertAuditAsync(client, session, sourceId, role, "pinned", refs);
    }

    private static async Task AssertAuditAsync(HttpClient client, ScenarioSession session, string sourceId,
        string role, string outcome, JsonElement[] expectedRefs)
    {
        using var response = await GetJson(client, session, "/api/cases/case-001");
        var audit = Assert.Single(response.RootElement.GetProperty("audit").EnumerateArray().Where(item =>
        {
            if (item.GetProperty("action").GetString() != ArrivalJourneyService.SourceReadAction) return false;
            var detail = item.GetProperty("detail");
            return detail.TryGetProperty("sourceId", out var detailSource)
                && detailSource.GetString() == sourceId
                && detail.TryGetProperty("role", out var detailRole)
                && detailRole.GetString() == role
                && detail.TryGetProperty("outcome", out var detailOutcome)
                && detailOutcome.GetString() == outcome;
        }));
        Assert.True(DateTimeOffset.TryParse(audit.GetProperty("at").GetString(), out _));
        var detail = audit.GetProperty("detail");
        if (expectedRefs.Length == 0)
        {
            Assert.False(detail.TryGetProperty("recordRefs", out _));
            return;
        }

        var actualRefs = detail.GetProperty("recordRefs").EnumerateArray().ToArray();
        Assert.Equal(expectedRefs.Select(RefKey).Order(StringComparer.Ordinal),
            actualRefs.Select(RefKey).Order(StringComparer.Ordinal));
    }

    private static string RefKey(JsonElement reference) =>
        $"{reference.GetProperty("sourceId").GetString()}|{reference.GetProperty("recordId").GetString()}|{reference.GetProperty("recordVersion").GetInt32()}";

    private static string[] ReadStrings(JsonElement array) => array.EnumerateArray()
        .Select(item => item.GetString()!).ToArray();

    private sealed record ScenarioSession(string UserId, string Role, string Cookie, string CsrfToken);

    private sealed class CountingNetworkHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("The arrival scenario must not call an external HTTP service.");
        }
    }
}
