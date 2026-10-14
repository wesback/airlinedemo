using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using AirlineDemo.Api.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

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
        File.WriteAllText(Path.Combine(wwwroot, "index.html"), "<!doctype html><title>reviewer</title><div id=\"root\"></div>");
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
    public async Task Client_route_falls_back_to_index_html()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/cases/42/review");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<title>reviewer</title>", await response.Content.ReadAsStringAsync());
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
    public async Task Page_and_assets_carry_csp_and_hsts_headers()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/", "/cases/42/review", "/assets/app-3f9c1a.js", "/api/does-not-exist" })
        {
            using var response = await client.GetAsync(path);

            Assert.Equal(TransportSecurity.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal(TransportSecurity.StrictTransportSecurity, response.Headers.GetValues("Strict-Transport-Security").Single());
        }
    }

    [Fact]
    public void Publish_builds_frontend_and_stages_dist_into_wwwroot()
    {
        var project = XDocument.Load(Path.Combine(FindRepoRoot(), "backend", "AirlineDemo.Api", "AirlineDemo.Api.csproj"));
        var target = project.Descendants("Target").Single(t => (string?)t.Attribute("Name") == "BuildAndStageClient");

        Assert.Equal("ComputeResolvedFilesToPublishList", (string?)target.Attribute("AfterTargets"));
        var exec = target.Descendants("Exec").Single().Attribute("Command")?.Value ?? string.Empty;
        Assert.Contains("npm --prefix", exec);
        Assert.Contains("run build", exec);
        Assert.Contains("frontend/dist", target.Descendants("ClientBuildFile").Single().Attribute("Include")?.Value);
        var staged = target.Descendants("ResolvedFileToPublish").Single().Attribute("RelativePath")?.Value;
        Assert.StartsWith("wwwroot/", staged);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "backend", "AirlineDemo.Api", "AirlineDemo.Api.csproj")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
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
