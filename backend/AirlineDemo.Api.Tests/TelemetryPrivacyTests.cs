using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Diagnostics;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace AirlineDemo.Api.Tests;

public sealed class TelemetryPrivacyTests
{
    [Fact]
    public void Bearer_jwt_and_cookie_name_shapes_are_redacted()
    {
        const string bearer = "opaque-Token_123";
        const string cookieA = "SECRETVALUE-A1";
        const string cookieB = "SECRETVALUE-B2";
        const string jwt = "eyJhbGciOi.eyJzdWIiOiIx.c2lnbmF0dXJl";

        var bearerOut = RedactionFilter.RedactValue("note", $"sent Bearer {bearer} now")!;
        var cookieOut = RedactionFilter.RedactValue("note", $"__Host-sid={cookieA}; .AspNetCore.Cookies={cookieB}; theme=dark")!;
        var jwtOut = RedactionFilter.RedactValue("note", $"token {jwt} end")!;

        Assert.DoesNotContain(bearer, bearerOut);
        Assert.Contains("now", bearerOut);
        Assert.DoesNotContain(cookieA, cookieOut);
        Assert.DoesNotContain(cookieB, cookieOut);
        Assert.Contains("theme=dark", cookieOut);
        Assert.DoesNotContain(jwt, jwtOut);
        Assert.Contains("end", jwtOut);
    }

    [Fact]
    public void Jwt_redaction_does_not_assume_header_prefix()
    {
        foreach (var jwt in new[] { "Q1234567ABCDEF.payload123.signature99", "abcdefgh.ijklmnop.qrstuvwx", "eyJhbGciOi.payload123.signature99" })
        {
            var output = RedactionFilter.RedactValue("note", $"value {jwt} end")!;
            Assert.DoesNotContain(jwt, output);
            Assert.Contains("end", output);
        }
    }

    [Fact]
    public void Logging_and_trace_pipelines_exclude_sensitive_canaries_and_keep_safe_metadata()
    {
        var logs = new List<PipelineLogEntry>();
        var spans = new List<Activity>();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.AddRedaction();
            o.AddProcessor(new PipelineLogSink(logs));
        }));
        using var loggers = services.BuildServiceProvider();
        var logger = loggers.GetRequiredService<ILogger<TelemetryPrivacyTests>>();

        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["passcode"] = Canaries[2],
            ["case.id"] = "scope-case-5521",
        }))
        {
            logger.LogInformation(
                "Review {State} prompt {Prompt} document {Document} note {Note} auth {Auth} email {Email}",
                "approved", Canaries[0], Canaries[1], "__Host-sid=" + Canaries[3] + "; theme=dark",
                "Bearer " + Canaries[4], Canaries[5]);
        }

        using var tracing = Sdk.CreateTracerProviderBuilder()
            .AddSource(TelemetryRedaction.ServiceName)
            .AddRedaction()
            .AddProcessor(new PipelineSpanSink(spans))
            .Build();
        using var source = new ActivitySource(TelemetryRedaction.ServiceName);
        using (var activity = source.StartActivity("review"))
        {
            activity!.SetTag("prompt", Canaries[0]);
            activity.SetTag("gen_ai.prompt", Canaries[0]);
            activity.SetTag("document.text", Canaries[1]);
            activity.SetTag("case.id", "trace-case-8810");
            activity.AddEvent(new ActivityEvent("review.event", tags: new ActivityTagsCollection(new Dictionary<string, object?>
            {
                ["passcode"] = Canaries[2],
                ["auth"] = "Bearer " + Canaries[4],
                ["jwt"] = "eyJhbGciOi.eyJzdWIiOiIx.c2lnbmF0dXJl",
                ["note"] = "__Host-sid=" + Canaries[3] + "; theme=dark",
                ["case.id"] = "trace-case-8810",
            })));
        }

        var logOutput = string.Join("\n",
            logs.Select(e => e.Message).Concat(logs.SelectMany(e => e.Attributes.Select(a => $"{a.Key}={a.Value}"))));
        var spanOutput = string.Join("\n",
            spans.SelectMany(s => s.TagObjects.Select(t => $"{t.Key}={t.Value}"))
                .Concat(spans.SelectMany(s => s.Events).SelectMany(e => e.Tags?.Select(t => $"{t.Key}={t.Value}") ?? Enumerable.Empty<string>())));
        var output = logOutput + "\n" + spanOutput;

        foreach (var canary in Canaries)
        {
            Assert.DoesNotContain(canary, output);
        }
        Assert.DoesNotContain("eyJhbGciOi.eyJzdWIiOiIx.c2lnbmF0dXJl", output);

        Assert.Contains("scope-case-5521", logOutput);
        Assert.Contains("approved", logOutput);
        Assert.Contains("theme=dark", logOutput);
        Assert.Contains("trace-case-8810", spanOutput);
        Assert.Contains("case.id=trace-case-8810", spanOutput);
    }

    private sealed record PipelineLogEntry(string Message, List<KeyValuePair<string, object?>> Attributes);

    private sealed class PipelineLogSink(List<PipelineLogEntry> sink) : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord data) =>
            sink.Add(new PipelineLogEntry((data.FormattedMessage ?? string.Empty) + "\n" + (data.Body ?? string.Empty), (data.Attributes ?? []).ToList()));
    }

    private sealed class PipelineSpanSink(List<Activity> sink) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => sink.Add(data);
    }

    private static readonly byte[] TestKey = Encoding.UTF8.GetBytes("synthetic-test-key-not-a-secret");
    private const string AppPasscode = "test-passcode";

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
    [InlineData("cookie: session=harmless; auth=CANARY-BEARER-5a1e")]
    [InlineData("Cookie: a=1; b=2; auth=CANARY-BEARER-5a1e; c=3")]
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
    public void Resource_attributes_redact_malformed_environment_and_version()
    {
        var resource = TelemetryRedaction.ResourceAttributes("1.2.3\nevent.name=x", "prod\"; drop table");

        Assert.Equal("[redacted]", resource["service.version"]);
        Assert.Equal("[redacted]", resource["deployment.environment"]);
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

    [Fact]
    public async Task Role_switch_success_event_is_redacted()
    {
        var logger = new CapturingLogger();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Passcode"] = AppPasscode,
            }));
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(logger)));
        });
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        using var signIn = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId = "presenter", passcode = AppPasscode }, ct);
        var oldCookie = signIn.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var oldSessionId = oldCookie.Split('=', 2)[1];
        var csrf = (await signIn.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("csrfToken").GetString()!;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/switch-user")
        { Content = JsonContent.Create(new { userId = "exec-1" }) };
        request.Headers.Add("Cookie", oldCookie);
        request.Headers.Add("X-CSRF-Token", csrf);
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var newCookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var newSessionId = newCookie.Split('=', 2)[1];

        var properties = Assert.Single(logger.States.Select(ToProperties)
            .Where(p => p.GetValueOrDefault("event.name") == TelemetryRedaction.SecurityEvents.RoleSwitch));
        Assert.Equal("role-switch", properties["action"]);
        Assert.Equal("success", properties["event.outcome"]);
        Assert.Equal("executive", properties["fixture.role"]);
        Assert.Matches("^actor-[0-9a-f]{16}$", properties["enduser.pseudo.id"]!);
        Assert.Matches("^trace-[0-9a-f]{16}$", properties["trace.id"]!);
        var payload = string.Join("|", properties.Values);
        Assert.DoesNotContain(oldSessionId, payload);
        Assert.DoesNotContain(newSessionId, payload);
        Assert.DoesNotContain(oldCookie, payload);
        Assert.DoesNotContain(csrf, payload);
    }

    [Fact]
    public async Task Role_switch_rejection_events_carry_the_exact_outcome()
    {
        var logger = new CapturingLogger();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Passcode"] = AppPasscode }));
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(logger)));
        });
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var unauthenticated = await client.PostAsJsonAsync("/api/auth/switch-user", new { userId = "exec-1" }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal("{\"error\":\"Not signed in.\"}", await unauthenticated.Content.ReadAsStringAsync(ct));

        using var signIn = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId = "presenter", passcode = AppPasscode }, ct);
        var cookie = signIn.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var csrf = (await signIn.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("csrfToken").GetString()!;

        foreach (var scenario in new[] { "no-token", "wrong-token", "unknown-user", "empty-user", "malformed-json" })
        {
            object body = scenario == "unknown-user" ? new { userId = "nobody" }
                : scenario == "empty-user" ? new { userId = "" } : new { userId = "exec-1" };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/switch-user")
            { Content = scenario == "malformed-json" ? new StringContent("{ not-json", Encoding.UTF8, "application/json") : JsonContent.Create(body) };
            request.Headers.Add("Cookie", cookie);
            if (scenario != "no-token") request.Headers.Add("X-CSRF-Token", scenario == "wrong-token" ? "wrong-token" : csrf);
            using var response = await client.SendAsync(request, ct);
            var expectedStatus = scenario.Contains("token") ? HttpStatusCode.Forbidden : HttpStatusCode.BadRequest;
            Assert.Equal(expectedStatus, response.StatusCode);
            if (scenario.Contains("token")) Assert.Equal("{\"error\":\"Anti-forgery validation failed.\"}", await response.Content.ReadAsStringAsync(ct));
            else if (scenario == "malformed-json") Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(ct));
            else Assert.Equal("{\"error\":\"Unknown user.\"}", await response.Content.ReadAsStringAsync(ct));
        }

        var events = logger.States.Select(ToProperties).Where(p => p.GetValueOrDefault("event.name") == TelemetryRedaction.SecurityEvents.RoleSwitch).ToList();
        Assert.Equal(6, events.Count);
        Assert.Equal(new[] { "unauthorized", "anti_forgery_failed", "anti_forgery_failed", "unknown_user", "unknown_user", "unknown_user" }, events.Select(p => p["event.outcome"]!).ToArray());
        Assert.Equal("unknown", events[0]["fixture.role"]);
        Assert.All(events.Skip(1), p => Assert.Equal("presenter", p["fixture.role"]));
    }

    [Fact]
    public void Role_switch_events_are_redacted()
    {
        var logger = new CapturingLogger();
        TelemetryRedaction.EmitRoleSwitchEvent(logger, "success", "executive", "session-secret-123", "1.2.3", "demo");
        var properties = Assert.Single(logger.States.Select(ToProperties));
        var redacted = TelemetryRedaction.Redact(new KeyValuePair<string, object?>[]
        {
            new("passcode", AppPasscode), new("cookie", "__Host-demo-session=secret"), new("token", "csrf-secret"),
            new("document.text", "item text"), new("prompt", "notice draft"), new("outside.allowlist", "canary"),
        });
        Assert.Empty(redacted);
        var payload = string.Join("|", properties.Values.Concat(redacted.Values));
        foreach (var secret in new[] { AppPasscode, "session-secret-123", "csrf-secret", "item text", "notice draft", "canary" }) Assert.DoesNotContain(secret, payload);
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
    public void Exhausted_model_budget_emits_budget_alert_event_and_refuses_the_run()
    {
        var logger = new CapturingLogger();
        var requests = new[]
        {
            new ModelRequest("v1", "uksouth", "SYNTHETIC EVIDENCE: one"),
            new ModelRequest("v1", "uksouth", "SYNTHETIC EVIDENCE: two"),
        };

        var ex = Assert.Throws<ModelGateException>(() =>
            ModelCallGate.AuthorizeScoredRunWithinBudget(null!, requests, null!, 1, logger, "1.2.3", "demo"));

        Assert.Contains("budget exhausted", ex.Message);
        var properties = Assert.Single(logger.States).ToDictionary(kv => kv.Key, kv => kv.Value?.ToString());
        Assert.Equal(TelemetryRedaction.SecurityEvents.BudgetExhausted, properties["event.name"]);
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

    [Fact]
    public async Task Review_decision_emits_correlated_trace_through_the_app()
    {
        var logger = new CapturingLogger();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Demo:Passcode"] = AppPasscode,
                    ["CaseState:Path"] = CaseSeed.CreateFile(),
                }));
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(logger)));
        });
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var signIn = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId = "reviewer", passcode = AppPasscode }, ct);
        signIn.EnsureSuccessStatusCode();
        var cookie = signIn.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var csrf = (await signIn.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("csrfToken").GetString()!;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/cases/case-001/reviews")
        {
            Content = JsonContent.Create(new { expectedRevision = 1, expectedBasisVersion = 1, decision = "accept", reason = "basis reviewed" }),
        };
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-CSRF-Token", csrf);
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var traces = logger.States
            .Select(ToProperties)
            .Where(p => p.GetValueOrDefault("event.name") == "review.completed")
            .ToList();
        var properties = Assert.Single(traces);
        Assert.False(string.IsNullOrWhiteSpace(properties["trace.id"]));
        Assert.Equal("return-ready", properties["investigation.decision"]);
        Assert.Equal(TelemetryRedaction.ServiceName, properties["service.name"]);
        Assert.Equal(TelemetryRedaction.ServiceVersion, properties["service.version"]);
        Assert.Equal("demo", properties["deployment.environment"]);
        Assert.Matches("^basis-v[0-9]{1,4}$", properties["investigation.basis_version"]!);
        Assert.False(string.IsNullOrWhiteSpace(properties["gen_ai.request.model"]));
        Assert.True(properties.ContainsKey("investigation.tools"));
        Assert.True(properties.ContainsKey("investigation.citations"));
        Assert.Matches("^actor-[0-9a-f]{16}$", properties["enduser.pseudo.id"]!);
        Assert.False(string.IsNullOrWhiteSpace(properties["investigation.outcome"]));
        Assert.DoesNotContain("reviewer", string.Join("|", properties.Values));
    }

    [Fact]
    public async Task Case_integrity_failure_emits_integrity_alert_event_through_the_app()
    {
        var logger = new CapturingLogger();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Passcode"] = AppPasscode }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(logger));
                var path = CaseSeed.CreateFile();
                services.AddSingleton<ICaseStateStore>(_ => new CaseStateStore(path,
                    () => throw new BasisIntegrityException("Forced integrity failure for alert-path test.")));
            });
        });
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var signIn = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId = "reviewer", passcode = AppPasscode }, ct);
        signIn.EnsureSuccessStatusCode();
        var cookie = signIn.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var csrf = (await signIn.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("csrfToken").GetString()!;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/cases/case-001/reviews")
        {
            Content = JsonContent.Create(new { expectedRevision = 1, expectedBasisVersion = 1, decision = "accept", reason = "basis reviewed" }),
        };
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-CSRF-Token", csrf);
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var alerts = logger.States
            .Select(ToProperties)
            .Where(p => p.GetValueOrDefault("event.name") == TelemetryRedaction.SecurityEvents.IntegritySignatureFailure)
            .ToList();
        Assert.Single(alerts);
    }

    private static Dictionary<string, string?> ToProperties(IReadOnlyList<KeyValuePair<string, object?>> state) =>
        state.GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value?.ToString());

    [Fact]
    public async Task Unauthenticated_case_requests_emit_auth_scope_failure_alert_event_through_the_app()
    {
        var logger = new CapturingLogger();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Passcode"] = AppPasscode }));
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(logger)));
        });
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.GetAsync("/api/cases/case-001", ct);
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var alerts = logger.States
            .Select(state => state.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()))
            .Where(properties => properties.GetValueOrDefault("event.name") == TelemetryRedaction.SecurityEvents.AuthScopeFailure)
            .ToList();
        Assert.Equal(2, alerts.Count);
    }

    private sealed class CapturingLoggerProvider(CapturingLogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly object _gate = new();
        private readonly List<IReadOnlyList<KeyValuePair<string, object?>>> _states = new();

        // Logging can happen on request threads while the test enumerates; return a locked snapshot.
        public IReadOnlyList<IReadOnlyList<KeyValuePair<string, object?>>> States
        {
            get
            {
                lock (_gate) return _states.ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> list)
            {
                // Snapshot now: framework state can be backed by request features disposed after the response.
                var snapshot = list.ToList();
                lock (_gate) _states.Add(snapshot);
            }
        }
    }
}
