using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AirlineDemo.Api.Diagnostics;

// Diagnostics only. Business audit is written to the SQL audit tables, never through this helper.
public static partial class TelemetryRedaction
{
    public const string ServiceName = "airline-demo-api";
    public const string Redacted = "[redacted]";

    private static readonly HashSet<string> DeniedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "prompt", "gen_ai.prompt", "gen_ai.content.prompt",
        "document", "document.text", "document.content", "evidence.text",
        "passcode", "cookie", "set-cookie", "authorization", "token", "access_token",
        "refresh_token", "password", "secret", "email", "user.email", "user.name",
        "phone", "address", "http.request.header.cookie",
        "http.request.header.authorization", "enduser.id", "user.id",
    };

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?i)(bearer\s+|token=|passcode=|cookie:\s*|authorization:\s*)\S+")]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"[\p{Cc}\p{Cf}]")]
    private static partial Regex ControlPattern();

    // Drops denylisted keys and scrubs secret-like and personal values. Control characters are
    // replaced so injected log content cannot forge new lines or fields.
    public static IReadOnlyDictionary<string, string> Redact(IEnumerable<KeyValuePair<string, object?>> attributes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
        {
            if (DeniedKeys.Contains(key))
            {
                continue;
            }

            result[key] = Sanitize(value?.ToString() ?? string.Empty);
        }

        return result;
    }

    public static string Sanitize(string value)
    {
        var scrubbed = ControlPattern().Replace(value, " ");
        scrubbed = SecretPattern().Replace(scrubbed, Redacted);
        return EmailPattern().Replace(scrubbed, Redacted);
    }

    // Keyed, opaque actor identifier: traces correlate by actor without exposing the real identity.
    public static string OpaqueActor(string actorId, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(actorId));
        return "actor-" + Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
    }

    public static IReadOnlyDictionary<string, string> ResourceAttributes(string serviceVersion, string environment) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["service.name"] = ServiceName,
            ["service.version"] = serviceVersion,
            ["deployment.environment"] = environment,
        };
}
