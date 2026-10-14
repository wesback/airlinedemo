using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AirlineDemo.Api.Auth;

namespace AirlineDemo.Api.Tests;

public sealed class PasscodeStepTests
{
    private const string Passcode = "test-passcode";
    private const string PasscodeCookie = "__Host-demo-passcode";

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static (WebApplicationFactory<Program> Factory, ManualClock Clock) CreateFactory()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Passcode"] = Passcode }));
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock));
        });
        return (factory, clock);
    }

    private static Task<HttpResponseMessage> PasscodeAsync(HttpClient client, string? passcode, CancellationToken ct) =>
        client.PostAsJsonAsync("/api/auth/passcode", new { passcode }, ct);

    private static async Task<string> PasscodeCookieAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await PasscodeAsync(client, Passcode, ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    private static Task<HttpResponseMessage> ListUsersAsync(HttpClient client, string? cookie, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/users");
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request, ct);
    }

    [Fact]
    public async Task Right_passcode_returns_204_and_host_prefixed_short_lived_strict_cookie()
    {
        var (factory, _) = CreateFactory();
        using var _f = factory;
        using var client = factory.CreateClient();
        using var response = await PasscodeAsync(client, Passcode, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        Assert.StartsWith("__host-demo-passcode=", setCookie);
        Assert.Contains("httponly", setCookie);
        Assert.Contains("secure", setCookie);
        Assert.Contains("samesite=strict", setCookie);
        Assert.Contains("max-age=300", setCookie);
        Assert.DoesNotContain(Passcode, setCookie);
    }

    [Fact]
    public async Task Wrong_passcode_returns_generic_401()
    {
        var (factory, _) = CreateFactory();
        using var _f = factory;
        using var client = factory.CreateClient();
        using var response = await PasscodeAsync(client, "wrong", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(response.Headers.Where(h => h.Key == "Set-Cookie"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Sign-in failed.", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Wrong_passcodes_lock_source_for_passcode_step_and_sign_in()
    {
        var (factory, _) = CreateFactory();
        using var _f = factory;
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < PasscodeThrottle.MaxFailures; i++)
        {
            using var wrong = await PasscodeAsync(client, "wrong", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var locked = await PasscodeAsync(client, Passcode, ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);

        using var signIn = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId = "reviewer", passcode = Passcode }, ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, signIn.StatusCode);
    }

    [Fact]
    public async Task User_list_requires_passcode_cookie()
    {
        var (factory, _) = CreateFactory();
        using var _f = factory;
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var anonymous = await ListUsersAsync(client, null, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var cookie = await PasscodeCookieAsync(client, ct);
        using var granted = await ListUsersAsync(client, cookie, ct);
        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        var users = await granted.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(JsonValueKind.Array, users.ValueKind);
        Assert.NotEmpty(users.EnumerateArray());
    }

    [Fact]
    public async Task Expired_passcode_cookie_is_rejected()
    {
        var (factory, clock) = CreateFactory();
        using var _f = factory;
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var cookie = await PasscodeCookieAsync(client, ct);
        clock.Advance(TimeSpan.FromMinutes(5));

        using var response = await ListUsersAsync(client, cookie, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Forged_passcode_cookie_is_rejected()
    {
        var (factory, _) = CreateFactory();
        using var _f = factory;
        using var client = factory.CreateClient();

        using var response = await ListUsersAsync(client, $"{PasscodeCookie}=forged-token", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sign_in_still_checks_passcode_and_clears_passcode_grant()
    {
        var (factory, _) = CreateFactory();
        using var _f = factory;
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var grant = await PasscodeCookieAsync(client, ct);

        using var badSignIn = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId = "reviewer", passcode = "wrong" }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, badSignIn.StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/sign-in")
        {
            Content = JsonContent.Create(new { userId = "reviewer", passcode = Passcode }),
        };
        request.Headers.Add("Cookie", grant);
        using var signIn = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        var cleared = signIn.Headers.GetValues("Set-Cookie").Any(v => v.StartsWith($"{PasscodeCookie}=", StringComparison.Ordinal) && v.Contains("expires=", StringComparison.OrdinalIgnoreCase));
        Assert.True(cleared, "sign-in must clear the passcode cookie");

        var sessionCookie = signIn.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("__Host-demo-session=", StringComparison.Ordinal)).Split(';')[0];
        var body = await signIn.Content.ReadFromJsonAsync<JsonElement>(ct);
        var csrf = body.GetProperty("csrfToken").GetString()!;
        var signOut = new HttpRequestMessage(HttpMethod.Post, "/api/auth/sign-out");
        signOut.Headers.Add("Cookie", sessionCookie);
        signOut.Headers.Add("X-CSRF-Token", csrf);
        using (var out_ = await client.SendAsync(signOut, ct)) Assert.Equal(HttpStatusCode.NoContent, out_.StatusCode);

        using var reused = await ListUsersAsync(client, grant, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
    }

    [Fact]
    public async Task Valid_session_allows_user_list_without_passcode_cookie()
    {
        var (factory, _) = CreateFactory();
        using var _f = factory;
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var signIn = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId = "reviewer", passcode = Passcode }, ct);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        var sessionCookie = signIn.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("__Host-demo-session=", StringComparison.Ordinal)).Split(';')[0];

        using var response = await ListUsersAsync(client, sessionCookie, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
