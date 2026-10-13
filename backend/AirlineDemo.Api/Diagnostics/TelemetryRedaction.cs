using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AirlineDemo.Api.Diagnostics;

// Diagnostics only. Business audit is written to the SQL audit tables, never through this helper.
public static partial class TelemetryRedaction
{
    public const string ServiceName = "airline-demo-api";
    public const string Redacted = "[redacted]";

    // Only enumerated diagnostic fields are emitted; any other key (for example free-text "detail") is dropped.
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "event.name", "trace.id", "service.name", "service.version", "deployment.environment",
        "investigation.basis_version", "gen_ai.request.model", "investigation.tools",
        "investigation.citations", "investigation.decision", "enduser.pseudo.id",
        "investigation.outcome", "error.type",
    };

    public static class SecurityEvents
    {
        public const string AuthScopeFailure = "security.auth_scope_failure";
        public const string IntegritySignatureFailure = "security.integrity_signature_failure";
        public const string BudgetExhausted = "security.budget_exhausted";
    }

    private static readonly HashSet<string> SecurityEventNames = new(StringComparer.Ordinal)
    {
        SecurityEvents.AuthScopeFailure, SecurityEvents.IntegritySignatureFailure, SecurityEvents.BudgetExhausted,
    };

    // Decisions are enumerated identifiers, never free text.
    private static readonly HashSet<string> AllowedDecisions = new(StringComparer.Ordinal)
    {
        "return-ready", "return-not-ready", "escalate",
    };

    public sealed record InvestigationTraceRecord(
        string TraceId,
        string BasisVersion,
        string ModelDeployment,
        IReadOnlyList<string> Tools,
        IReadOnlyList<string> CitationIds,
        string Decision,
        string ActorId,
        string Outcome);

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

    [GeneratedRegex(@"(?i)(authorization:\s*(?:[A-Za-z]+\s+)?|bearer\s+|token=|passcode=|cookie:\s*|set-cookie:\s*)\S+")]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"[\p{Cc}\p{Cf}]")]
    private static partial Regex ControlPattern();

    private static readonly HashSet<string> AllowedEventNames = new(StringComparer.Ordinal)
    {
        "investigation.completed", SecurityEvents.AuthScopeFailure, SecurityEvents.IntegritySignatureFailure, SecurityEvents.BudgetExhausted,
    };

    private static readonly HashSet<string> AllowedTools = new(StringComparer.Ordinal) { "reserve-calc", "citation-search" };

    private static readonly HashSet<string> AllowedOutcomes = new(StringComparer.Ordinal) { "approved", "rejected", "escalated" };

    private static readonly Regex TraceIdRule = new(@"^trace-[0-9a-f]{4,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex BasisRule = new(@"^basis-v[0-9]{1,4}$", RegexOptions.CultureInvariant);
    private static readonly Regex DeploymentRule = new(@"^[a-z0-9][a-z0-9.\-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex CitationRule = new(@"^doc-[0-9]{1,6}#p[0-9]{1,4}$", RegexOptions.CultureInvariant);
    private static readonly Regex ActorRule = new(@"^actor-[0-9a-f]{16}$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionRule = new(@"^[0-9]+\.[0-9]+\.[0-9]+([\-+][0-9A-Za-z.\-]+)?$", RegexOptions.CultureInvariant);
    private static readonly Regex EnvironmentRule = new(@"^[a-z][a-z0-9\-]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex ErrorTypeRule = new(@"^[a-z][a-z0-9.\-]{0,63}$", RegexOptions.CultureInvariant);

    // Each allowlisted key has a rule that accepts only its enumerated or structured identifier form.
    // Free text, and identifier-shaped content such as canaries, fail the rule and are replaced with Redacted.
    private static readonly Dictionary<string, Func<string, bool>> ValueRules = new(StringComparer.Ordinal)
    {
        ["event.name"] = v => AllowedEventNames.Contains(v),
        ["service.name"] = v => v == ServiceName,
        ["service.version"] = v => VersionRule.IsMatch(v),
        ["deployment.environment"] = v => EnvironmentRule.IsMatch(v),
        ["trace.id"] = v => TraceIdRule.IsMatch(v),
        ["investigation.basis_version"] = v => BasisRule.IsMatch(v),
        ["gen_ai.request.model"] = v => DeploymentRule.IsMatch(v),
        ["investigation.tools"] = v => v.Length == 0 || v.Split(',').All(t => AllowedTools.Contains(t)),
        ["investigation.citations"] = v => v.Length == 0 || v.Split(',').All(c => CitationRule.IsMatch(c)),
        ["investigation.decision"] = v => AllowedDecisions.Contains(v),
        ["enduser.pseudo.id"] = v => ActorRule.IsMatch(v),
        ["investigation.outcome"] = v => AllowedOutcomes.Contains(v),
        ["error.type"] = v => ErrorTypeRule.IsMatch(v),
    };

    // Keeps only allowlisted keys, drops denylisted keys and replaces values that fail their key's rule.
    // Control characters are replaced so injected log content cannot forge new lines or fields.
    public static IReadOnlyDictionary<string, string> Redact(IEnumerable<KeyValuePair<string, object?>> attributes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
        {
            if (DeniedKeys.Contains(key) || !AllowedKeys.Contains(key))
            {
                continue;
            }

            var text = value?.ToString() ?? string.Empty;
            result[key] = ValueRules.TryGetValue(key, out var accepts) && accepts(text) ? text : Redacted;
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

    // M-08: one investigation trace correlating basis, model, tools, citations, decision, actor and outcome.
    // Business content stays in the SQL audit tables; this carries only identifiers and opaque actors.
    public static IReadOnlyDictionary<string, string> InvestigationTrace(
        InvestigationTraceRecord trace, string serviceVersion, string environment, byte[] actorKey)
    {
        if (!AllowedDecisions.Contains(trace.Decision))
        {
            throw new ArgumentException("Decision must be an enumerated decision identifier.", nameof(trace));
        }

        var attributes = Redact(new KeyValuePair<string, object?>[]
        {
            new("event.name", "investigation.completed"),
            new("trace.id", trace.TraceId),
            new("investigation.basis_version", trace.BasisVersion),
            new("gen_ai.request.model", trace.ModelDeployment),
            new("investigation.tools", string.Join(",", trace.Tools)),
            new("investigation.citations", string.Join(",", trace.CitationIds)),
            new("investigation.decision", trace.Decision),
            new("enduser.pseudo.id", OpaqueActor(trace.ActorId, actorKey)),
            new("investigation.outcome", trace.Outcome),
        });

        var merged = new Dictionary<string, string>(attributes, StringComparer.Ordinal);
        foreach (var (key, value) in ResourceAttributes(serviceVersion, environment))
        {
            merged[key] = value;
        }

        return merged;
    }

    // Emission path: sanitized attributes become structured log properties (AppTraces Properties in Log Analytics).
    public static void EmitInvestigationTrace(
        ILogger logger, InvestigationTraceRecord trace, string serviceVersion, string environment, byte[] actorKey) =>
        Emit(logger, LogLevel.Information, InvestigationTrace(trace, serviceVersion, environment, actorKey));

    public static void EmitSecurityEvent(ILogger logger, string eventName, string serviceVersion, string environment)
    {
        if (!SecurityEventNames.Contains(eventName))
        {
            throw new ArgumentException("Unknown security event name.", nameof(eventName));
        }

        var attributes = new Dictionary<string, string>(ResourceAttributes(serviceVersion, environment), StringComparer.Ordinal)
        {
            ["event.name"] = eventName,
        };
        Emit(logger, LogLevel.Warning, attributes);
    }

    private static void Emit(ILogger logger, LogLevel level, IReadOnlyDictionary<string, string> attributes)
    {
        var state = attributes.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)).ToList();
        var eventName = attributes.TryGetValue("event.name", out var name) ? name : "telemetry";
        logger.Log(level, new EventId(0, eventName), state, null,
            static (s, _) => string.Join(" ", s.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    public static string ServiceVersion => typeof(TelemetryRedaction).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static IReadOnlyDictionary<string, string> ResourceAttributes(string serviceVersion, string environment) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["service.name"] = ServiceName,
            ["service.version"] = serviceVersion,
            ["deployment.environment"] = environment,
        };
}
