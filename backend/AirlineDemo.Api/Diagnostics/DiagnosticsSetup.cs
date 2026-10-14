using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;

namespace AirlineDemo.Api.Diagnostics;

public static class DiagnosticsSetup
{
    public const string ConnectionStringSetting = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    // Without a connection string, diagnostics stay on the console only.
    public static WebApplicationBuilder AddAirlineDiagnostics(this WebApplicationBuilder builder)
    {
        builder.Services.AddRedactingLogging();

        var connectionString = builder.Configuration[ConnectionStringSetting];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return builder;
        }

        builder.Logging.AddOpenTelemetry(logging =>
            logging.AddAzureMonitorLogExporter(options => options.ConnectionString = connectionString));
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .AddProcessor(new RedactionFilter.RedactingActivityProcessor())
                .AddAzureMonitorTraceExporter(options => options.ConnectionString = connectionString));
        return builder;
    }

    public static IServiceCollection AddRedactingLogging(this IServiceCollection services) =>
        services.AddSingleton<ILoggerFactory>(sp => new RedactionFilter.RedactingLoggerFactory(new LoggerFactory(
            sp.GetServices<ILoggerProvider>(),
            sp.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>())));
}
