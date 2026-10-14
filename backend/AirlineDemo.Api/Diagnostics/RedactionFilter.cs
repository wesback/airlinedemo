using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;
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

    // Shape only: three base64url segments of at least 8 characters each, separated by two dots. Nothing is decoded.
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(?![A-Za-z0-9_-])")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?:__Host-|\.AspNetCore\.)[A-Za-z0-9_.-]+=[^;\s]+")]
    private static partial Regex AuthCookie();

    public static string ScrubText(string text) =>
        AuthCookie().Replace(Jwt().Replace(BearerToken().Replace(text, TelemetryRedaction.Redacted), TelemetryRedaction.Redacted), TelemetryRedaction.Redacted);

    // Only the name decides whether a value is dropped; other values are kept except for the listed value shapes.
    public static object? Sanitize(string name, object? value) =>
        IsSensitiveName(name) ? TelemetryRedaction.Redacted : value is string text ? ScrubText(text) : value;

    // Renders the message from already-redacted values, so a sensitive value is never passed to a formatter or format specifier.
    // Literal template text is written by developers and is not scanned.
    private static string RenderTemplate(IReadOnlyList<KeyValuePair<string, object?>> pairs)
    {
        var template = pairs.FirstOrDefault(p => p.Key == "{OriginalFormat}").Value as string;
        if (template is null)
        {
            return string.Join(" ", pairs.Where(p => p.Key != "{OriginalFormat}").Select(p => $"{p.Key}={p.Value}"));
        }

        var rendered = new StringBuilder(template.Length);
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{')
            {
                rendered.Append('{');
                i++;
            }
            else if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                rendered.Append('}');
                i++;
            }
            else if (c == '{')
            {
                var close = template.IndexOf('}', i);
                if (close < 0)
                {
                    rendered.Append(template, i, template.Length - i);
                    break;
                }

                rendered.Append(RenderPlaceholder(template.Substring(i + 1, close - i - 1), pairs));
                i = close;
            }
            else
            {
                rendered.Append(c);
            }
        }

        return rendered.ToString();
    }

    private static string RenderPlaceholder(string inner, IReadOnlyList<KeyValuePair<string, object?>> pairs)
    {
        var specStart = inner.IndexOfAny([',', ':']);
        var name = specStart < 0 ? inner : inner[..specStart];
        var spec = specStart < 0 ? string.Empty : inner[specStart..];
        var pair = pairs.FirstOrDefault(p => p.Key == name);
        if (pair.Key is null)
        {
            return "{" + inner + "}";
        }

        if (IsSensitiveName(name))
        {
            return TelemetryRedaction.Redacted;
        }

        try
        {
            return string.Format(CultureInfo.InvariantCulture, "{0" + spec + "}", pair.Value);
        }
        catch (FormatException)
        {
            return pair.Value?.ToString() ?? string.Empty;
        }
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
            if (source is null)
            {
                var plain = ScrubText(formatter(state, exception));
                inner.Log(logLevel, eventId, new RedactedState(null, plain), SanitizeException(exception), static (s, _) => s.Message);
                return;
            }

            var pairs = Redact(source);
            inner.Log(logLevel, eventId, new RedactedState(pairs, RenderTemplate(pairs)), SanitizeException(exception), static (s, _) => s.Message);
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
