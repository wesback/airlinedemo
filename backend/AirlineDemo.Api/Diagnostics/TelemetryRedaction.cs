using System.Diagnostics.Metrics;
using System.Globalization;
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
    internal const string SourceSystemMeterName = "airline-demo-api.source-systems";

    private static readonly Meter SourceSystemMeter = new(SourceSystemMeterName);
    private static readonly Counter<long> SourceSystemEventCounter =
        SourceSystemMeter.CreateCounter<long>("source.system.events");

    // Only enumerated diagnostic fields are emitted; any other key (for example free-text "detail") is dropped.
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "event.name", "trace.id", "service.name", "service.version", "deployment.environment",
        "investigation.basis_version", "gen_ai.request.model", "investigation.model", "investigation.tools",
        "investigation.citations", "investigation.decision", "enduser.pseudo.id",
        "investigation.outcome", "error.type", "severity.text", "severity.number",
        "source.id", "case.id", "event.outcome", "duration",
    };

    public static class SecurityEvents
    {
        public const string AuthScopeFailure = "security.auth_scope_failure";
        public const string IntegritySignatureFailure = "security.integrity_signature_failure";
        public const string BudgetExhausted = "security.budget_exhausted";
    }

    public static class SourceEvents
    {
        public const string ModeChangeAccepted = "source.mode_change.accepted";
        public const string CrossCaseReadDenied = "source.read.denied_cross_case";
        public const string OutOfRoleReadDenied = "source.read.denied_out_of_role";
        public const string FixtureRejected = "source.fixture.rejected";
        public const string SourceUnavailable = "source.read.unavailable";
        public const string ReadLimited = "source.read.rate_or_concurrency_limited";
        public const string RecordLimitRejected = "source.read.record_limit_rejected";
        public const string ReadTimedOut = "source.read.timed_out";
        public const string AuditWriteFailed = "source.mode_change.audit_write_failed";
    }

    private static readonly HashSet<string> SecurityEventNames = new(StringComparer.Ordinal)
    {
        SecurityEvents.AuthScopeFailure, SecurityEvents.IntegritySignatureFailure, SecurityEvents.BudgetExhausted,
    };

    private static readonly HashSet<string> SourceEventNames = new(StringComparer.Ordinal)
    {
        SourceEvents.ModeChangeAccepted, SourceEvents.CrossCaseReadDenied, SourceEvents.OutOfRoleReadDenied,
        SourceEvents.FixtureRejected, SourceEvents.SourceUnavailable, SourceEvents.ReadLimited,
        SourceEvents.RecordLimitRejected, SourceEvents.ReadTimedOut, SourceEvents.AuditWriteFailed,
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
        string Outcome,
        string? ModelName = null);

    private static readonly HashSet<string> DeniedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "source.raw_record_id", "recordid", "record.id", "payload",
        "prompt", "gen_ai.prompt", "gen_ai.content.prompt",
        "document", "document.text", "document.content", "evidence.text",
        "passcode", "cookie", "set-cookie", "authorization", "token", "access_token",
        "refresh_token", "password", "secret", "email", "user.email", "user.name",
        "phone", "address", "http.request.header.cookie",
        "http.request.header.authorization", "enduser.id", "user.id",
    };

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    // Cookie and Set-Cookie headers carry several pairs separated by ';', so the whole header value is redacted.
    [GeneratedRegex(@"(?i)(?:authorization:\s*(?:[A-Za-z]+\s+)?|bearer\s+|token=|passcode=)\S+|(?:set-)?cookie:[^\r\n]*")]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"[\p{Cc}\p{Cf}]")]
    private static partial Regex ControlPattern();

    private static readonly HashSet<string> AllowedEventNames = new(StringComparer.Ordinal)
    {
        "investigation.completed", "review.completed", SecurityEvents.AuthScopeFailure, SecurityEvents.IntegritySignatureFailure, SecurityEvents.BudgetExhausted,
        SourceEvents.ModeChangeAccepted, SourceEvents.CrossCaseReadDenied, SourceEvents.OutOfRoleReadDenied, SourceEvents.FixtureRejected,
        SourceEvents.SourceUnavailable, SourceEvents.ReadLimited, SourceEvents.RecordLimitRejected, SourceEvents.ReadTimedOut, SourceEvents.AuditWriteFailed,
    };

    // Investigator read tools (CaseReadTools) plus the earlier tool identifiers.
    private static readonly HashSet<string> AllowedTools = new(StringComparer.Ordinal)
    {
        "reserve-calc", "citation-search",
        "get_case_inventory", "get_approved_requirements", "get_source_page", "search_within_basis", "get_processing_status",
    };

    private static readonly HashSet<string> AllowedOutcomes = new(StringComparer.Ordinal) { "approved", "rejected", "escalated" };

    private static readonly HashSet<string> SourceSpecificAttributeNames = new(StringComparer.Ordinal)
    {
        "source.id", "case.id", "event.outcome", "duration",
    };

    private static readonly HashSet<string> AllowedSourceIds = new(StringComparer.Ordinal)
    {
        "technical", "maintenance", "finance", "records", "contracts", "source-fixture-pack",
    };
    private static readonly HashSet<string> AllowedSourceOutcomes = new(StringComparer.Ordinal)
    {
        "accepted", "denied", "failed", "limited", "rejected", "timed_out", "unavailable",
    };
    private static readonly Regex TraceIdRule = new(@"^trace-[0-9a-f]{4,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex BasisRule = new(@"^basis-v[0-9]{1,4}$", RegexOptions.CultureInvariant);
    private static readonly Regex DeploymentRule = new(@"^[a-z0-9][a-z0-9.\-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex CitationRule = new(@"^doc-[0-9]{1,6}#p[0-9]{1,4}$", RegexOptions.CultureInvariant);
    private static readonly Regex ActorRule = new(@"^actor-[0-9a-f]{16}$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionRule = new(@"^[0-9]+\.[0-9]+\.[0-9]+([\-+][0-9A-Za-z.\-]+)?$", RegexOptions.CultureInvariant);
    private static readonly Regex EnvironmentRule = new(@"^[a-z][a-z0-9\-]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex ErrorTypeRule = new(@"^[a-z][a-z0-9.\-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex CaseIdRule = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);

    public static bool IsAllowedErrorType(string value) => ErrorTypeRule.IsMatch(value);

    internal static bool IsSourceSpecificAttribute(string key) =>
        key.StartsWith("source.", StringComparison.OrdinalIgnoreCase)
        || key.Equals("case.id", StringComparison.OrdinalIgnoreCase)
        || key.Equals("event.outcome", StringComparison.OrdinalIgnoreCase)
        || key.Equals("duration", StringComparison.OrdinalIgnoreCase);

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
        ["investigation.model"] = v => DeploymentRule.IsMatch(v) || v == "unknown",
        ["investigation.tools"] = v => v.Length == 0 || v.Split(',').All(t => AllowedTools.Contains(t)),
        ["investigation.citations"] = v => v.Length == 0 || v.Split(',').All(c => CitationRule.IsMatch(c)),
        ["investigation.decision"] = v => AllowedDecisions.Contains(v),
        ["enduser.pseudo.id"] = v => ActorRule.IsMatch(v),
        ["investigation.outcome"] = v => AllowedOutcomes.Contains(v),
        ["error.type"] = v => ErrorTypeRule.IsMatch(v),
        ["severity.text"] = v => v is "INFO" or "WARN" or "ERROR",
        ["severity.number"] = v => v is "9" or "13" or "17",
        ["source.id"] = v => AllowedSourceIds.Contains(v),
        ["case.id"] = v => CaseIdRule.IsMatch(v),
        ["event.outcome"] = v => AllowedSourceOutcomes.Contains(v),
        ["duration"] = v => long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var duration) && duration >= 0,
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
        InvestigationTraceRecord trace, string serviceVersion, string environment, byte[] actorKey, string eventName = "investigation.completed")
    {
        if (!AllowedDecisions.Contains(trace.Decision))
        {
            throw new ArgumentException("Decision must be an enumerated decision identifier.", nameof(trace));
        }

        var attributes = Redact(new KeyValuePair<string, object?>[]
        {
            new("event.name", eventName),
            new("trace.id", trace.TraceId),
            new("investigation.basis_version", trace.BasisVersion),
            new("gen_ai.request.model", trace.ModelDeployment),
            new("investigation.model", trace.ModelName ?? "unknown"),
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
        ILogger logger, InvestigationTraceRecord trace, string serviceVersion, string environment, byte[] actorKey, string eventName = "investigation.completed") =>
        Emit(logger, LogLevel.Information, InvestigationTrace(trace, serviceVersion, environment, actorKey, eventName));

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

    public static void EmitSourceSystemEvent(
        ILogger logger,
        string eventName,
        LogLevel level,
        string environment,
        string? sourceId = null,
        string? caseId = null,
        string? outcome = null,
        long? durationMilliseconds = null)
    {
        if (!SourceEventNames.Contains(eventName))
        {
            throw new ArgumentException("Unknown source-system event name.", nameof(eventName));
        }

        if (level is not (LogLevel.Information or LogLevel.Warning or LogLevel.Error))
        {
            throw new ArgumentOutOfRangeException(nameof(level), "Source-system events must use an approved severity.");
        }

        var hasSourceContext = sourceId is not null || caseId is not null || outcome is not null || durationMilliseconds is not null;
        if (hasSourceContext && (sourceId is null || caseId is null || outcome is null || durationMilliseconds is null))
        {
            throw new ArgumentException("Source-system context must include source, case, outcome and duration.");
        }
        if (durationMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds), "Duration must be nonnegative.");
        }

        var sourceEventAttributes = new List<KeyValuePair<string, object?>>
        {
            new("event.name", eventName),
        };
        if (hasSourceContext)
        {
            sourceEventAttributes.Add(new("source.id", sourceId));
            sourceEventAttributes.Add(new("case.id", caseId));
            sourceEventAttributes.Add(new("event.outcome", outcome));
            sourceEventAttributes.Add(new("duration", durationMilliseconds));
        }
        var attributes = Redact(sourceEventAttributes);
        var merged = attributes.ToDictionary(attribute => attribute.Key, attribute => (object?)attribute.Value, StringComparer.Ordinal);
        foreach (var (key, value) in ResourceAttributes(ServiceVersion, environment.ToLowerInvariant()))
        {
            merged[key] = value;
        }
        var severity = level switch
        {
            LogLevel.Information => ("INFO", 9),
            LogLevel.Warning => ("WARN", 13),
            LogLevel.Error => ("ERROR", 17),
            _ => throw new ArgumentOutOfRangeException(nameof(level)),
        };
        SourceSystemEventCounter.Add(1);
        if (durationMilliseconds is not null && merged.ContainsKey("duration"))
        {
            merged["duration"] = durationMilliseconds.Value;
        }
        Emit(logger, level, merged, severity.Item1, severity.Item2);
    }

    private static void Emit(ILogger logger, LogLevel level, IReadOnlyDictionary<string, string> attributes)
        => Emit(logger, level, attributes.ToDictionary(attribute => attribute.Key, attribute => (object?)attribute.Value, StringComparer.Ordinal), null, null);

    private static void Emit(
        ILogger logger,
        LogLevel level,
        IReadOnlyDictionary<string, string> attributes,
        string? severityText,
        int? severityNumber)
        => Emit(logger, level, attributes.ToDictionary(attribute => attribute.Key, attribute => (object?)attribute.Value, StringComparer.Ordinal), severityText, severityNumber);

    private static void Emit(
        ILogger logger,
        LogLevel level,
        IReadOnlyDictionary<string, object?> attributes,
        string? severityText,
        int? severityNumber)
    {
        var state = attributes.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)).ToList();
        if (severityText is not null && severityNumber is not null)
        {
            state.Add(new KeyValuePair<string, object?>("severity.text", severityText));
            state.Add(new KeyValuePair<string, object?>("severity.number", severityNumber.Value));
        }
        var eventName = attributes.TryGetValue("event.name", out var name)
            ? Convert.ToString(name, CultureInfo.InvariantCulture) ?? "telemetry"
            : "telemetry";
        logger.Log(level, new EventId(0, eventName), state, null,
            static (s, _) => string.Join(" ", s.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    public static string ServiceVersion => typeof(TelemetryRedaction).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static IReadOnlySet<string> ReadApprovedSourceAttributes(string approvalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalPath);
        var approved = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(approvalPath))
        {
            return approved;
        }

        var inSourceAttributes = false;
        foreach (var rawLine in File.ReadLines(approvalPath))
        {
            var line = rawLine.Trim();
            var isHeading = line.StartsWith('#');
            if (isHeading)
            {
                line = line.TrimStart('#').Trim().TrimEnd('#').Trim();
            }

            if (line == "source-specific-attributes")
            {
                inSourceAttributes = true;
                continue;
            }

            if (line == "metric-dimensions")
            {
                break;
            }

            if (isHeading)
            {
                inSourceAttributes = false;
                continue;
            }

            if (!inSourceAttributes)
            {
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                line = line[2..].Trim();
            }

            if (SourceSpecificAttributeNames.Contains(line))
            {
                approved.Add(line);
            }
        }

        return approved;
    }

    public static IReadOnlyDictionary<string, string> ResourceAttributes(string serviceVersion, string environment) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["service.name"] = ServiceName,
            ["service.version"] = VersionRule.IsMatch(serviceVersion) ? serviceVersion : Redacted,
            ["deployment.environment"] = EnvironmentRule.IsMatch(environment) ? environment : Redacted,
        };
}
