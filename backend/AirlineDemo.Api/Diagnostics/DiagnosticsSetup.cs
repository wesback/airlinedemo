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

        private const string FixedLogBody = "diagnostic event";

        private static readonly Action<LogRecord> ClearScopeProvider = ResolveScopeProviderClearer();

        // Maps the exception type to an allowlist-shaped identifier: System.InvalidOperationException -> invalid-operation,
        // airline.demo.boom stays as is. Names that still fail the rule are omitted by the caller.
        private static string ErrorTypeName(Exception exception)
        {
            var name = exception.GetType().FullName ?? exception.GetType().Name;
            if (name.StartsWith("System.", StringComparison.Ordinal))
            {
                name = name["System.".Length..];
            }

            if (name.EndsWith("Exception", StringComparison.Ordinal) && name.Length > "Exception".Length)
            {
                name = name[..^"Exception".Length];
            }

            return string.Concat(name.Select((c, i) =>
                i > 0 && char.IsUpper(c) ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
        }

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
                // OpenTelemetry adds the raw message template as {OriginalFormat}; it is free text, so it is never kept.
                source.AddRange(attributes.Where(a => a.Key != "{OriginalFormat}"));
            }

            data.ForEachScope(static (scope, pairs) =>
            {
                foreach (var pair in scope)
                {
                    pairs.Add(pair);
                }
            }, source);
            ClearScopeProvider(data);

            // Exception messages and stack traces are free text that the filter cannot check, so only the exception's type name
            // (mapped to a lowercase identifier) is kept as error.type, and only when it passes the allowlist.
            if (data.Exception is { } exception)
            {
                var errorType = ErrorTypeName(exception);
                if (TelemetryRedaction.IsAllowedErrorType(errorType))
                {
                    source.Add(new KeyValuePair<string, object?>("error.type", errorType));
                }
                data.Exception = null;
            }

            // Body and rendered message are always replaced by the fixed text, so no interpolated or formatted message is exported.
            data.Attributes = source.Count > 0 ? RedactionFilter.RedactAttributes(source) : null;
            data.Body = FixedLogBody;
            data.FormattedMessage = FixedLogBody;
        }
    }

    private sealed class RedactingActivityProcessor : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            foreach (var tag in data.TagObjects.ToList())
            {
                if (RedactionFilter.IsSensitiveKey(tag.Key) || !RedactionFilter.TryRedact(tag.Key, tag.Value, out var redacted))
                {
                    RemoveActivityTag(data, tag.Key);
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
                    if (RedactionFilter.IsSensitiveKey(tag.Key) || !RedactionFilter.TryRedact(tag.Key, tag.Value, out var redacted))
                    {
                        RemoveEventTag(eventTags, tag);
                    }
                    else if (!Equals(redacted, tag.Value))
                    {
                        SetEventTag(eventTags, tag.Key, redacted);
                    }
                }
            }
        }

        // Activity has no public remove for span tags, so the key is removed from the tag list Activity stores internally.
        // The list is located by its Remove method; if none exists the export fails closed rather than keeping the key.
        private static void RemoveActivityTag(Activity activity, string key)
        {
            const System.Reflection.BindingFlags Any =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            foreach (var field in typeof(Activity).GetFields(Any))
            {
                if (field.GetValue(activity) is not { } store)
                {
                    continue;
                }
                var remove = store.GetType().GetMethods(Any)
                    .FirstOrDefault(m => m.Name == "Remove" && m.GetParameters() is [{ ParameterType: var p }] && p == typeof(string));
                if (remove is not null)
                {
                    remove.Invoke(store, [key]);
                    return;
                }
            }
            throw new InvalidOperationException("Activity tag storage has no Remove method; span tags cannot be redacted.");
        }

        // Activity stores event tags in an internal list (TagsLinkedList), not ActivityTagsCollection. The entry is
        // removed through that list's Remove method, which must exist, so a sensitive key is never kept.
        private static void RemoveEventTag(object eventTags, KeyValuePair<string, object?> tag)
        {
            var methods = eventTags.GetType()
                .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .Where(m => m.Name == "Remove" && m.GetParameters().Length == 1)
                .ToList();
            if (methods.FirstOrDefault(m => m.GetParameters()[0].ParameterType == typeof(string)) is { } byKey)
            {
                byKey.Invoke(eventTags, [tag.Key]);
                return;
            }
            var byPair = methods.FirstOrDefault(m => m.GetParameters()[0].ParameterType == typeof(KeyValuePair<string, object?>))
                ?? throw new InvalidOperationException("Activity event tag list has no Remove method; sensitive event attributes cannot be removed.");
            byPair.Invoke(eventTags, [tag]);
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
