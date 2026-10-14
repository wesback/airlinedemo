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

    // Redacts log attributes and scope values before any exporter sees them. Message text is not rewritten.
    // Scopes are folded into the record's attributes so they pass through the same filter; exporting raw
    // scopes would bypass it, so IncludeScopes stays off.
    public static OpenTelemetryLoggerOptions AddRedaction(this OpenTelemetryLoggerOptions options)
    {
        options.IncludeScopes = false;
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
            var source = new List<KeyValuePair<string, object?>>();
            if (data.Attributes is { } attributes)
            {
                source.AddRange(attributes);
            }

            data.ForEachScope(static (scope, pairs) =>
            {
                foreach (var pair in scope)
                {
                    pairs.Add(pair);
                }
            }, source);

            // Exception messages and stack traces are free text that the filter cannot check, so only the type name is kept.
            if (data.Exception is { } exception)
            {
                source.Add(new KeyValuePair<string, object?>("exception.type", exception.GetType().FullName));
                data.Exception = null;
            }

            if (source.Count == 0)
            {
                return;
            }

            // Re-render the message from the redacted values; the previously rendered text is never scanned.
            var redacted = RedactionFilter.RedactAttributes(source);
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
                if (!RedactionFilter.TryRedact(tag.Key, tag.Value, out var redacted))
                {
                    data.SetTag(tag.Key, RedactionFilter.Redacted);
                }
                else if (!Equals(redacted, tag.Value))
                {
                    data.SetTag(tag.Key, redacted);
                }
            }

            // Events recorded with Activity.AddEvent keep a reference to their tag collection, which stays mutable
            // after recording, so event attributes are rewritten here, in the same pass as span tags.
            foreach (var activityEvent in data.Events)
            {
                if (activityEvent.Tags is not { } eventTags)
                {
                    continue;
                }

                foreach (var tag in eventTags.ToList())
                {
                    if (!RedactionFilter.TryRedact(tag.Key, tag.Value, out var redacted))
                    {
                        SetEventTag(eventTags, tag.Key, RedactionFilter.Redacted);
                    }
                    else if (!Equals(redacted, tag.Value))
                    {
                        SetEventTag(eventTags, tag.Key, redacted);
                    }
                }
            }
        }

        // Activity stores event tags in an internal list (TagsLinkedList), not ActivityTagsCollection, so the
        // value is replaced through that list's Set method. If the method is missing the export fails closed.
        private static void SetEventTag(object eventTags, string key, object? value)
        {
            var set = eventTags.GetType()
                .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "Set" && m.GetParameters() is [{ ParameterType: var p }] && p == typeof(KeyValuePair<string, object?>))
                ?? throw new InvalidOperationException("Activity event tag list has no Set method; event attributes cannot be redacted.");
            set.Invoke(eventTags, [new KeyValuePair<string, object?>(key, value)]);
        }
    }
}
