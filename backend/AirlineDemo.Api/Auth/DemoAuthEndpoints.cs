using System.Security.Cryptography;
using System.Text;

namespace AirlineDemo.Api.Auth;

public sealed record SignInRequest(string? UserId, string? Passcode);

public static class DemoAuthEndpoints
{
    public const string CookieName = "__Host-demo-session";
    public const string CsrfHeaderName = "X-CSRF-Token";
    public const string PasscodeSetting = "Demo:Passcode";
    private const string GenericFailure = "Sign-in failed.";

    private static readonly CookieOptions SessionCookie = new() { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true };

    public static IEndpointRouteBuilder MapDemoAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/users", () => Results.Ok(DemoFixture.Users.Select(u => new { u.Id, u.DisplayName, u.Role })));
        app.MapPost("/api/auth/sign-in", SignIn);
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

    private static IResult SignIn(SignInRequest request, HttpContext http, IConfiguration config, DemoSessionStore sessions, PasscodeThrottle throttle)
    {
        if (CurrentSession(http, sessions) is not null)
            return Results.Conflict(new { error = "Sign out before switching users." });

        var source = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (throttle.IsLocked(source))
            return Results.Json(new { error = GenericFailure }, statusCode: StatusCodes.Status429TooManyRequests);

        var expected = config[PasscodeSetting];
        var user = DemoFixture.Find(request.UserId);
        var passcodeOk = !string.IsNullOrEmpty(expected) && Matches(request.Passcode ?? string.Empty, expected);
        if (user is null || !passcodeOk)
        {
            throttle.RecordFailure(source);
            return Results.Json(new { error = GenericFailure }, statusCode: StatusCodes.Status401Unauthorized);
        }

        throttle.Reset(source);
        var session = sessions.Create(user);
        http.Response.Cookies.Append(CookieName, session.Id, SessionCookie);
        return Results.Ok(SessionView(session));
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
