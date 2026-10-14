using System.Diagnostics;
using System.Net;
using System.Text.Json;
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
    public async Task Page_assets_fallback_and_preflight_carry_dd028_csp_and_hsts_headers()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/", "/cases/42/review", "/assets/app-3f9c1a.js", "/api/does-not-exist" })
        {
            using var response = await client.GetAsync(path);

            AssertSecurityHeaders(response);
        }

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/health");
        preflight.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        using var preflightResponse = await client.SendAsync(preflight);

        AssertSecurityHeaders(preflightResponse);
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        // Literal DD-028 policy, so the test fails if the constant drifts from the PRD.
        Assert.Equal(
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; frame-ancestors 'none'; base-uri 'none'",
            response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(TransportSecurity.StrictTransportSecurity, response.Headers.GetValues("Strict-Transport-Security").Single());
    }

    [Fact]
    public void Publish_builds_frontend_and_stages_dist_into_wwwroot()
    {
        var repoRoot = FindRepoRoot();
        var project = Path.Combine(repoRoot, "backend", "AirlineDemo.Api", "AirlineDemo.Api.csproj");

        var json = RunMsBuild(project, "-t:ComputeResolvedFilesToPublishList", "-getItem:ResolvedFileToPublish");

        Assert.True(File.Exists(Path.Combine(repoRoot, "frontend", "dist", "index.html")), "npm build did not produce frontend/dist/index.html.");
        using var document = JsonDocument.Parse(json);
        var staged = document.RootElement.GetProperty("Items").GetProperty("ResolvedFileToPublish")
            .EnumerateArray()
            .Select(item => item.GetProperty("RelativePath").GetString())
            .ToList();
        Assert.Contains("wwwroot/index.html", staged);
        Assert.Contains(staged, path => path is not null && path.StartsWith("wwwroot/assets/", StringComparison.Ordinal) && path.EndsWith(".js", StringComparison.Ordinal));
    }

    private static string RunMsBuild(string project, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add("-nologo");
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start dotnet msbuild.");
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var stderr = stderrTask.Result;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet msbuild failed ({process.ExitCode}): {stderr}{stdout}");
        }

        return stdout;
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
