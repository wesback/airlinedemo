using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Diagnostics;

namespace AirlineDemo.Api.Auth;

public sealed record SignInRequest(string? UserId, string? Passcode);

public sealed record PasscodeRequest(string? Passcode);

public sealed record SwitchUserRequest(string? UserId);

public static class DemoAuthEndpoints
{
    public const string CookieName = "__Host-demo-session";
    public const string PasscodeCookieName = "__Host-demo-passcode";
    public const string CsrfHeaderName = "X-CSRF-Token";
    public const string PasscodeSetting = "Demo:Passcode";
    public static readonly TimeSpan PasscodeLifetime = TimeSpan.FromMinutes(5);
    private const string GenericFailure = "Sign-in failed.";

    private static readonly CookieOptions SessionCookie = new() { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true };
    private static readonly CookieOptions PasscodeCookieClear = new() { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true };

    // Random server-side grant tokens -> expiry. The cookie carries only the token, never the passcode.
    private static readonly ConcurrentDictionary<string, DateTimeOffset> PasscodeGrants = new();

    public static IEndpointRouteBuilder MapDemoAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/users", (HttpContext http, DemoSessionStore sessions, TimeProvider time) =>
            CurrentSession(http, sessions) is not null || HasValidPasscodeStep(http, time)
                ? Results.Ok(DemoFixture.Users.Select(u => new { u.Id, u.DisplayName, u.Role }))
                : Unauthenticated());
        app.MapPost("/api/auth/passcode", PasscodeStep);
        app.MapPost("/api/auth/sign-in", SignIn);
        app.MapPost("/api/auth/switch-user", SwitchUser);
        app.MapPost("/api/auth/sign-out", SignOut);
        return app;
    }

    internal static DemoSession? CurrentSession(HttpContext http, DemoSessionStore sessions) =>
        sessions.Find(http.Request.Cookies[CookieName]);

    internal static bool HasValidAntiForgery(HttpContext http, DemoSession session)
    {
        var supplied = http.Request.Headers[CsrfHeaderName].ToString();
        return supplied.Length > 0 && Matches(supplied, session.CsrfToken);
    }

    internal static IResult Unauthenticated() => Results.Json(new { error = "Not signed in." }, statusCode: StatusCodes.Status401Unauthorized);

    internal static IResult AntiForgeryFailed() => Results.Json(new { error = "Anti-forgery validation failed." }, statusCode: StatusCodes.Status403Forbidden);

    private static void EmitAuthFailure(ILogger logger, IConfiguration config) =>
        TelemetryRedaction.EmitSecurityEvent(logger, TelemetryRedaction.SecurityEvents.AuthScopeFailure,
            TelemetryRedaction.ServiceVersion, config["Telemetry:Environment"] ?? "demo");

    private static IResult SignIn(SignInRequest request, HttpContext http, IConfiguration config, DemoSessionStore sessions, PasscodeThrottle throttle, ILogger<DemoSessionStore> logger)
    {
        if (CurrentSession(http, sessions) is not null)
            return Results.Conflict(new { error = "Sign out before switching users." });

        var source = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (throttle.IsLocked(source))
        {
            // A passcode lockout is an authentication failure, not an investigation or model budget exhaustion.
            EmitAuthFailure(logger, config);
            return Results.Json(new { error = GenericFailure }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        var expected = config[PasscodeSetting];
        var user = DemoFixture.Find(request.UserId);
        var passcodeOk = !string.IsNullOrEmpty(expected) && Matches(request.Passcode ?? string.Empty, expected);
        if (user is null || !passcodeOk)
        {
            throttle.RecordFailure(source);
            EmitAuthFailure(logger, config);
            return Results.Json(new { error = GenericFailure }, statusCode: StatusCodes.Status401Unauthorized);
        }

        throttle.Reset(source);
        ClearPasscodeStep(http);
        var session = sessions.Create(user);
        http.Response.Cookies.Append(CookieName, session.Id, SessionCookie);
        return Results.Ok(SessionView(session));
    }

    private static async Task<IResult> SwitchUser(HttpContext http, IConfiguration config, DemoSessionStore sessions, ILogger<DemoSessionStore> logger)
    {
        var oldSession = CurrentSession(http, sessions);
        if (oldSession is null)
        {
            TelemetryRedaction.EmitRoleSwitchEvent(logger, "unauthorized", "unknown", null,
                TelemetryRedaction.ServiceVersion, config["Telemetry:Environment"] ?? "demo");
            return Unauthenticated();
        }
        if (!HasValidAntiForgery(http, oldSession))
        {
            TelemetryRedaction.EmitRoleSwitchEvent(logger, "anti_forgery_failed", oldSession.User.Role, oldSession.Id,
                TelemetryRedaction.ServiceVersion, config["Telemetry:Environment"] ?? "demo");
            return AntiForgeryFailed();
        }

        SwitchUserRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<SwitchUserRequest>(http.RequestAborted);
        }
        catch (JsonException)
        {
            request = null;
        }

        var user = request is not null ? DemoFixture.Find(request.UserId) : null;
        if (user is null)
        {
            TelemetryRedaction.EmitRoleSwitchEvent(logger, "unknown_user", oldSession.User.Role, oldSession.Id,
                TelemetryRedaction.ServiceVersion, config["Telemetry:Environment"] ?? "demo");
            return Results.Json(new { error = "Unknown user." }, statusCode: StatusCodes.Status400BadRequest);
        }

        sessions.Remove(oldSession.Id);
        var newSession = sessions.Create(user);
        http.Response.Cookies.Append(CookieName, newSession.Id, SessionCookie);
        TelemetryRedaction.EmitRoleSwitchEvent(logger, "success", user.Role, newSession.Id,
            TelemetryRedaction.ServiceVersion, config["Telemetry:Environment"] ?? "demo");
        return Results.Ok(SessionView(newSession));
    }

    private static IResult PasscodeStep(PasscodeRequest request, HttpContext http, IConfiguration config, PasscodeThrottle throttle, TimeProvider time, ILogger<DemoSessionStore> logger)
    {
        var source = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (throttle.IsLocked(source))
        {
            EmitAuthFailure(logger, config);
            return Results.Json(new { error = GenericFailure }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        var expected = config[PasscodeSetting];
        var passcodeOk = !string.IsNullOrEmpty(expected) && Matches(request.Passcode ?? string.Empty, expected);
        if (!passcodeOk)
        {
            throttle.RecordFailure(source);
            EmitAuthFailure(logger, config);
            return Results.Json(new { error = GenericFailure }, statusCode: StatusCodes.Status401Unauthorized);
        }

        throttle.Reset(source);
        var token = IssuePasscodeGrant(time);
        http.Response.Cookies.Append(PasscodeCookieName, token, new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true, MaxAge = PasscodeLifetime,
        });
        return Results.NoContent();
    }

    private static string IssuePasscodeGrant(TimeProvider time)
    {
        var now = time.GetUtcNow();
        foreach (var (stale, expires) in PasscodeGrants)
            if (now >= expires) PasscodeGrants.TryRemove(stale, out _);

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        PasscodeGrants[token] = now + PasscodeLifetime;
        return token;
    }

    private static bool HasValidPasscodeStep(HttpContext http, TimeProvider time)
    {
        var token = http.Request.Cookies[PasscodeCookieName];
        return !string.IsNullOrEmpty(token)
            && PasscodeGrants.TryGetValue(token, out var expires)
            && time.GetUtcNow() < expires;
    }

    private static void ClearPasscodeStep(HttpContext http)
    {
        var token = http.Request.Cookies[PasscodeCookieName];
        if (string.IsNullOrEmpty(token)) return;
        PasscodeGrants.TryRemove(token, out _);
        http.Response.Cookies.Delete(PasscodeCookieName, PasscodeCookieClear);
    }

    private static IResult SignOut(HttpContext http, DemoSessionStore sessions)
    {
        var session = CurrentSession(http, sessions);
        if (session is null) return Unauthenticated();
        if (!HasValidAntiForgery(http, session)) return AntiForgeryFailed();

        sessions.Remove(session.Id);
        http.Response.Cookies.Delete(CookieName, SessionCookie);
        return Results.NoContent();
    }

    private static object SessionView(DemoSession s) => new { s.User.Id, s.User.DisplayName, s.User.Role, s.User.CaseScopes, csrfToken = s.CsrfToken };

    private static bool Matches(string supplied, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
}
