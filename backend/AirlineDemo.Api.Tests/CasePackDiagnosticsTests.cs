using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using AirlineDemo.Api.Diagnostics;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AirlineDemo.Api.Tests;

public sealed class CasePackDiagnosticsTests
{
    private sealed class CaptureLogger : ILogger
    {
        public List<IReadOnlyDictionary<string, object?>> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                Events.Add(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void AC022_OperationAndOutcomeTableEmitsOnlyApprovedFields()
    {
        var logger = new CaptureLogger();
        var expectedEvents = new[] { "pack_load", "case_list", "scene_projection", "reset" };
        var expectedOutcomes = new[] { "success", "failed", "validation_failed", "denied", "invalid", "conflict", "replayed", "rate_limited" };
        Assert.Equal(expectedEvents.ToHashSet(), CasePackDiagnostics.AllowedEventTypes);
        Assert.Equal(expectedOutcomes.ToHashSet(), CasePackDiagnostics.AllowedOutcomes);

        IReadOnlyDictionary<string, object?>[] emissions =
        [
            .. expectedOutcomes.Select(outcome => CasePackDiagnostics.PackLoad(logger, outcome, 1, "pack-1.0")),
            .. expectedOutcomes.Select(outcome => CasePackDiagnostics.CaseList(logger, outcome, 2)),
            .. expectedOutcomes.Select(outcome => CasePackDiagnostics.SceneProjection(logger, outcome, 3, "cp-case-c")),
            .. expectedOutcomes.Select(outcome => CasePackDiagnostics.Reset(logger, outcome, 4, "cp-case-c", "cmd-1")),
        ];

        Assert.Equal(emissions.Length, logger.Events.Count);
        for (var index = 0; index < emissions.Length; index++)
        {
            var fields = emissions[index];
            var eventIndex = index / expectedOutcomes.Length;
            var outcome = expectedOutcomes[index % expectedOutcomes.Length];
            var eventType = expectedEvents[eventIndex];
            var expectedKeys = new HashSet<string>(StringComparer.Ordinal)
            {
                "contractVersion", "outcome", "durationMs", "eventType"
            };
            if (eventType is "scene_projection" or "reset") expectedKeys.Add("caseId");
            if (eventType == "reset") expectedKeys.Add("commandId");
            if (eventType == "pack_load") expectedKeys.Add("packVersion");

            Assert.Equal(expectedKeys.Order(), fields.Keys.Order());
            Assert.Equal(eventType, fields["eventType"]);
            Assert.Equal(outcome, fields["outcome"]);
            Assert.Equal(1, fields["contractVersion"]);
            Assert.True(fields["durationMs"] is long duration && duration >= 0);
            Assert.True(fields.Keys.All(CasePackDiagnostics.ApprovedFields.Contains));
        }

        var withoutIdentifiers = CasePackDiagnostics.CaseList(logger, "success", 0);
        Assert.DoesNotContain("caseId", withoutIdentifiers.Keys);
        Assert.DoesNotContain("commandId", withoutIdentifiers.Keys);
        Assert.DoesNotContain("packVersion", withoutIdentifiers.Keys);
    }

    [Fact]
    public void AC022_SecretAndContentCanariesNeverReachDiagnostics()
    {
        var propertyNames = new[] { "documentText", "prompt", "response", "cookie", "passcode", "token", "authorization", "connectionString", "credential", "auditDetail" };
        var canaries = propertyNames.Select((name, index) => $"{name}-canary-{index}").ToArray();
        var injected = propertyNames.Zip(canaries, (name, value) => new KeyValuePair<string, object?>($"{name}-canary-property", value));
        var logger = new CaptureLogger();
        var fields = CasePackDiagnostics.Reset(logger, "success", 1, "cp-case-c", "command-1");

        foreach (var propertyName in propertyNames)
        {
            var redacted = TelemetryRedaction.Redact([new KeyValuePair<string, object?> (propertyName, "secret-canary")]);
            Assert.Empty(redacted);
            Assert.DoesNotContain(propertyName, fields.Keys, StringComparer.OrdinalIgnoreCase);
        }
        Assert.Empty(TelemetryRedaction.Redact(injected));

        var activityTags = new List<KeyValuePair<string, string?>>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetryRedaction.ServiceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activityTags.AddRange(activity.Tags.Select(tag => new KeyValuePair<string, string?>(tag.Key, tag.Value))),
        };
        ActivitySource.AddActivityListener(listener);
        CasePackDiagnostics.Reset(logger, "success", 1, "cp-case-c", "command-1");

        Assert.DoesNotContain(activityTags, tag => propertyNames.Contains(tag.Key, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(activityTags.Select(tag => tag.Value), value => canaries.Contains(value));
        Assert.DoesNotContain(fields.Values, value => value is string text && canaries.Contains(text));
        Assert.DoesNotContain(logger.Events.SelectMany(e => e.Keys), key => propertyNames.Any(name => key.Contains(name, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(logger.Events.SelectMany(e => e.Values).OfType<string>(), value => canaries.Contains(value));
    }

    [Fact]
    public async Task NFR006_DiagnosticsStaySeparateFromAuditAndHaveNoIngestRoute()
    {
        var logger = new CaptureLogger();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Passcode"] = "nfr006-passcode",
                ["Persistence:EvidenceProvider"] = "Memory",
            }));
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(new CaptureLoggerProvider(logger)));
        });
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var caseStore = factory.Services.GetRequiredService<AirlineDemo.Api.Persistence.ICaseStateStore>();
        if (caseStore.OwnerOf("cp-case-c") is null)
            caseStore.CreateCase("cp-case-c", "nfr006-owner");
        var auditScope = caseStore.AuthorizeCase("cp-case-c", "nfr006-owner");
        var auditCountBefore = caseStore.ReadAudit(auditScope).Count;

        using var response = await client.PostAsJsonAsync("/api/v1/telemetry", new
        {
            documentText = "document-canary",
            prompt = "prompt-canary",
            response = "response-canary",
            cookie = "cookie-canary",
            passcode = "passcode-canary",
            token = "token-canary",
            authorization = "authorization-canary",
            connectionString = "connection-string-canary",
            credential = "credential-canary",
            auditDetail = "audit-detail-canary",
        }, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var auditCountAfter = caseStore.ReadAudit(auditScope).Count;
        Assert.Equal(auditCountBefore, auditCountAfter);
        Assert.DoesNotContain(logger.Events.SelectMany(e => e.Keys), key => key.Equals("auditDetail", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Events.SelectMany(e => e.Values).OfType<string>(), value => value.Contains("canary", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CaptureLoggerProvider(CaptureLogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }
}
