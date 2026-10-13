using System.Net;
using AirlineDemo.Api.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AirlineDemo.Api.Tests;

/// <summary>
/// Covers acceptance criterion [M-02] (NFR-001, AC-020, DD-028): CSP and HSTS headers on every response,
/// no CORS, and the local identity bypass refused in deployed mode.
/// </summary>
public sealed class TransportSecurityTests
{
    private static WebApplicationFactory<Program> CreateFactory(string environment, string? bypass = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Demo:Passcode"] = "test-passcode",
                    [TransportSecurity.LocalIdentityBypassKey] = bypass,
                }));
        });

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Health_response_carries_csp_and_hsts_headers(string environment)
    {
        using var factory = CreateFactory(environment);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TransportSecurity.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(TransportSecurity.StrictTransportSecurity, response.Headers.GetValues("Strict-Transport-Security").Single());
    }

    [Fact]
    public void Csp_matches_dd_028()
    {
        Assert.Equal(
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; frame-ancestors 'none'; base-uri 'none'",
            TransportSecurity.ContentSecurityPolicy);
    }

    [Fact]
    public void Hsts_matches_m02_pinned_value()
    {
        Assert.Equal("max-age=31536000; includeSubDomains", TransportSecurity.StrictTransportSecurity);
    }

    [Fact]
    public async Task Responses_do_not_enable_cors()
    {
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");

        using var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public void Bypass_enabled_in_deployed_mode_fails_startup()
    {
        using var factory = CreateFactory("Production", bypass: "true");

        var ex = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(ex);
        Assert.Contains(TransportSecurity.LocalIdentityBypassKey, ex!.ToString());
    }

    [Fact]
    public void Bypass_enabled_in_development_is_allowed()
    {
        using var factory = CreateFactory("Development", bypass: "true");

        var ex = Record.Exception(() => factory.CreateClient());

        Assert.Null(ex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void Bypass_disabled_in_deployed_mode_starts(string? bypass)
    {
        using var factory = CreateFactory("Production", bypass);

        var ex = Record.Exception(() => factory.CreateClient());

        Assert.Null(ex);
    }

    [Fact]
    public void Validate_rejects_bypass_for_non_development_environment()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [TransportSecurity.LocalIdentityBypassKey] = "true" })
            .Build();
        var env = new FakeEnvironment("Staging");

        Assert.Throws<InvalidOperationException>(() => TransportSecurity.ValidateDeploymentConfiguration(config, env));
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "AirlineDemo.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
