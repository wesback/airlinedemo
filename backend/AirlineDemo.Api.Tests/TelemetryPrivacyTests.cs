using System.Text;
using AirlineDemo.Api.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AirlineDemo.Api.Tests;

public sealed class TelemetryPrivacyTests
{
    private static readonly byte[] TestKey = Encoding.UTF8.GetBytes("synthetic-test-key-not-a-secret");

    private static readonly string[] Canaries =
    [
        "CANARY-PROMPT-7f3a", "CANARY-DOC-9b21", "CANARY-PASSCODE-4d10",
        "CANARY-COOKIE-2e88", "CANARY-TOKEN-c6f0", "canary.person@example.invalid",
    ];

    [Theory]
    [InlineData("prompt")]
    [InlineData("gen_ai.prompt")]
    [InlineData("document.text")]
    [InlineData("passcode")]
    [InlineData("cookie")]
    [InlineData("authorization")]
    [InlineData("token")]
    [InlineData("user.email")]
    [InlineData("PASSCODE")]
    public void Denylisted_keys_are_dropped_regardless_of_case(string key)
    {
        var redacted = TelemetryRedaction.Redact(new[] { new KeyValuePair<string, object?>(key, "value") });

        Assert.DoesNotContain(key, redacted.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Canaries_never_appear_in_any_output()
    {
        var redacted = TelemetryRedaction.Redact(new[]
        {
            new KeyValuePair<string, object?>("prompt", Canaries[0]),
            new KeyValuePair<string, object?>("document.text", Canaries[1]),
            new KeyValuePair<string, object?>("passcode", Canaries[2]),
            new KeyValuePair<string, object?>("cookie", Canaries[3]),
            new KeyValuePair<string, object?>("token", Canaries[4]),
            new KeyValuePair<string, object?>("user.email", Canaries[5]),
            new KeyValuePair<string, object?>("investigation.decision", $"cookie: {Canaries[3]} token={Canaries[4]} owner {Canaries[5]}"),
        });

        var output = string.Join("|", redacted.Values);
        Assert.Single(redacted);
        foreach (var canary in Canaries)
        {
            Assert.DoesNotContain(canary, output);
        }

        Assert.Contains(TelemetryRedaction.Redacted, output);
    }

    [Theory]
    [InlineData("detail")]
    [InlineData("message")]
    [InlineData("gen_ai.prompt.text")]
    public void Unlisted_keys_are_dropped_even_when_value_is_canary(string key)
    {
        var redacted = TelemetryRedaction.Redact(new[] { new KeyValuePair<string, object?>(key, Canaries[0]) });

        Assert.Empty(redacted);
    }

    [Theory]
    [InlineData("Authorization: Bearer CANARY-BEARER-5a1e")]
    [InlineData("authorization: Basic CANARY-BEARER-5a1e")]
    [InlineData("set-cookie: session=CANARY-BEARER-5a1e")]
    public void Authorization_and_cookie_headers_redact_the_credential(string input)
    {
        var sanitized = TelemetryRedaction.Sanitize(input);

        Assert.DoesNotContain("CANARY-BEARER-5a1e", sanitized);
        Assert.Contains(TelemetryRedaction.Redacted, sanitized);
    }

    [Fact]
    public void Investigation_trace_correlates_required_fields_without_raw_content()
    {
        var trace = new TelemetryRedaction.InvestigationTraceRecord(
            TraceId: "trace-0001",
            BasisVersion: "basis-v3",
            ModelDeployment: "gpt-demo-deployment",
            Tools: new[] { "reserve-calc", "citation-search" },
            CitationIds: new[] { "doc-12#p4", "doc-13#p1" },
            Decision: "return-ready",
            ActorId: "synthetic-analyst-01",
            Outcome: "approved");

        var attributes = TelemetryRedaction.InvestigationTrace(trace, "1.2.3", "demo", TestKey);

        Assert.Equal("trace-0001", attributes["trace.id"]);
        Assert.Equal("basis-v3", attributes["investigation.basis_version"]);
        Assert.Equal("gpt-demo-deployment", attributes["gen_ai.request.model"]);
        Assert.Equal("reserve-calc,citation-search", attributes["investigation.tools"]);
        Assert.Equal("doc-12#p4,doc-13#p1", attributes["investigation.citations"]);
        Assert.Equal("return-ready", attributes["investigation.decision"]);
        Assert.Equal("approved", attributes["investigation.outcome"]);
        Assert.Equal(TelemetryRedaction.OpaqueActor("synthetic-analyst-01", TestKey), attributes["enduser.pseudo.id"]);
        Assert.Equal(TelemetryRedaction.ServiceName, attributes["service.name"]);
        Assert.Equal("1.2.3", attributes["service.version"]);
        Assert.Equal("demo", attributes["deployment.environment"]);
        Assert.DoesNotContain("synthetic-analyst-01", string.Join("|", attributes.Values));
    }

    [Fact]
    public void Security_event_names_match_alert_contract()
    {
        Assert.Equal("security.auth_scope_failure", TelemetryRedaction.SecurityEvents.AuthScopeFailure);
        Assert.Equal("security.integrity_signature_failure", TelemetryRedaction.SecurityEvents.IntegritySignatureFailure);
        Assert.Equal("security.budget_exhausted", TelemetryRedaction.SecurityEvents.BudgetExhausted);
    }

    [Fact]
    public void Injected_control_characters_cannot_forge_lines_or_fields()
    {
        var sanitized = TelemetryRedaction.Sanitize("ok\r\nsecurity.auth_scope_failure=forged\u202Ehidden\u0000");

        foreach (var control in new[] { '\r', '\n', '\u202E', '\u0000' })
        {
            Assert.DoesNotContain(control, sanitized);
        }
    }

    [Fact]
    public void Opaque_actor_is_stable_keyed_and_hides_identity()
    {
        var first = TelemetryRedaction.OpaqueActor("synthetic-analyst-01", TestKey);

        Assert.Equal(first, TelemetryRedaction.OpaqueActor("synthetic-analyst-01", TestKey));
        Assert.NotEqual(first, TelemetryRedaction.OpaqueActor("synthetic-analyst-02", TestKey));
        Assert.NotEqual(first, TelemetryRedaction.OpaqueActor("synthetic-analyst-01", Encoding.UTF8.GetBytes("other-key")));
        Assert.StartsWith("actor-", first);
        Assert.DoesNotContain("analyst", first);
    }

    [Fact]
    public void Resource_attributes_include_required_service_fields()
    {
        var resource = TelemetryRedaction.ResourceAttributes("1.2.3", "demo");

        Assert.Equal(TelemetryRedaction.ServiceName, resource["service.name"]);
        Assert.Equal("1.2.3", resource["service.version"]);
        Assert.Equal("demo", resource["deployment.environment"]);
    }

    [Fact]
    public void Free_text_decision_is_rejected_before_emission()
    {
        var trace = new TelemetryRedaction.InvestigationTraceRecord(
            "trace-0002", "basis-v3", "gpt-demo-deployment", new[] { "reserve-calc" }, new[] { "doc-12#p4" },
            Decision: $"Passenger note {Canaries[0]} with prompt text",
            ActorId: "synthetic-analyst-01", Outcome: "approved");

        var logger = new CapturingLogger();

        Assert.Throws<ArgumentException>(() =>
            TelemetryRedaction.EmitInvestigationTrace(logger, trace, "1.2.3", "demo", TestKey));
        Assert.Empty(logger.States);
    }

    [Fact]
    public void Free_text_values_on_allowlisted_keys_are_replaced_with_redacted()
    {
        var redacted = TelemetryRedaction.Redact(new[]
        {
            new KeyValuePair<string, object?>("investigation.outcome", "Please approve the claim for passenger"),
            new KeyValuePair<string, object?>("trace.id", "trace-0003"),
        });

        Assert.Equal(TelemetryRedaction.Redacted, redacted["investigation.outcome"]);
        Assert.Equal("trace-0003", redacted["trace.id"]);
    }

    [Fact]
    public void Investigation_trace_is_emitted_with_required_correlation_properties()
    {
        var trace = new TelemetryRedaction.InvestigationTraceRecord(
            "trace-0004", "basis-v3", "gpt-demo-deployment", new[] { "reserve-calc" }, new[] { "doc-12#p4" },
            "escalate", "synthetic-analyst-01", "approved");
        var logger = new CapturingLogger();

        TelemetryRedaction.EmitInvestigationTrace(logger, trace, "1.2.3", "demo", TestKey);

        var properties = Assert.Single(logger.States).ToDictionary(kv => kv.Key, kv => kv.Value?.ToString());
        Assert.Equal("trace-0004", properties["trace.id"]);
        Assert.Equal("escalate", properties["investigation.decision"]);
        Assert.Equal("1.2.3", properties["service.version"]);
        Assert.Equal("demo", properties["deployment.environment"]);
        Assert.Equal(TelemetryRedaction.ServiceName, properties["service.name"]);
        Assert.Equal("investigation.completed", properties["event.name"]);
        Assert.DoesNotContain("synthetic-analyst-01", string.Join("|", properties.Values));
    }

    [Theory]
    [InlineData(TelemetryRedaction.SecurityEvents.AuthScopeFailure)]
    [InlineData(TelemetryRedaction.SecurityEvents.IntegritySignatureFailure)]
    [InlineData(TelemetryRedaction.SecurityEvents.BudgetExhausted)]
    public void Security_events_are_emitted_under_their_alert_event_name(string eventName)
    {
        var logger = new CapturingLogger();

        TelemetryRedaction.EmitSecurityEvent(logger, eventName, "1.2.3", "demo");

        var properties = Assert.Single(logger.States).ToDictionary(kv => kv.Key, kv => kv.Value?.ToString());
        Assert.Equal(eventName, properties["event.name"]);
    }

    [Fact]
    public void Unknown_security_event_names_are_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            TelemetryRedaction.EmitSecurityEvent(new CapturingLogger(), "security.made_up", "1.2.3", "demo"));
    }

    [Fact]
    public void Empty_tool_and_citation_lists_are_kept_and_injected_tool_values_are_redacted()
    {
        var redacted = TelemetryRedaction.Redact(new[]
        {
            new KeyValuePair<string, object?>("investigation.tools", string.Empty),
            new KeyValuePair<string, object?>("investigation.citations", string.Empty),
        });
        Assert.Equal(string.Empty, redacted["investigation.tools"]);
        Assert.Equal(string.Empty, redacted["investigation.citations"]);

        var injected = TelemetryRedaction.Redact(new[]
        {
            new KeyValuePair<string, object?>("investigation.tools", $"reserve-calc,{Canaries[0]}"),
        });
        Assert.Equal(TelemetryRedaction.Redacted, injected["investigation.tools"]);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<IReadOnlyList<KeyValuePair<string, object?>>> States { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> list)
            {
                States.Add(list);
            }
        }
    }
}
