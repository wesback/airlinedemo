using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AirlineDemo.Api.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

// Runs test classes one at a time: the telemetry tests share a global listener.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace AirlineDemo.Api.Tests;

/// <summary>
/// Covers FR-013 and DD-028: the React client is served from wwwroot on one origin with an SPA fallback,
/// /api/* misses return JSON 404, and the CSP/HSTS headers apply to the page and assets without CORS.
/// </summary>
public sealed class StaticClientTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), "static-client-" + Guid.NewGuid().ToString("N"));

    public StaticClientTests()
    {
        var wwwroot = Path.Combine(_contentRoot, "wwwroot");
        Directory.CreateDirectory(Path.Combine(wwwroot, "assets"));
        File.WriteAllText(Path.Combine(wwwroot, "index.html"), "<!doctype html><meta name=\"csp-nonce\" content=\"__CSP_NONCE__\"><title>reviewer</title><div id=\"root\"></div>");
        File.WriteAllText(Path.Combine(wwwroot, "assets", "app-3f9c1a.js"), "console.log('client');");
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseContentRoot(_contentRoot);
            builder.UseWebRoot(Path.Combine(_contentRoot, "wwwroot"));
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Passcode"] = "test-passcode" }));
        });

    [Fact]
    public async Task Root_serves_index_html_and_hashed_assets_are_served_under_assets()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var page = await client.GetAsync("/");
        using var asset = await client.GetAsync("/assets/app-3f9c1a.js");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<title>reviewer</title>", await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Contains("console.log", await asset.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task document_response_nonce_contract()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        string? firstNonce = null;
        foreach (var path in new[] { "/", "/index.html", "/work" })
        {
            using var response = await client.GetAsync(path);
            var html = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.GetValues("Cache-Control").Single());
            var nonceMatch = Regex.Match(html, "<meta\\b[^>]*name=[\"']csp-nonce[\"'][^>]*content=[\"']([^\"']+)[\"'][^>]*>", RegexOptions.IgnoreCase);
            Assert.True(nonceMatch.Success);
            var nonce = nonceMatch.Groups[1].Value;
            Assert.Matches("^[A-Za-z0-9+/]{22}==$", nonce);
            Assert.Equal(24, nonce.Length);
            Assert.Equal(TransportSecurity.CreateDocumentContentSecurityPolicy(nonce), response.Headers.GetValues("Content-Security-Policy").Single());
            firstNonce ??= nonce;
        }

        using var secondRoot = await client.GetAsync("/");
        var secondHtml = await secondRoot.Content.ReadAsStringAsync();
        var secondMatch = Regex.Match(secondHtml, "<meta\\b[^>]*name=[\"']csp-nonce[\"'][^>]*content=[\"']([^\"']+)[\"'][^>]*>", RegexOptions.IgnoreCase);
        Assert.True(secondMatch.Success);
        Assert.NotEqual(firstNonce, secondMatch.Groups[1].Value);
    }

    [Fact]
    public async Task Document_nonce_fallback_contract()
    {
        using var factory = CreateFactory();
        var wwwroot = Path.Combine(_contentRoot, "wwwroot");

        foreach (var html in new[] { "<!doctype html><title>reviewer</title>", "<!doctype html><meta name=\"csp-nonce\" content=\"\"><title>reviewer</title>" })
        {
            File.WriteAllText(Path.Combine(wwwroot, "index.html"), html);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/");

            Assert.Equal(TransportSecurity.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.DoesNotContain("nonce-", response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.DoesNotContain("__CSP_NONCE__", await response.Content.ReadAsStringAsync());
        }

        File.Delete(Path.Combine(wwwroot, "index.html"));
        using var missingFactoryClient = factory.CreateClient();
        using var missing = await missingFactoryClient.GetAsync("/");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Client_route_falls_back_to_index_html()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/cases/42/review");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<title>reviewer</title>", html);
        Assert.Matches("<meta\\b[^>]*name=[\"']csp-nonce[\"'][^>]*content=[\"'][A-Za-z0-9+/]{22}==[\"'][^>]*>", html);
    }

    [Fact]
    public async Task Missing_asset_returns_404_not_the_page()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/assets/missing-0a1b2c.js");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("<title>reviewer</title>", body);
    }

    [Fact]
    public async Task Unknown_api_path_returns_json_404_not_the_page()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/does-not-exist");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("<title>reviewer</title>", body);
        Assert.Equal("not_found", JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Static_client_sends_exact_security_headers()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/assets/app-3f9c1a.js", "/api/does-not-exist" })
        {
            using var response = await client.GetAsync(path);

            AssertSecurityHeaders(response);
        }

        foreach (var path in new[] { "/", "/index.html", "/cases/42/review" })
        {
            using var response = await client.GetAsync(path);

            AssertDocumentSecurityHeaders(response);
        }

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/health");
        preflight.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        using var preflightResponse = await client.SendAsync(preflight);

        AssertSecurityHeaders(preflightResponse);
    }

    // Response headers the framework produces on its own; every other header must be one of the two DD-028 headers.
    private static readonly HashSet<string> FrameworkResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type",
        "Content-Length",
        "Content-Encoding",
        "Date",
        "Server",
        "Last-Modified",
        "ETag",
        "Accept-Ranges",
        "Vary",
        "Cache-Control",
        "Expires",
        "Location",
        "Connection",
        "Keep-Alive",
        "Transfer-Encoding",
    };

    private static void AssertDocumentSecurityHeaders(HttpResponseMessage response)
    {
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("nonce-", csp);
        Assert.Equal("no-store", response.Headers.GetValues("Cache-Control").Single());
        Assert.Equal(TransportSecurity.StrictTransportSecurity, response.Headers.GetValues("Strict-Transport-Security").Single());
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        // Literal Hangar policy, so the test fails if the constant drifts from the PRD.
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Equal(
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; worker-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'",
            csp);
        Assert.DoesNotContain("nonce-", csp);
        Assert.False(response.Headers.TryGetValues("Cache-Control", out var cacheControl) && cacheControl.Contains("no-store"));
        Assert.Equal(TransportSecurity.StrictTransportSecurity, response.Headers.GetValues("Strict-Transport-Security").Single());
        // DD-028: exactly two security headers (CSP and HSTS). Any response header that is neither framework-produced
        // nor one of those two fails, so no other security header can slip in.
        var unexpectedHeaders = response.Headers.Select(h => h.Key)
            .Concat(response.Content?.Headers.Select(h => h.Key) ?? [])
            .Where(name => !FrameworkResponseHeaders.Contains(name))
            .Where(name => name is not ("Content-Security-Policy" or "Strict-Transport-Security"))
            .ToArray();
        Assert.Empty(unexpectedHeaders);
        // DD-028: no CORS, so no cross-origin allow header is ever emitted on the page or assets.
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Headers"));
    }

    [Fact]
    public async Task Cross_origin_preflight_gets_no_allow_headers()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/health");
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");

        using var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
    }
}
