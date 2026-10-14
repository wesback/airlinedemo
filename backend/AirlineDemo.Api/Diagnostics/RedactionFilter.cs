using System.Collections;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace AirlineDemo.Api.Diagnostics;

// Applied to every log record (through RedactingLoggerFactory) and every trace attribute (through
// RedactingActivityProcessor) before any provider or exporter sees it.
public static partial class RedactionFilter
{
    private static readonly HashSet<string> SensitiveSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "prompt", "document", "text", "passcode", "password", "cookie", "authorization", "token", "secret",
    };

    public static bool IsSensitiveName(string name) =>
        name.Split(new[] { '.', '_', '-' }).Any(SensitiveSegments.Contains);

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9\-._~+/]+=*")]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"\b[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\b")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?:__Host-|\.AspNetCore\.)[A-Za-z0-9_.-]+=[^;\s]+")]
    private static partial Regex AuthCookie();

    public static string ScrubText(string text) =>
        AuthCookie().Replace(Jwt().Replace(BearerToken().Replace(text, TelemetryRedaction.Redacted), TelemetryRedaction.Redacted), TelemetryRedaction.Redacted);

    // Only the name decides whether a value is dropped; other values are kept except for the listed value shapes.
    public static object? Sanitize(string name, object? value) =>
        IsSensitiveName(name) ? TelemetryRedaction.Redacted : value is string text ? ScrubText(text) : value;

    // The message keeps its text; the values bound to sensitive placeholders are replaced by the redaction marker.
    private static string RenderMessage(string formatted, IEnumerable<KeyValuePair<string, object?>>? pairs)
    {
        var rendered = formatted;
        foreach (var pair in pairs ?? [])
        {
            var raw = pair.Value?.ToString();
            if (IsSensitiveName(pair.Key) && !string.IsNullOrEmpty(raw))
            {
                rendered = rendered.Replace(raw, TelemetryRedaction.Redacted, StringComparison.Ordinal);
            }
        }

        return ScrubText(rendered);
    }

    public static IReadOnlyList<KeyValuePair<string, object?>> Redact(IEnumerable<KeyValuePair<string, object?>> pairs) =>
        pairs.Select(pair => new KeyValuePair<string, object?>(pair.Key, Sanitize(pair.Key, pair.Value))).ToList();

    // Exception messages are free-form and may carry secrets, so only the type, a scrubbed stack trace and the inner chain are kept.
    public static Exception? SanitizeException(Exception? exception) =>
        exception is null ? null : new RedactedException(
            exception.GetType().FullName ?? nameof(Exception),
            exception.StackTrace is null ? null : ScrubText(exception.StackTrace),
            SanitizeException(exception.InnerException));

    internal sealed class RedactedException(string typeName, string? stackTrace, Exception? inner)
        : Exception($"{typeName}: {TelemetryRedaction.Redacted}", inner)
    {
        private readonly string? _stackTrace = stackTrace;

        public override string? StackTrace => _stackTrace;
    }

    internal sealed class RedactedState(IReadOnlyList<KeyValuePair<string, object?>>? pairs, string message)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly IReadOnlyList<KeyValuePair<string, object?>> _pairs = pairs ?? [];

        public string Message { get; } = message;

        public KeyValuePair<string, object?> this[int index] => _pairs[index];

        public int Count => _pairs.Count;

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _pairs.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => Message;
    }

    internal sealed class RedactingLoggerFactory(ILoggerFactory inner) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new RedactingLogger(inner.CreateLogger(categoryName));

        public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

        public void Dispose() => inner.Dispose();
    }

    internal sealed class RedactingLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            object scope = state switch
            {
                IEnumerable<KeyValuePair<string, object?>> pairs => new RedactedState(Redact(pairs), TelemetryRedaction.Redacted),
                string text => ScrubText(text),
                _ => TelemetryRedaction.Redacted,
            };
            return inner.BeginScope(scope);
        }

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!inner.IsEnabled(logLevel))
            {
                return;
            }

            var source = state as IEnumerable<KeyValuePair<string, object?>>;
            var formatted = formatter(state, exception);
            var message = RenderMessage(formatted, source);
            var pairs = source is null ? null : Redact(source);
            inner.Log(logLevel, eventId, new RedactedState(pairs, message), SanitizeException(exception), static (s, _) => s.Message);
        }
    }

    public sealed class RedactingActivityProcessor : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            foreach (var tag in data.TagObjects.ToList())
            {
                var sanitized = Sanitize(tag.Key, tag.Value);
                if (!Equals(sanitized, tag.Value))
                {
                    data.SetTag(tag.Key, sanitized);
                }
            }

            var events = data.Events.ToList();
            if (!events.Any(e => e.Tags.Any(t => !Equals(Sanitize(t.Key, t.Value), t.Value))))
            {
                return;
            }

            // Activity offers no API to change event tags in place, so the events are rebuilt with redacted tags.
            (EventsField ?? throw new InvalidOperationException("Activity event storage is unavailable; refusing to export unredacted events."))
                .SetValue(data, null);
            foreach (var activityEvent in events)
            {
                data.AddEvent(new ActivityEvent(
                    activityEvent.Name,
                    activityEvent.Timestamp,
                    new ActivityTagsCollection(activityEvent.Tags.Select(t => new KeyValuePair<string, object?>(t.Key, Sanitize(t.Key, t.Value))))));
            }
        }

        private static readonly System.Reflection.FieldInfo? EventsField =
            typeof(Activity).GetField("_events", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    }
}
