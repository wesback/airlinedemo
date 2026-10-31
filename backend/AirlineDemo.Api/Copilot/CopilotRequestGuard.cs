using AirlineDemo.Api.Auth;
using Microsoft.Net.Http.Headers;

namespace AirlineDemo.Api.Copilot;

/// <summary>
/// The shared same-origin checks for copilot requests (the MCP host and the trusted confirmation routes): a live demo
/// session, an allowed Origin and Host, the session anti-forgery token on POST and a JSON body on POST.
/// </summary>
internal static class CopilotRequestGuard
{
    public static HashSet<string> Origins(IConfiguration configuration) =>
        (configuration[CopilotMcp.AllowedOriginsSetting] is { Length: > 0 } configured ? configured : "http://localhost")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

    public static HashSet<string> Hosts(IEnumerable<string> origins) =>
        origins.Select(origin => new Uri(origin).Authority).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static (int Status, string Code)? Check(HttpContext http, DemoSession? session, HashSet<string> origins, HashSet<string> hosts)
    {
        var origin = http.Request.Headers.Origin;
        var post = HttpMethods.IsPost(http.Request.Method);
        return session is null ? (401, "unauthenticated")
            : origin.Count != 1 || !origins.Contains(origin[0]!) ? (403, "origin_forbidden")
            : !hosts.Contains(http.Request.Host.Value ?? "") ? (403, "host_forbidden")
            : post && !DemoAuthEndpoints.HasValidAntiForgery(http, session) ? (403, "anti_forgery_failed")
            : post && !(MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var media)
                && media.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)) ? (415, "unsupported_media_type")
            : null;
    }

    public static (int Status, string Code)? Check(HttpContext http, DemoSession? session)
    {
        var origins = Origins(http.RequestServices.GetRequiredService<IConfiguration>());
        return Check(http, session, origins, Hosts(origins));
    }

    public static IResult Error(int status, string code) =>
        Results.Json(new { contractVersion = CopilotProjections.ContractVersion, error = new { code } }, statusCode: status);
}
