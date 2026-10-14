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

    // Records which exporters were actually attached. Flags are set inside the exporter configuration
    // callbacks, so they become true only when the logger/tracer providers are built.
    public sealed class ExporterRegistration
    {
        public bool Logs { get; internal set; }
        public bool Traces { get; internal set; }
    }

    public static IServiceCollection AddAirlineDiagnostics(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration[ConnectionStringSetting];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return services;
        }

        var registration = new ExporterRegistration();
        services.AddSingleton(registration);

        services.AddLogging(logging => logging.AddOpenTelemetry(logs =>
        {
            logs.AddRedaction();
            logs.AddAzureMonitorLogExporter(o =>
            {
                o.ConnectionString = connectionString;
                registration.Logs = true;
            });
        }));

        services.AddOpenTelemetry().WithTracing(tracing =>
        {
            tracing.AddSource(TelemetryRedaction.ServiceName);
            // Processors run in registration order, so redaction must be added before the exporter.
            tracing.AddRedaction();
            tracing.AddAzureMonitorTraceExporter(o =>
            {
                o.ConnectionString = connectionString;
                registration.Traces = true;
            });
        });

        return services;
    }

    // Redacts structured log attributes before any exporter sees them. Message text is not rewritten.
    public static OpenTelemetryLoggerOptions AddRedaction(this OpenTelemetryLoggerOptions options)
    {
        options.AddProcessor(new RedactingLogProcessor());
        return options;
    }

    // Redacts span tags and span event attributes before export.
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

            var redacted = RedactionFilter.RedactAttributes(attributes);
            var changed = false;
            for (var i = 0; i < attributes.Count; i++)
            {
                changed |= !Equals(attributes[i].Value, redacted[i].Value);
            }

            if (!changed)
            {
                return;
            }

            // Re-render the message from the redacted values; the previously rendered text is never scanned.
            data.Attributes = redacted;
            data.FormattedMessage = RedactionFilter.RenderMessage(data.Body, redacted);
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

            // ActivityEvent exposes its tags read-only, but the SDK stores them in a mutable ActivityTagsCollection.
            foreach (var activityEvent in data.Events)
            {
                if (activityEvent.Tags is not IDictionary<string, object?> tags)
                {
                    continue;
                }

                foreach (var tag in tags.ToList())
                {
                    var text = tag.Value?.ToString() ?? string.Empty;
                    var redacted = RedactionFilter.RedactValue(tag.Key, text) ?? string.Empty;
                    if (redacted != text)
                    {
                        tags[tag.Key] = redacted;
                    }
                }
            }
        }
    }
}
