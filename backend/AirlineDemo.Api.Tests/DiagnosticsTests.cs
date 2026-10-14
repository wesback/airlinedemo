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

        Assert.True(registration.Logs);
        Assert.True(registration.Traces);
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
        var captured = new List<List<KeyValuePair<string, object?>>>();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddOpenTelemetry(o =>
        {
            o.AddRedaction();
            o.AddProcessor(new CaptureLogs(captured));
        }));

        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<DiagnosticsTests>>();

        logger.LogInformation("Case {CaseId} state {State}", "case-42", "approved");
        logger.LogInformation(
            "Attempt {Passcode} cookie {Cookie} bearer {Auth} jwt {Jwt} prompt {Prompt} doc {Document}",
            Passcode, CookieValue, $"Bearer {BearerValue}", Jwt, PromptText, DocumentText);

        var output = string.Join("\n", captured.SelectMany(r => r.Select(a => $"{a.Key}={a.Value}")));

        Assert.DoesNotContain(Passcode, output);
        Assert.DoesNotContain(CookieValue, output);
        Assert.DoesNotContain(BearerValue, output);
        Assert.DoesNotContain("xyzabc12", output);
        Assert.DoesNotContain(PromptText, output);
        Assert.DoesNotContain(DocumentText, output);
        Assert.Contains("case-42", output);
        Assert.Contains("approved", output);
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

    // LogRecord instances are pooled by the SDK, so snapshot the attributes during OnEnd.
    private sealed class CaptureLogs(List<List<KeyValuePair<string, object?>>> sink) : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord data) =>
            sink.Add((data.Attributes ?? []).ToList());
    }

    private sealed class CaptureActivities(List<Activity> sink) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => sink.Add(data);
    }
}
