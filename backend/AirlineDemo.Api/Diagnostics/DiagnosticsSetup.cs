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

    public static IServiceCollection AddAirlineDiagnostics(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration[ConnectionStringSetting];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return services;
        }

        services.AddLogging(logging => logging.AddOpenTelemetry(logs =>
        {
            logs.AddRedaction();
            logs.AddAzureMonitorLogExporter(o =>
            {
                o.ConnectionString = connectionString;
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
            });
        });

        return services;
    }

    // Redacts log attributes and scope values before any exporter sees them. Message text is re-rendered from the
    // redacted values. Scopes are read by the processor, folded into the attributes through the same filter, and
    // then removed from the record so exporters cannot read the raw scope values.
    public static OpenTelemetryLoggerOptions AddRedaction(this OpenTelemetryLoggerOptions options)
    {
        options.IncludeScopes = true;
        options.AddProcessor(new RedactingLogProcessor());
        return options;
    }

    // Redacts span tags and span event attributes before export.
    public static TracerProviderBuilder AddRedaction(this TracerProviderBuilder builder) =>
        builder.AddProcessor(new RedactingActivityProcessor());

    private sealed class RedactingLogProcessor : BaseProcessor<LogRecord>
    {
        // LogRecord keeps scopes in an internal ScopeStorage field; it is cleared after scopes are copied so exporters see no raw scopes.
        private const System.Reflection.BindingFlags AnyInstance =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

        private static readonly Action<LogRecord> ClearScopeProvider = ResolveScopeProviderClearer();

        private static Action<LogRecord> ResolveScopeProviderClearer()
        {
            var field = typeof(LogRecord).GetField("ScopeStorage", AnyInstance)
                ?? throw new InvalidOperationException("LogRecord exposes no ScopeStorage field; scopes cannot be removed before export.");
            return record => field.SetValue(record, null);
        }

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
            ClearScopeProvider(data);

            // Exception messages and stack traces are free text that the filter cannot check, so only the type name is kept.
            if (data.Exception is { } exception)
            {
                source.Add(new KeyValuePair<string, object?>("exception.type", exception.GetType().FullName));
                data.Exception = null;
            }

            // Without properties or scopes the rendered text could carry free text, so only the fixed body is kept.
            if (source.Count == 0)
            {
                data.FormattedMessage = null;
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
                    data.SetTag(tag.Key, null);
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
                        SetEventTag(eventTags, tag.Key, null);
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
