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

    // Strings, numbers and flags survive only under operational names; strings must also be single short tokens.
    public static object? Sanitize(string name, object? value)
    {
        if (IsSensitiveName(name))
        {
            return TelemetryRedaction.Redacted;
        }

        return value switch
        {
            null => null,
            ValueType when IsOperationalName(name) => value,
            string text when IsOperationalName(name) => FreeText(text),
            _ => TelemetryRedaction.Redacted,
        };
    }

    private static string FreeText(string text) =>
        text.Length > MaxFreeTextLength || text.Any(char.IsWhiteSpace) ? TelemetryRedaction.Redacted : ScrubText(text);

    // Literal text without placeholders is kept only as a single token made of operational words.
    private static string LiteralText(string text) =>
        text.Length is > 0 and <= MaxFreeTextLength && !text.Any(char.IsWhiteSpace)
            && text.Split(new[] { '.', '_', '-' }).All(OperationalSegments.Contains)
            ? ScrubText(text)
            : TelemetryRedaction.Redacted;

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^\{[@$]?[A-Za-z_][A-Za-z0-9_]*(?:[,:][^{}]*)?\}$")]
    private static partial Regex ValidPlaceholder();

    // A template is trusted only when it is short and every brace is part of a well-formed placeholder;
    // an interpolated string that carries content fails this.
    private static bool IsTrustedTemplate(string template, IEnumerable<KeyValuePair<string, object?>>? pairs) =>
        template.Length <= MaxFreeTextLength
        && Placeholder().Matches(template).All(match => ValidPlaceholder().IsMatch(match.Value))
        && Placeholder().Matches(template).All(match => HasValue(match.Value, pairs))
        && !Placeholder().Replace(template, string.Empty).Any(c => c is '{' or '}');

    // A placeholder without a bound value means the content was interpolated into the template text.
    private static bool HasValue(string placeholder, IEnumerable<KeyValuePair<string, object?>>? pairs)
    {
        var name = placeholder.Trim('{', '}').Split(',', ':')[0].TrimStart('@', '$');
        return pairs?.Any(pair => pair.Key == name) == true;
    }

    // Messages are only trusted as message templates: placeholder values are redacted and literal text is never free-form.
    private static string RenderMessage(string formatted, string? template, IEnumerable<KeyValuePair<string, object?>>? pairs)
    {
        if (template is null)
        {
            return TelemetryRedaction.Redacted;
        }

        if (!template.Contains('{'))
        {
            return LiteralText(template);
        }

        if (!IsTrustedTemplate(template, pairs))
        {
            return TelemetryRedaction.Redacted;
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
                string text => LiteralText(text),
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
