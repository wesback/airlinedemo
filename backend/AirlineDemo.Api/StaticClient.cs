using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AirlineDemo.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AirlineDemo.Api;

/// <summary>
/// Serves the built React client from wwwroot on the same origin (DD-013, DD-028). Unknown extensionless non-API paths
/// fall back to index.html for client-side routes; unknown file-like paths and /api/* paths return 404 (JSON for the API).
/// </summary>
public static class StaticClient
{
    private static readonly Regex MetaTagRegex = new("<meta\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AttributeRegex = new("\\b(?<name>[\\w:-]+)\\s*=\\s*(?:\"(?<double>[^\"]*)\"|'(?<single>[^']*)')", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static WebApplication MapStaticClient(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            var relativeStaticPath = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var assetPath = string.IsNullOrEmpty(app.Environment.WebRootPath)
                ? null
                : Path.Combine(app.Environment.WebRootPath, relativeStaticPath);
            var isExistingExtensionlessAsset = !path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) &&
                                              !Path.HasExtension(path) &&
                                              assetPath is not null && File.Exists(assetPath);
            var isDocument = context.Request.Method == HttpMethods.Get &&
                             (path.Equals("/", StringComparison.OrdinalIgnoreCase) ||
                              path.Equals("/index.html", StringComparison.OrdinalIgnoreCase) ||
                              (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) && !Path.HasExtension(path) && !isExistingExtensionlessAsset));

            if (context.Request.Method == HttpMethods.Get && isExistingExtensionlessAsset && assetPath is not null)
            {
                context.Response.ContentType = "application/octet-stream";
                await context.Response.SendFileAsync(assetPath);
                return;
            }

            if (!isDocument)
            {
                await next();
                return;
            }

            var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
            if (!index.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await using var stream = index.CreateReadStream();
            using var reader = new StreamReader(stream);
            var html = await reader.ReadToEndAsync();

            var nonce = TryGetNonce(html);
            if (nonce is not null)
            {
                html = ReplaceNonceMetaContent(html, nonce);
                context.Response.Headers.ContentSecurityPolicy = TransportSecurity.CreateDocumentContentSecurityPolicy(nonce);
            }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(html);
        });
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions { ServeUnknownFileTypes = true });
        app.MapFallback(async context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new { error = "not_found" });
                return;
            }

            if (Path.HasExtension(context.Request.Path.Value))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
        });
        return app;
    }

    private static string? TryGetNonce(string html)
    {
        foreach (Match meta in MetaTagRegex.Matches(html))
        {
            var attributes = AttributeRegex.Matches(meta.Value);
            string? name = null;
            string? content = null;
            foreach (Match attribute in attributes)
            {
                var value = attribute.Groups["double"].Success ? attribute.Groups["double"].Value : attribute.Groups["single"].Value;
                if (attribute.Groups["name"].Value.Equals("name", StringComparison.OrdinalIgnoreCase)) name = value;
                if (attribute.Groups["name"].Value.Equals("content", StringComparison.OrdinalIgnoreCase)) content = value;
            }
            if (name?.Equals("csp-nonce", StringComparison.OrdinalIgnoreCase) == true && content == "__CSP_NONCE__")
            {
                return Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            }
        }
        return null;
    }

    private static string ReplaceNonceMetaContent(string html, string nonce)
    {
        foreach (Match meta in MetaTagRegex.Matches(html).Cast<Match>().Where(match => match.Value.Contains("csp-nonce", StringComparison.OrdinalIgnoreCase)).Reverse())
        {
            var replaced = meta.Value.Replace("__CSP_NONCE__", nonce);
            html = html.Remove(meta.Index, meta.Length).Insert(meta.Index, replaced);
        }
        return html;
    }
}
