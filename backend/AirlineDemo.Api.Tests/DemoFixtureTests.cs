using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AirlineDemo.Api.Tests;

public sealed class DemoFixtureTests
{
    private const string Passcode = "test-passcode";

    private static readonly DemoUser[] ExpectedUsers =
    [
        new("presenter", "Demo presenter", "presenter", ["case-001", "case-002"]),
        new("reviewer", "Technical reviewer", "technical-reviewer", ["case-001"]),
        new("fin-1", "Finance reviewer", "finance-reviewer", ["case-001"]),
        new("legal-1", "Legal reviewer", "legal-reviewer", ["case-001"]),
    ];

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Passcode"] = Passcode })));

    private static MemoryStream Utf8(string json) => new(Encoding.UTF8.GetBytes(json));

    [Fact]
    public async Task VersionOneFixtureSuppliesPickerAndSessionUsers()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var signIn = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId = "presenter", passcode = Passcode }, ct);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        var cookie = signIn.Headers.GetValues("Set-Cookie").First(c => c.StartsWith(DemoAuthEndpoints.CookieName + "=", StringComparison.Ordinal)).Split(';')[0];

        using var users = new HttpRequestMessage(HttpMethod.Get, "/api/auth/users");
        users.Headers.Add("Cookie", cookie);
        using var pickerResponse = await client.SendAsync(users, ct);
        Assert.Equal(HttpStatusCode.OK, pickerResponse.StatusCode);
        using var pickerJson = JsonDocument.Parse(await pickerResponse.Content.ReadAsStringAsync(ct));
        var picker = pickerJson.RootElement.EnumerateArray().Select(e => (
            e.GetProperty("id").GetString()!, e.GetProperty("displayName").GetString()!, e.GetProperty("role").GetString()!)).ToArray();
        Assert.Equal(ExpectedUsers.Select(u => (u.Id, u.DisplayName, u.Role)), picker);

        foreach (var expected in ExpectedUsers)
        {
            // Each user signs in from its own client, because a client already holding a session gets 409.
            using var perUser = factory.CreateClient();
            using var response = await perUser.PostAsJsonAsync("/api/auth/sign-in", new { userId = expected.Id, passcode = Passcode }, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            Assert.Equal(expected.Id, root.GetProperty("id").GetString());
            Assert.Equal(expected.DisplayName, root.GetProperty("displayName").GetString());
            Assert.Equal(expected.Role, root.GetProperty("role").GetString());
            Assert.Equal(expected.CaseScopes, root.GetProperty("caseScopes").EnumerateArray().Select(e => e.GetString()!).ToArray());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("csrfToken").GetString()));
        }
    }

    [Fact]
    public void InvalidRosterFixturesFailWithoutFallback()
    {
        const string validUser = """{"id":"a","displayName":"A","role":"r","caseScopes":["case-001"]}""";
        var invalid = new (string Name, string Json)[]
        {
            ("malformed JSON", "{ not json"),
            ("unsupported schema version", $$"""{"schemaVersion":2,"users":[{{validUser}}]}"""),
            ("missing schema version", $$"""{"users":[{{validUser}}]}"""),
            ("missing user list", """{"schemaVersion":1}"""),
            ("empty user list", """{"schemaVersion":1,"users":[]}"""),
            ("duplicate IDs", $$"""{"schemaVersion":1,"users":[{{validUser}},{{validUser}}]}"""),
            ("blank ID", """{"schemaVersion":1,"users":[{"id":" ","displayName":"A","role":"r","caseScopes":["case-001"]}]}"""),
            ("blank display name", """{"schemaVersion":1,"users":[{"id":"a","displayName":"","role":"r","caseScopes":["case-001"]}]}"""),
            ("blank role", """{"schemaVersion":1,"users":[{"id":"a","displayName":"A","role":"  ","caseScopes":["case-001"]}]}"""),
            ("empty case-scope list", """{"schemaVersion":1,"users":[{"id":"a","displayName":"A","role":"r","caseScopes":[]}]}"""),
            ("blank case-scope entry", """{"schemaVersion":1,"users":[{"id":"a","displayName":"A","role":"r","caseScopes":["case-001",""]}]}"""),
            ("null user entry", """{"schemaVersion":1,"users":[null]}"""),
        };

        foreach (var (name, json) in invalid)
        {
            using var stream = Utf8(json);
            var ex = Assert.Throws<InvalidDataException>(() => DemoFixture.Load(stream));
            Assert.StartsWith("Invalid demo user fixture", ex.Message);
        }
    }

    [Fact]
    public void StartupFixtureLoadingRejectsMissingOrInvalidFixture()
    {
        Assert.Throws<InvalidDataException>(() => DemoFixture.Initialize(null));
        using var invalid = Utf8("""{"schemaVersion":1,"users":[]}""");
        Assert.Throws<InvalidDataException>(() => DemoFixture.Initialize(invalid));
    }
}
