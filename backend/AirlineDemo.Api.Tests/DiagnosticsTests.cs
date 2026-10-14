using System.Diagnostics;
using AirlineDemo.Api.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace AirlineDemo.Api.Tests;

public sealed class DiagnosticsTests
{
    [Theory]
    [InlineData("prompt")]
    [InlineData("gen_ai.prompt")]
    [InlineData("document")]
    [InlineData("document.text")]
    [InlineData("text")]
    [InlineData("passcode")]
    [InlineData("password")]
    [InlineData("cookie")]
    [InlineData("set-cookie")]
    [InlineData("authorization")]
    [InlineData("token")]
    [InlineData("access_token")]
    public void SensitiveNamesAreRedacted(string name) =>
        Assert.Equal(TelemetryRedaction.Redacted, RedactionFilter.Sanitize(name, "anything"));

    [Theory]
    [InlineData("gen_ai.usage.input_tokens")]
    [InlineData("gen_ai.usage.output_tokens")]
    [InlineData("duration_ms")]
    [InlineData("investigation.citations")]
    public void OperationalNamesAreKept(string name) =>
        Assert.Equal(42, RedactionFilter.Sanitize(name, 42));

    [Theory]
    [InlineData("Bearer eyJhbGci.abc.def", "eyJhbGci")]
    [InlineData("session=8f3c9a2b7d; Path=/", "8f3c9a2b7d")]
    [InlineData(".AspNetCore.Session=abc123", "abc123")]
    public void BearerTokensAndSessionCookiesInTextAreScrubbed(string text, string secret)
    {
        var scrubbed = RedactionFilter.ScrubText(text);

        Assert.Contains(TelemetryRedaction.Redacted, scrubbed);
        Assert.DoesNotContain(secret, scrubbed);
    }

    [Fact]
    public void LoggingPipelineKeepsSecretsOutOfEveryCapturedRecord()
    {
        var capture = new CapturingProvider();
        using var services = new ServiceCollection()
            .AddLogging(builder => builder.AddProvider(capture))
            .AddRedactingLogging()
            .BuildServiceProvider();
        var logger = services.GetRequiredService<ILogger<DiagnosticsTests>>();

        logger.LogInformation(
            "Sign-in {passcode} cookie {cookie} prompt {prompt} document {document} text {text}",
            "4815", "session=8f3c9a2b7d", "Summarise the secret clause", "Confidential lease text", "Plain body text");
        logger.LogWarning("Request carried {Header}", "session=8f3c9a2b7d");
        logger.LogInformation("Caller {authorization} tokens {input_tokens}", "Bearer eyJhbGci.abc.def", 128);

        var output = string.Join("\n", capture.Records.Select(record =>
            record.Message + " " + string.Join(" ", record.State.Select(pair => $"{pair.Key}={pair.Value}"))));

        Assert.Equal(3, capture.Records.Count);
        foreach (var secret in new[] { "4815", "8f3c9a2b7d", "Summarise the secret clause", "Confidential lease text", "Plain body text", "eyJhbGci" })
        {
            Assert.DoesNotContain(secret, output);
        }
        Assert.Contains(TelemetryRedaction.Redacted, output);
        Assert.Contains("input_tokens=128", output);
    }

    [Fact]
    public void ExceptionsAndScopesAreRedactedBeforeTheProvider()
    {
        var capture = new CapturingProvider();
        using var services = new ServiceCollection()
            .AddLogging(builder => builder.AddProvider(capture))
            .AddRedactingLogging()
            .BuildServiceProvider();
        var logger = services.GetRequiredService<ILogger<DiagnosticsTests>>();

        using (logger.BeginScope("request session=8f3c9a2b7d Authorization: Bearer eyJhbGci"))
        {
            logger.LogError(new InvalidOperationException("Bad passcode 4815 for cookie session=8f3c9a2b7d"), "Sign-in failed");
        }

        var record = Assert.Single(capture.Records);
        var exceptionText = record.Exception!.ToString();
        Assert.DoesNotContain("4815", exceptionText);
        Assert.DoesNotContain("8f3c9a2b7d", exceptionText);
        Assert.Contains(TelemetryRedaction.Redacted, exceptionText);
        Assert.Contains(TelemetryRedaction.Redacted, capture.Scopes.Single());
        Assert.DoesNotContain("eyJhbGci", capture.Scopes.Single());
        Assert.DoesNotContain("8f3c9a2b7d", capture.Scopes.Single());
    }

    [Fact]
    public void NumbersUnderUnlistedNamesAreRedactedButOperationalOnesKept()
    {
        Assert.Equal(TelemetryRedaction.Redacted, RedactionFilter.Sanitize("balance", 12345));
        Assert.Equal(TelemetryRedaction.Redacted, RedactionFilter.Sanitize("flag", true));
        Assert.Equal(7, RedactionFilter.Sanitize("retry_count", 7));
    }

    [Fact]
    public void LiteralTemplateTextAndMalformedTemplatesAreRedacted()
    {
        var capture = new CapturingProvider();
        using var services = new ServiceCollection()
            .AddLogging(builder => builder.AddProvider(capture))
            .AddRedactingLogging()
            .BuildServiceProvider();
        var logger = services.GetRequiredService<ILogger<DiagnosticsTests>>();

        logger.LogInformation("Confidential");
        logger.LogInformation("Lease clause 4 forbids subletting");
        logger.LogInformation($"Interpolated {"Confidential"} content {{braces}}");
        logger.LogInformation("Investigation {investigation_id} state {state}", "inv-1", "ready");

        var messages = capture.Records.Select(r => r.Message).ToList();
        Assert.All(messages.Take(3), m => Assert.Equal(TelemetryRedaction.Redacted, m));
        Assert.Equal("Investigation inv-1 state ready", messages[3]);
    }

    [Fact]
    public void ConfiguredTracingPipelineRedactsTraceAttributes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration[DiagnosticsSetup.ConnectionStringSetting] =
            "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://localhost/";
        builder.AddAirlineDiagnostics();
        var seen = new CapturingProcessor();
        builder.Services.ConfigureOpenTelemetryTracerProvider((_, tracing) => tracing.AddSource("DiagnosticsTests.Pipeline").AddProcessor(seen));
        using var app = builder.Build();
        using var provider = app.Services.GetRequiredService<TracerProvider>();
        using var source = new ActivitySource("DiagnosticsTests.Pipeline");

        using (var activity = source.StartActivity("chat"))
        {
            Assert.NotNull(activity);
            activity!.SetTag("gen_ai.prompt", "Summarise the secret clause");
            activity.SetTag("cookie", "session=8f3c9a2b7d");
        }

        var tags = Assert.Single(seen.Tags);
        Assert.Equal(TelemetryRedaction.Redacted, tags["gen_ai.prompt"]);
        Assert.Equal(TelemetryRedaction.Redacted, tags["cookie"]);
    }

    [Fact]
    public void OverlongFreeTextIsRedacted()
    {
        var longText = new string('x', RedactionFilter.MaxFreeTextLength + 1);

        Assert.Equal(TelemetryRedaction.Redacted, RedactionFilter.Sanitize("status", longText));
        Assert.Equal("short", RedactionFilter.Sanitize("status", "short"));
    }

    [Fact]
    public void FreeTextUnderUnlistedNamesIsRedactedInMessagesAndState()
    {
        var capture = new CapturingProvider();
        using var services = new ServiceCollection()
            .AddLogging(builder => builder.AddProvider(capture))
            .AddRedactingLogging()
            .BuildServiceProvider();
        var logger = services.GetRequiredService<ILogger<DiagnosticsTests>>();

        logger.LogInformation("Model said {answer}", "Lease clause 4 forbids subletting");

        var record = Assert.Single(capture.Records);
        Assert.DoesNotContain("Lease clause", record.Message);
        Assert.DoesNotContain("Lease clause", string.Join(" ", record.State.Select(pair => pair.Value)));
        Assert.Contains(TelemetryRedaction.Redacted, record.Message);
    }

    [Fact]
    public void TraceAttributesAreRedactedOnEnd()
    {
        var exported = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "DiagnosticsTests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("DiagnosticsTests");
        var processor = new RedactionFilter.RedactingActivityProcessor();

        using (var activity = source.StartActivity("chat"))
        {
            Assert.NotNull(activity);
            activity!.SetTag("gen_ai.prompt", "Summarise the secret clause");
            activity.SetTag("cookie", "session=8f3c9a2b7d");
            activity.SetTag("gen_ai.usage.input_tokens", 128);
            activity.SetTag("note", "Authorization: Bearer eyJhbGci");
            processor.OnEnd(activity);
            exported.Add(activity);
        }

        var tags = exported.Single().TagObjects.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal(TelemetryRedaction.Redacted, tags["gen_ai.prompt"]);
        Assert.Equal(TelemetryRedaction.Redacted, tags["cookie"]);
        Assert.Equal(128, tags["gen_ai.usage.input_tokens"]);
        Assert.DoesNotContain("eyJhbGci", (string)tags["note"]!);
    }

    [Fact]
    public void ConnectionStringIsReadFromConfigurationOnly()
    {
        var withoutString = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        withoutString.Configuration[DiagnosticsSetup.ConnectionStringSetting] = null;
        withoutString.AddAirlineDiagnostics();
        using (var app = withoutString.Build())
        {
            Assert.NotNull(app.Services.GetRequiredService<ILoggerFactory>());
            Assert.Null(app.Services.GetService<TracerProvider>());
            Assert.DoesNotContain(app.Services.GetServices<ILoggerProvider>(), p => p.GetType().FullName!.Contains("OpenTelemetry"));
        }

        var withString = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        withString.Configuration[DiagnosticsSetup.ConnectionStringSetting] =
            "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://localhost/";
        withString.AddAirlineDiagnostics();
        using (var app = withString.Build())
        {
            Assert.NotNull(app.Services.GetRequiredService<ILoggerFactory>());
            Assert.NotNull(app.Services.GetService<TracerProvider>());
            Assert.Contains(app.Services.GetServices<ILoggerProvider>(), p => p.GetType().FullName!.Contains("OpenTelemetry"));
        }
    }

    private sealed class CapturingProcessor : BaseProcessor<Activity>
    {
        public List<Dictionary<string, object?>> Tags { get; } = [];

        public override void OnEnd(Activity data) => Tags.Add(data.TagObjects.ToDictionary(t => t.Key, t => t.Value));
    }

    private sealed record CapturedRecord(string Message, IReadOnlyList<KeyValuePair<string, object?>> State, Exception? Exception);

    private sealed class CapturingProvider : ILoggerProvider
    {
        public List<CapturedRecord> Records { get; } = [];

        public List<string> Scopes { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(CapturingProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            provider.Scopes.Add(state.ToString() ?? string.Empty);
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var pairs = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            provider.Records.Add(new CapturedRecord(formatter(state, exception), pairs, exception));
        }
    }
}
