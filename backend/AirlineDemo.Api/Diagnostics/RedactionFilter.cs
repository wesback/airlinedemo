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
        "source.raw_record_id", "recordid", "record.id", "payload",
        "prompt", "gen_ai.prompt", "gen_ai.content.prompt",
        "document", "document.text", "document.content", "evidence.text",
        "passcode", "cookie", "set-cookie", "authorization", "token", "access_token",
        "refresh_token", "password", "secret",
        "email", "user.email", "user.name", "phone", "address", "enduser.id", "user.id",
        "http.request.header.cookie", "http.request.header.authorization",
    };

    // Cookie pairs whose names start with __Host- or .AspNetCore.; the whole name=value pair is redacted.
    [GeneratedRegex(@"(?:__Host-|\.AspNetCore\.)[^=;\s]*=[^;\s]*")]
    private static partial Regex SensitiveCookiePattern();

    [GeneratedRegex(@"(?i)\bbearer\s+\S+")]
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

        var withoutCookies = SensitiveCookiePattern().Replace(value, Redacted);
        var scrubbed = BearerPattern().Replace(withoutCookies, "bearer " + Redacted);
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
        IReadOnlyList<KeyValuePair<string, object?>>? attributes,
        IReadOnlySet<string>? approvedSourceAttributes = null)
    {
        if (attributes is null)
        {
            return Array.Empty<KeyValuePair<string, object?>>();
        }

        var result = new List<KeyValuePair<string, object?>>(attributes.Count);
        foreach (var (key, value) in attributes)
        {
            if (TelemetryRedaction.IsSourceSpecificAttribute(key)
                && approvedSourceAttributes is not null
                && !approvedSourceAttributes.Contains(key))
            {
                continue;
            }

            // Log properties and scopes with a sensitive name are dropped, not kept as placeholders.
            if (IsSensitiveKey(key))
            {
                continue;
            }

            if (TryRedact(key, value, out var redacted))
            {
                result.Add(new KeyValuePair<string, object?>(key, redacted));
            }
        }

        return result;
    }

    // Scalars are scrubbed by their string form. Other objects are omitted (returns false) because their
    // ToString() cannot be proven safe.
    public static bool TryRedact(string key, object? value, out object? redacted)
    {
        if (IsSensitiveKey(key))
        {
            redacted = Redacted;
            return true;
        }

        // error.type is an identifier, so it must pass the allowlist rule wherever it appears (properties, scopes, tags); otherwise it is omitted.
        if (key == "error.type"
            && !TelemetryRedaction.IsAllowedErrorType(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty))
        {
            redacted = null;
            return false;
        }

        switch (value)
        {
            case null:
                redacted = null;
                return true;
            case string text:
                redacted = RedactValue(key, text);
                return true;
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                redacted = value;
                return true;
            case char or Enum or Guid or DateTime or DateTimeOffset or TimeSpan:
                redacted = RedactValue(key, Convert.ToString(value, CultureInfo.InvariantCulture));
                return true;
            default:
                redacted = null;
                return false;
        }
    }
}
