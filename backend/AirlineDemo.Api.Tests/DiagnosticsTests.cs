using System.Diagnostics;
using AirlineDemo.Api.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace AirlineDemo.Api.Tests;

public sealed class DiagnosticsTests
{
    private const string Passcode = "SENTINEL-PASSCODE-7731";
    private const string CookieValue = "SENTINEL-COOKIE-VALUE";
    private const string BearerValue = "SENTINEL-BEARER-VALUE";
    private const string Jwt = "xyzabc12.payload123.signature99";
    private const string PromptText = "SENTINEL-PROMPT-TEXT";
    private const string DocumentText = "SENTINEL-DOCUMENT-TEXT";

    private static readonly string[] SensitiveEntryNames =
    [
        "prompt", "gen_ai.prompt", "gen_ai.content.prompt", "document", "document.text", "document.content",
        "evidence.text", "passcode", "cookie", "set-cookie", "authorization", "token", "access_token",
        "refresh_token", "password", "secret", "email", "user.email", "user.name", "phone", "address",
        "http.request.header.cookie", "http.request.header.authorization", "enduser.id", "user.id",
    ];

    private static IConfiguration Config(string? connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(connectionString is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { [DiagnosticsSetup.ConnectionStringSetting] = connectionString })
            .Build();

    [Fact]
    public void ApplicationInsights_exporters_follow_connection_string_configuration()
    {
        var previous = Environment.GetEnvironmentVariable(DiagnosticsSetup.ConnectionStringSetting);
        try
        {
            Environment.SetEnvironmentVariable(DiagnosticsSetup.ConnectionStringSetting, "InstrumentationKey=00000000-0000-0000-0000-000000000000");
            var services = new ServiceCollection();
            services.AddAirlineDiagnostics(new ConfigurationBuilder().AddEnvironmentVariables().Build());

            using var provider = services.BuildServiceProvider();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            var tracerProvider = provider.GetRequiredService<TracerProvider>();

            Assert.True(ContainsObjectOfType(loggerFactory, "AzureMonitorLogExporter"), "Log exporter was not registered.");
            Assert.True(ContainsObjectOfType(tracerProvider, "AzureMonitorTraceExporter"), "Trace exporter was not registered.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(DiagnosticsSetup.ConnectionStringSetting, previous);
        }
    }

    [Fact]
    public void Exporters_are_not_registered_when_connection_string_is_absent()
    {
        var previous = Environment.GetEnvironmentVariable(DiagnosticsSetup.ConnectionStringSetting);
        try
        {
            Environment.SetEnvironmentVariable(DiagnosticsSetup.ConnectionStringSetting, null);
            var services = new ServiceCollection();
            services.AddAirlineDiagnostics(new ConfigurationBuilder().AddEnvironmentVariables().Build());

            using var provider = services.BuildServiceProvider();
            Assert.Empty(services);
            Assert.Null(provider.GetService<ILoggerFactory>());
            Assert.Null(provider.GetService<TracerProvider>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(DiagnosticsSetup.ConnectionStringSetting, previous);
        }
    }

    // Walks the object graph of a built provider and reports whether an object of the exporter type is reachable.
    // The exporter is found through the processors that hold it, not through any flag set by the setup code.
    private static bool ContainsObjectOfType(object root, string typeNameFragment)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(object Value, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (value, depth) = pending.Pop();
            if (!visited.Add(value))
            {
                continue;
            }

            if ((value.GetType().FullName ?? string.Empty).Contains(typeNameFragment, StringComparison.Ordinal))
            {
                return true;
            }

            if (depth >= 16)
            {
                continue;
            }

            if (value is Array array)
            {
                foreach (var item in array)
                {
                    if (item is not null && IsWalkable(item))
                    {
                        pending.Push((item, depth + 1));
                    }
                }
                continue;
            }

            for (var type = value.GetType(); type is not null && type != typeof(object); type = type.BaseType)
            {
                foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    if (field.FieldType.IsValueType)
                    {
                        continue;
                    }

                    var child = field.GetValue(value);
                    if (child is not null && IsWalkable(child))
                    {
                        pending.Push((child, depth + 1));
                    }
                }
            }
        }

        return false;
    }

    private static bool IsWalkable(object value)
    {
        if (value is Array)
        {
            return true;
        }

        var name = value.GetType().FullName ?? string.Empty;
        return name.StartsWith("OpenTelemetry", StringComparison.Ordinal)
            || name.StartsWith("Azure.Monitor", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal)
            || name.StartsWith("System.Collections", StringComparison.Ordinal)
            || name.StartsWith("System.Diagnostics", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("Document")]
    [InlineData("passcode")]
    [InlineData("cookie")]
    [InlineData("authorization")]
    [InlineData("__Host-session")]
    [InlineData(".AspNetCore.Cookies")]
    public void Sensitive_keys_are_redacted(string key)
    {
        Assert.Equal(RedactionFilter.Redacted, RedactionFilter.RedactValue(key, "anything"));
    }

    [Fact]
    public void Bearer_token_and_jwt_values_are_redacted_by_shape()
    {
        Assert.DoesNotContain(BearerValue, RedactionFilter.RedactValue("note", $"Bearer {BearerValue}")!);
        Assert.DoesNotContain("xyzabc12", RedactionFilter.RedactValue("note", $"token {Jwt} end")!);
    }

    [Fact]
    public void Jwt_with_header_not_starting_with_e_is_redacted()
    {
        var jwt = "xyzabc12.payload123.signature99";
        Assert.DoesNotContain(jwt, RedactionFilter.RedactValue("note", jwt)!);
    }

    [Fact]
    public void Unrelated_safe_metadata_is_preserved()
    {
        Assert.Equal("case-42", RedactionFilter.RedactValue("case.id", "case-42"));
        Assert.Equal("trace-abcd1234", RedactionFilter.RedactValue("trace.id", "trace-abcd1234"));
        Assert.Equal("v1.2.3", RedactionFilter.RedactValue("service.version", "v1.2.3"));
    }

    [Fact]
    public void Log_properties_and_scopes_redact_sensitive_values_and_keep_safe_metadata()
    {
        var captured = new List<CapturedLog>();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
            o.AddRedaction();
            o.AddProcessor(new CaptureLogs(captured));
        }));

        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<DiagnosticsTests>>();

        logger.LogInformation("Case {CaseId} state {State}", "case-42", "approved");
        logger.LogInformation(
            "Attempt {Passcode} cookie {Cookie} bearer {Auth} jwt {Jwt} prompt {Prompt} doc {Document}",
            Passcode, CookieValue, $"Bearer {BearerValue}", Jwt, PromptText, DocumentText);
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["passcode"] = Passcode,
            ["__Host-session"] = CookieValue,
            [".AspNetCore.Cookies"] = CookieValue,
            ["case.id"] = "scope-case-5521",
        }))
        {
            logger.LogInformation("Scoped event {State}", "approved");
        }

        const string NamedValue = "SENTINEL-NAMED-VALUE";
        var namedEntries = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["case.id"] = "named-case-9",
        };
        foreach (var name in SensitiveEntryNames)
        {
            namedEntries[name] = NamedValue;
        }

        using (logger.BeginScope(namedEntries))
        {
            logger.LogInformation("Named entries {State}", "approved");
        }

        var attributes = string.Join("\n", captured.SelectMany(r => r.Attributes.Select(a => $"{a.Key}={a.Value}")));
        var scopes = string.Join("\n", captured.SelectMany(r => r.Scopes.Select(a => $"{a.Key}={a.Value}")));
        var messages = string.Join("\n", captured.Select(r => r.Message));
        var output = attributes + "\n" + scopes + "\n" + messages;

        Assert.DoesNotContain(Passcode, output);
        Assert.DoesNotContain(CookieValue, output);
        Assert.DoesNotContain(BearerValue, output);
        Assert.DoesNotContain("xyzabc12", output);
        Assert.DoesNotContain(PromptText, output);
        Assert.DoesNotContain(DocumentText, output);
        Assert.DoesNotContain(NamedValue, output);
        Assert.Contains("named-case-9", attributes);
        Assert.Contains("case-42", attributes);
        Assert.Contains("case.id=scope-case-5521", attributes);
        Assert.Contains("approved", attributes);
        Assert.All(captured, r => Assert.Equal("diagnostic event", r.Message));

        // Sensitive entries must be dropped by name, not merely given placeholder values.
        var keys = captured.SelectMany(r => r.Attributes.Select(a => a.Key).Concat(r.Scopes.Select(s => s.Key)))
            .Select(k => k.ToLowerInvariant())
            .ToList();
        foreach (var name in SensitiveEntryNames)
        {
            Assert.DoesNotContain(name, keys);
        }
    }

    [Fact]
    public void Logging_pipeline_omits_formatted_message_and_exception_details_when_no_properties_or_scopes()
    {
        const string ExceptionCanary = "SENTINEL-EXCEPTION-MESSAGE";
        const string InnerCanary = "SENTINEL-INNER-EXCEPTION";

        var captured = new List<CapturedLog>();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
            o.AddRedaction();
            o.AddProcessor(new CaptureLogs(captured));
        }));

        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<DiagnosticsTests>>();

        const string FormattedCanary = "SENTINEL-FORMATTED-MESSAGE";
        Exception thrown;
        try
        {
            throw new InvalidOperationException(ExceptionCanary, new IOException(InnerCanary));
        }
        catch (Exception ex)
        {
            thrown = ex;
        }
        Assert.NotNull(thrown.StackTrace);

        // No template arguments: the canary exists only in the rendered text.
        logger.LogError(thrown, $"Operation failed {FormattedCanary}");
        logger.LogError(thrown, "Operation failed");

        // AC-05: every exported record carries the fixed body, whatever the call supplied.
        Assert.Equal(2, captured.Count);
        Assert.All(captured, r => Assert.Equal("diagnostic event", r.Message));

        var output = string.Join("\n", captured.SelectMany(r => r.Attributes.Select(a => $"{a.Key}={a.Value}")).Concat(captured.Select(r => r.Message)));
        Assert.DoesNotContain(ExceptionCanary, output);
        Assert.DoesNotContain(InnerCanary, output);
        Assert.DoesNotContain(FormattedCanary, output);
        Assert.DoesNotContain("   at ", output);
        Assert.DoesNotContain("System.InvalidOperationException", output);

        // With structured data, the exception type is still not exported because it fails the error.type allowlist.
        captured.Clear();
        logger.LogError(thrown, "Operation failed for {Case}", "case-42");
        Assert.All(captured, r => Assert.Equal("diagnostic event", r.Message));
        var structured = string.Join("\n", captured.SelectMany(r => r.Attributes.Select(a => $"{a.Key}={a.Value}")).Concat(captured.Select(r => r.Message)));
        Assert.Contains("case-42", structured);
        Assert.DoesNotContain(ExceptionCanary, structured);
        Assert.DoesNotContain("System.InvalidOperationException", structured);
    }

    [Fact]
    public void Logging_pipeline_omits_interpolated_message_when_only_scopes_are_present_and_keeps_allowlisted_error_type()
    {
        const string FormattedCanary = "SENTINEL-SCOPED-FORMATTED";
        var captured = new List<CapturedLog>();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
            o.AddRedaction();
            o.AddProcessor(new CaptureLogs(captured));
        }));

        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<DiagnosticsTests>>();

        using (logger.BeginScope(new List<KeyValuePair<string, object?>> { new("case.id", "case-42") }))
        {
            logger.LogError(new airline.demo.boom("ignored"), $"Operation failed {FormattedCanary}");
        }

        var record = captured.Single();
        var output = string.Join("\n", record.Attributes.Select(a => $"{a.Key}={a.Value}")) + "\n" + record.Message;
        Assert.DoesNotContain(FormattedCanary, output);
        Assert.Contains("case.id=case-42", output);
        // AC-05: an error.type that passes the allowlist rule is preserved.
        Assert.Contains("error.type=airline.demo.boom", output);
        Assert.DoesNotContain("ignored", output);
    }

    [Fact]
    public void Trace_pipeline_redacts_span_tags_and_direct_activity_event_attributes()
    {
        var captured = new List<Activity>();
        using var source = new ActivitySource(TelemetryRedaction.ServiceName);
        using var providers = Sdk.CreateTracerProviderBuilder()
            .AddSource(TelemetryRedaction.ServiceName)
            .AddRedaction()
            .AddProcessor(new CaptureActivities(captured))
            .Build();

        using (var activity = source.StartActivity("review"))
        {
            activity!.SetTag("prompt", PromptText);
            activity.SetTag("passcode", Passcode);
            activity.SetTag("cookie", CookieValue);
            activity.SetTag("note", $"Bearer {BearerValue}");
            activity.SetTag(".AspNetCore.Cookies", CookieValue);
            activity.SetTag("__Host-session", CookieValue);
            activity.SetTag("case.id", "case-42");
            activity.AddEvent(new ActivityEvent("review.event", tags: new ActivityTagsCollection(new Dictionary<string, object?>
            {
                ["prompt"] = PromptText,
                ["document.text"] = DocumentText,
                ["passcode"] = Passcode,
                ["cookie"] = CookieValue,
                [".AspNetCore.Cookies"] = CookieValue,
                ["note"] = $"Bearer {BearerValue}",
                ["jwt"] = Jwt,
                ["case.id"] = "case-42",
            })));
        }

        var activityRecord = captured.Single();
        var tags = string.Join("\n", activityRecord.TagObjects.Select(t => $"{t.Key}={t.Value}"));

        Assert.DoesNotContain(PromptText, tags);
        Assert.DoesNotContain(Passcode, tags);
        Assert.DoesNotContain(CookieValue, tags);
        Assert.DoesNotContain(BearerValue, tags);
        Assert.Contains("case.id=case-42", tags);
        foreach (var name in new[] { "prompt", "document.text", "passcode", "cookie" })
        {
            Assert.DoesNotContain(activityRecord.TagObjects, t => t.Key == name);
        }
        Assert.DoesNotContain(activityRecord.TagObjects, t => t.Key == "__Host-session");
        Assert.DoesNotContain(activityRecord.TagObjects, t => t.Key == ".AspNetCore.Cookies");

        var eventTags = string.Join("\n", activityRecord.Events.Single().Tags!.Select(t => $"{t.Key}={t.Value}"));
        Assert.DoesNotContain(PromptText, eventTags);
        Assert.DoesNotContain(DocumentText, eventTags);
        Assert.DoesNotContain(Passcode, eventTags);
        Assert.DoesNotContain(CookieValue, eventTags);
        Assert.DoesNotContain(BearerValue, eventTags);
        Assert.DoesNotContain(Jwt, eventTags);
        Assert.Contains("case.id=case-42", eventTags);
        var eventTagList = activityRecord.Events.Single().Tags!.ToList();
        foreach (var name in new[] { "prompt", "document.text", "passcode", "cookie", ".AspNetCore.Cookies" })
        {
            Assert.DoesNotContain(eventTagList, t => t.Key == name);
        }
    }

    [Fact]
    public void Non_string_property_values_are_scrubbed_for_bearer_and_jwt()
    {
        var holder = new Holder($"Bearer {BearerValue}", Jwt);
        var attributes = RedactionFilter.RedactAttributes(new[] { new KeyValuePair<string, object?>("holder", holder) });
        var text = string.Join("\n", attributes.Select(a => $"{a.Key}={a.Value}"));

        Assert.DoesNotContain(BearerValue, text);
        Assert.DoesNotContain("xyzabc12", text);
    }

    private sealed record Holder(string Auth, string Token);

    // LogRecord instances are pooled by the SDK, so snapshot the attributes, scopes and rendered message during OnEnd.
    private sealed record CapturedLog(
        string Message,
        List<KeyValuePair<string, object?>> Attributes,
        List<KeyValuePair<string, object?>> Scopes);

    private sealed class CaptureLogs(List<CapturedLog> sink) : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord data)
        {
            // Scopes are folded into the redacted attributes and removed from the record, so the attributes carry them.
            sink.Add(new CapturedLog(data.Body ?? string.Empty, (data.Attributes ?? []).ToList(), []));
        }
    }

    private sealed class CaptureActivities(List<Activity> sink) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => sink.Add(data);
    }
}
