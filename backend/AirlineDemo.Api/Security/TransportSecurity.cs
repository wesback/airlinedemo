using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AirlineDemo.Api.Security;

/// <summary>
/// Browser transport policy (DD-028): one origin, no CORS, restrictive CSP, HSTS.
/// </summary>
public static class TransportSecurity
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; " +
        "worker-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'";

    public const string StrictTransportSecurity = "max-age=31536000; includeSubDomains";

    public const string LocalIdentityBypassKey = "Demo:LocalIdentityBypass";

    /// <summary>
    /// Fails startup when the local identity bypass is enabled outside Development.
    /// </summary>
    public static void ValidateDeploymentConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        var bypassEnabled = bool.TryParse(configuration[LocalIdentityBypassKey], out var enabled) && enabled;
        if (!environment.IsDevelopment() && bypassEnabled)
        {
            throw new InvalidOperationException(
                $"{LocalIdentityBypassKey} must not be enabled in deployed mode (environment '{environment.EnvironmentName}').");
        }
    }

    /// <summary>
    /// Adds the CSP and HSTS headers to every response. CORS is intentionally not configured.
    /// </summary>
    public static IApplicationBuilder UseTransportSecurityHeaders(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                ApplyHeaders(context.Response.Headers);
                return Task.CompletedTask;
            });
            await next(context);
        });
    }

    private static void ApplyHeaders(IHeaderDictionary headers)
    {
        if (!headers.ContainsKey("Content-Security-Policy"))
        {
            headers["Content-Security-Policy"] = ContentSecurityPolicy;
        }
        headers["Strict-Transport-Security"] = StrictTransportSecurity;
    }

    public static string CreateDocumentContentSecurityPolicy(string nonce)
    {
        return $"default-src 'self'; script-src 'self' 'nonce-{nonce}'; style-src 'self' 'nonce-{nonce}'; " +
               "img-src 'self' data: blob:; worker-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'";
    }
}
