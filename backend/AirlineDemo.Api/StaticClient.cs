using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AirlineDemo.Api;

/// <summary>
/// Serves the built React client from wwwroot on the same origin (DD-013, DD-028). Unknown non-API paths fall back
/// to index.html for client-side routes; unknown /api/* paths return a JSON 404 instead of the page.
/// </summary>
public static class StaticClient
{
    public static WebApplication MapStaticClient(this WebApplication app)
    {
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapFallback("/api/{**rest}", () => Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound));
        app.MapFallbackToFile("index.html");
        return app;
    }
}
