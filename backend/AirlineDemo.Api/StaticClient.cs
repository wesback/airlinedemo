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
        app.MapFallback(async context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new { error = "not_found" });
                return;
            }

            var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
            if (!index.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(index);
        });
        return app;
    }
}
