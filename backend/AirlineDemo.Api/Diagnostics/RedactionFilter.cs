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

    // Only these names may carry a string value: identifiers, states, counts, durations and token usage.
    private static readonly HashSet<string> OperationalSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "ids", "state", "status", "count", "counts", "citations", "duration", "ms", "usage", "tokens", "total",
        "decision", "revision", "kind", "code", "outcome", "event", "type", "model", "trace", "span", "route", "level",
        "operation", "attempts", "latency", "version", "service", "name", "environment", "deployment", "span",
    };

    public static bool IsSensitiveName(string name) =>
        name.Split(new[] { '.', '_', '-' }).Any(SensitiveSegments.Contains);

    public static bool IsOperationalName(string name) =>
        name.Split(new[] { '.', '_', '-' }).Any(OperationalSegments.Contains);

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9\-._~+/]+=*")]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"(?i)(?:\b__session|\bsession|\bsid|\.AspNetCore\.[A-Za-z]+)=[^;\s]+")]
    private static partial Regex SessionCookie();

    public static string ScrubText(string text) =>
        SessionCookie().Replace(BearerToken().Replace(text, TelemetryRedaction.Redacted), TelemetryRedaction.Redacted);

    // Free-form content (prompts, document text) is long or contains spaces; identifiers, states and counts are single tokens.
    public const int MaxFreeTextLength = 256;

    // Strings survive only under operational names and only as single short tokens; numbers and flags are always operational.
    public static object? Sanitize(string name, object? value)
    {
        if (IsSensitiveName(name))
        {
            return TelemetryRedaction.Redacted;
        }

        return value switch
        {
            null => null,
            ValueType => value,
            string text when IsOperationalName(name) => FreeText(text),
            _ => TelemetryRedaction.Redacted,
        };
    }

    private static string FreeText(string text) =>
        text.Length > MaxFreeTextLength || text.Any(char.IsWhiteSpace) ? TelemetryRedaction.Redacted : ScrubText(text);

    // Messages are only trusted as message templates: placeholder values are redacted, and literal text must be a single token.
    private static string RenderMessage(string formatted, string? template, IEnumerable<KeyValuePair<string, object?>>? pairs)
    {
        if (template is null)
        {
            return TelemetryRedaction.Redacted;
        }

        if (!template.Contains('{'))
        {
            return FreeText(template);
        }

        var rendered = formatted;
        foreach (var pair in pairs ?? [])
        {
            if (pair.Key == "{OriginalFormat}")
            {
                continue;
            }

            var raw = pair.Value?.ToString();
            var sanitized = Sanitize(pair.Key, pair.Value)?.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(raw) && raw != sanitized)
            {
                rendered = rendered.Replace(raw, sanitized, StringComparison.Ordinal);
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
                string text => FreeText(text),
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
            var template = source?.FirstOrDefault(pair => pair.Key == "{OriginalFormat}").Value as string;
            var message = RenderMessage(formatted, template, source);
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
        }
    }
}
