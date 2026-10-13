using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AirlineDemo.Api.Tests;

public sealed class AuthBoundaryTests
{
    private const string Passcode = "test-passcode";

    private sealed record Session(string Cookie, string Csrf);

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Passcode"] = Passcode,
                ["CaseState:Path"] = TechnicalReviewTests.SeedAcceptedDecision(),
            })));

    private static Task<HttpResponseMessage> SignInRawAsync(HttpClient client, string? userId, string? passcode, CancellationToken ct) =>
        client.PostAsJsonAsync("/api/auth/sign-in", new { userId, passcode }, ct);

    private static async Task<Session> SignInAsync(HttpClient client, string userId, CancellationToken ct)
    {
        using var response = await SignInRawAsync(client, userId, Passcode, ct);
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

    private static async Task<int> RevisionAsync(HttpClient client, Session reader, string caseId, CancellationToken ct)
    {
        using var response = await SendAsync(client, HttpMethod.Get, $"/api/cases/{caseId}", reader, false, null, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("revision").GetInt32();
    }

    [Fact]
    public async Task Successful_sign_in_issues_host_prefixed_strict_http_only_secure_cookie()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var response = await SignInRawAsync(client, "reviewer", Passcode, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        Assert.StartsWith("__host-demo-session=", setCookie);
        Assert.Contains("httponly", setCookie);
        Assert.Contains("secure", setCookie);
        Assert.Contains("samesite=strict", setCookie);
    }

    [Fact]
    public async Task Wrong_passcode_or_unknown_user_gets_generic_failure_without_cookie()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        using var wrongPasscode = await SignInRawAsync(client, "reviewer", "nope", ct);
        using var unknownUser = await SignInRawAsync(client, "intruder", Passcode, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPasscode.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.False(wrongPasscode.Headers.Contains("Set-Cookie"));
        Assert.False(unknownUser.Headers.Contains("Set-Cookie"));
        Assert.Equal(await wrongPasscode.Content.ReadAsStringAsync(ct), await unknownUser.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Repeated_wrong_passcodes_lock_out_even_the_correct_passcode()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 5; i++)
        {
            using var wrong = await SignInRawAsync(client, "reviewer", "nope", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var locked = await SignInRawAsync(client, "reviewer", Passcode, ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.False(locked.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Protected_read_without_session_is_denied()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/cases/case-001", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Presenter_cannot_read_case_outside_their_scope()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var presenter = await SignInAsync(client, "presenter", ct);
        using var outOfScope = await SendAsync(client, HttpMethod.Get, "/api/cases/case-003", presenter, false, null, ct);
        using var inScope = await SendAsync(client, HttpMethod.Get, "/api/cases/case-002", presenter, false, null, ct);

        Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);
        Assert.Equal(HttpStatusCode.OK, inScope.StatusCode);
    }

    [Fact]
    public async Task Review_without_anti_forgery_token_is_denied_and_changes_nothing()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var reviewer = await SignInAsync(client, "reviewer", ct);
        using var response = await SendAsync(client, HttpMethod.Post, "/api/cases/case-001/reviews",
            reviewer, false, new { expectedRevision = 1, decision = "accept" }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, await RevisionAsync(client, reviewer, "case-001", ct));
    }

    [Fact]
    public async Task Presenter_review_is_denied_by_role_and_changes_nothing()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var presenter = await SignInAsync(client, "presenter", ct);
        using var response = await SendAsync(client, HttpMethod.Post, "/api/cases/case-001/reviews",
            presenter, true, new { expectedRevision = 1, decision = "accept" }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, await RevisionAsync(client, presenter, "case-001", ct));
    }

    [Fact]
    public async Task Reviewer_review_outside_scope_is_not_found_and_changes_nothing()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var reviewer = await SignInAsync(client, "reviewer", ct);
        var presenter = await SignInAsync(client, "presenter", ct);
        using var response = await SendAsync(client, HttpMethod.Post, "/api/cases/case-002/reviews",
            reviewer, true, new { expectedRevision = 1, decision = "accept" }, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, await RevisionAsync(client, presenter, "case-002", ct));
    }

    [Fact]
    public async Task Stale_revision_review_is_rejected_and_changes_nothing()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var reviewer = await SignInAsync(client, "reviewer", ct);
        using var response = await SendAsync(client, HttpMethod.Post, "/api/cases/case-001/reviews",
            reviewer, true, new { expectedRevision = 0, decision = "accept" }, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, await RevisionAsync(client, reviewer, "case-001", ct));
    }

    [Fact]
    public async Task Reviewer_with_valid_session_and_token_records_review_and_advances_revision()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var reviewer = await SignInAsync(client, "reviewer", ct);
        using var response = await SendAsync(client, HttpMethod.Post, "/api/cases/case-001/reviews",
            reviewer, true, new { expectedRevision = 1, decision = "accept" }, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, await RevisionAsync(client, reviewer, "case-001", ct));
    }

    [Fact]
    public async Task Sign_out_invalidates_session_and_requires_anti_forgery()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var presenter = await SignInAsync(client, "presenter", ct);
        using var noToken = await SendAsync(client, HttpMethod.Post, "/api/auth/sign-out", presenter, false, null, ct);
        using var signOut = await SendAsync(client, HttpMethod.Post, "/api/auth/sign-out", presenter, true, null, ct);
        using var afterSignOut = await SendAsync(client, HttpMethod.Get, "/api/cases/case-001", presenter, false, null, ct);

        Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterSignOut.StatusCode);
    }

    [Fact]
    public async Task Switching_users_while_signed_in_is_refused()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var presenter = await SignInAsync(client, "presenter", ct);
        using var response = await SendAsync(client, HttpMethod.Post, "/api/auth/sign-in",
            presenter, false, new { userId = "reviewer", passcode = Passcode }, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
