using System.Globalization;
using System.Text.RegularExpressions;

namespace AirlineDemo.Api.Diagnostics;

// Structured-value redaction for log properties/scopes and trace tags. Applied to property values,
// never to rendered message text. Keys are matched by name; values are scrubbed by shape only.
public static partial class RedactionFilter
{
    public const string Redacted = "[redacted]";

    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "prompt", "gen_ai.prompt", "gen_ai.content.prompt",
        "document", "document.text", "document.content", "evidence.text",
        "passcode", "cookie", "set-cookie", "authorization", "token", "access_token",
        "refresh_token", "password", "secret",
        "http.request.header.cookie", "http.request.header.authorization",
    };

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9\-._~+/]+=*")]
    private static partial Regex BearerPattern();

    // Shape only: three base64url segments of 8+ characters separated by two dots. No header decoding.
    [GeneratedRegex(@"[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}")]
    private static partial Regex JwtPattern();

    public static bool IsSensitiveKey(string key) =>
        SensitiveKeys.Contains(key)
        || key.StartsWith("__Host-", StringComparison.Ordinal)
        || key.StartsWith(".AspNetCore.", StringComparison.Ordinal);

    public static string? RedactValue(string key, string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (IsSensitiveKey(key))
        {
            return Redacted;
        }

        var scrubbed = BearerPattern().Replace(value, "bearer " + Redacted);
        return JwtPattern().Replace(scrubbed, Redacted);
    }

    [GeneratedRegex(@"\{([A-Za-z0-9_.\-]+)(?:[,:][^}]*)?\}")]
    private static partial Regex PlaceholderPattern();

    // Renders a message template from already-redacted attribute values, so the text never holds the raw values.
    public static string? RenderMessage(string? template, IReadOnlyList<KeyValuePair<string, object?>> attributes)
    {
        if (template is null)
        {
            return null;
        }

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in attributes)
        {
            values[key] = value;
        }

        return PlaceholderPattern().Replace(template, match =>
            values.TryGetValue(match.Groups[1].Value, out var value)
                ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
                : match.Value);
    }

    public static IReadOnlyList<KeyValuePair<string, object?>> RedactAttributes(
        IReadOnlyList<KeyValuePair<string, object?>>? attributes)
    {
        if (attributes is null)
        {
            return Array.Empty<KeyValuePair<string, object?>>();
        }

        var result = new List<KeyValuePair<string, object?>>(attributes.Count);
        foreach (var (key, value) in attributes)
        {
            result.Add(new KeyValuePair<string, object?>(key, value is string text ? RedactValue(key, text) : IsSensitiveKey(key) ? Redacted : value));
        }

        return result;
    }
}
