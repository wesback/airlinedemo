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

    private static IConfiguration Config(string? connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(connectionString is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { [DiagnosticsSetup.ConnectionStringSetting] = connectionString })
            .Build();

    [Fact]
    public void Exporters_are_registered_when_connection_string_is_configured()
    {
        var services = new ServiceCollection();
        services.AddAirlineDiagnostics(Config("InstrumentationKey=00000000-0000-0000-0000-000000000000"));

        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<DiagnosticsSetup.ExporterRegistration>();
        // Exporter options run when the logger and tracer providers are built, so resolve both first.
        _ = provider.GetRequiredService<ILoggerFactory>();
        _ = provider.GetRequiredService<TracerProvider>();

        Assert.True(registration.Logs, "Log exporter was not registered.");
        Assert.True(registration.Traces, "Trace exporter was not registered.");
    }

    [Fact]
    public void Exporters_are_not_registered_when_connection_string_is_absent()
    {
        var services = new ServiceCollection();
        services.AddAirlineDiagnostics(Config(null));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(DiagnosticsSetup.ExporterRegistration));
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
    public void Log_pipeline_removes_sentinels_from_structured_properties()
    {
        var captured = new List<CapturedLog>();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.AddRedaction();
            o.AddProcessor(new CaptureLogs(captured));
        }));

        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<DiagnosticsTests>>();

        logger.LogInformation("Case {CaseId} state {State}", "case-42", "approved");
        logger.LogInformation(
            "Attempt {Passcode} cookie {Cookie} bearer {Auth} jwt {Jwt} prompt {Prompt} doc {Document}",
            Passcode, CookieValue, $"Bearer {BearerValue}", Jwt, PromptText, DocumentText);

        var attributes = string.Join("\n", captured.SelectMany(r => r.Attributes.Select(a => $"{a.Key}={a.Value}")));
        var messages = string.Join("\n", captured.Select(r => r.Message));
        var output = attributes + "\n" + messages;

        Assert.DoesNotContain(Passcode, output);
        Assert.DoesNotContain(CookieValue, output);
        Assert.DoesNotContain(BearerValue, output);
        Assert.DoesNotContain("xyzabc12", output);
        Assert.DoesNotContain(PromptText, output);
        Assert.DoesNotContain(DocumentText, output);
        Assert.Contains("case-42", attributes);
        Assert.Contains("approved", attributes);
        Assert.Contains("case-42", messages);
        Assert.Contains("approved", messages);
    }

    [Fact]
    public void Trace_pipeline_removes_sentinels_from_tags()
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
            activity.SetTag("case.id", "case-42");
        }

        var tags = captured.Single().TagObjects.Select(t => $"{t.Key}={t.Value}").ToList();
        var output = string.Join("\n", tags);

        Assert.DoesNotContain(PromptText, output);
        Assert.DoesNotContain(Passcode, output);
        Assert.DoesNotContain(CookieValue, output);
        Assert.DoesNotContain(BearerValue, output);
        Assert.Contains("case.id=case-42", output);
    }

    // LogRecord instances are pooled by the SDK, so snapshot the attributes and rendered message during OnEnd.
    private sealed record CapturedLog(string Message, List<KeyValuePair<string, object?>> Attributes);

    private sealed class CaptureLogs(List<CapturedLog> sink) : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord data) =>
            sink.Add(new CapturedLog(data.FormattedMessage ?? string.Empty, (data.Attributes ?? []).ToList()));
    }

    private sealed class CaptureActivities(List<Activity> sink) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => sink.Add(data);
    }
}
