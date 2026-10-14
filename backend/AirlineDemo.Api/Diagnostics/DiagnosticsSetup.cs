using System.Diagnostics;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace AirlineDemo.Api.Diagnostics;

// Registers Application Insights exporters only when a connection string is configured (DD-029).
public static class DiagnosticsSetup
{
    public const string ConnectionStringSetting = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    // Marker recording which exporters were registered; lets tests verify the DI wiring without Azure access.
    public sealed record ExporterRegistration(bool Logs, bool Traces);

    public static IServiceCollection AddAirlineDiagnostics(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration[ConnectionStringSetting];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return services;
        }

        services.AddSingleton(new ExporterRegistration(Logs: true, Traces: true));

        services.AddLogging(logging => logging.AddOpenTelemetry(logs =>
        {
            logs.AddRedaction();
            logs.AddAzureMonitorLogExporter(o => o.ConnectionString = connectionString);
        }));

        services.AddOpenTelemetry().WithTracing(tracing =>
        {
            tracing.AddSource(TelemetryRedaction.ServiceName);
            tracing.AddRedaction();
            tracing.AddAzureMonitorTraceExporter(o => o.ConnectionString = connectionString);
        });

        return services;
    }

    // Redacts structured log attributes before any exporter sees them. Message text is not rewritten.
    public static OpenTelemetryLoggerOptions AddRedaction(this OpenTelemetryLoggerOptions options)
    {
        options.AddProcessor(new RedactingLogProcessor());
        return options;
    }

    // Redacts trace tags before export.
    public static TracerProviderBuilder AddRedaction(this TracerProviderBuilder builder) =>
        builder.AddProcessor(new RedactingActivityProcessor());

    private sealed class RedactingLogProcessor : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord data)
        {
            if (data.Attributes is not { Count: > 0 } attributes)
            {
                return;
            }

            data.Attributes = RedactionFilter.RedactAttributes(attributes);
        }
    }

    private sealed class RedactingActivityProcessor : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            foreach (var tag in data.TagObjects.ToList())
            {
                var text = tag.Value?.ToString() ?? string.Empty;
                var redacted = RedactionFilter.RedactValue(tag.Key, text) ?? string.Empty;
                if (redacted != text)
                {
                    data.SetTag(tag.Key, redacted);
                }
            }
        }
    }
}
