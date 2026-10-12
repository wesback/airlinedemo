using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AirlineDemo.Api.Tests;

public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_endpoint_reports_service_readiness()
    {
        using var client = factory.CreateClient();

        var cancellationToken = TestContext.Current.CancellationToken;
        var response = await client.GetAsync("/api/health", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>(cancellationToken);
        Assert.Equal("ok", body?.Status);
    }

    private sealed record HealthResponse(string Status);
}
