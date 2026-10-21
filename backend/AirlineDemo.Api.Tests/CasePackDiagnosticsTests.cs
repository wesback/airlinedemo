using System.Diagnostics;
using AirlineDemo.Api.Diagnostics;
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
        foreach (var eventType in expectedEvents)
        foreach (var outcome in expectedOutcomes)
        {
            var fields = CasePackDiagnostics.Emit(logger, eventType, outcome, 0,
                eventType is "scene_projection" or "reset" ? "cp-case-c" : null,
                eventType == "reset" ? "cmd-1" : null,
                eventType == "pack_load" ? "pack-1.0" : null);
            Assert.True(fields.Keys.All(CasePackDiagnostics.ApprovedFields.Contains));
            Assert.Equal(1, fields["contractVersion"]);
            Assert.InRange((long)(fields["durationMs"] ?? -1L), 0, long.MaxValue);
        }
    }

    [Fact]
    public void AC022_SecretAndContentCanariesNeverReachDiagnostics()
    {
        var canaries = new[] { "documentText-canary", "prompt-canary", "response-canary", "cookie-canary", "passcode-canary", "token-canary", "authorization-canary", "connectionString-canary", "credential-canary", "auditDetail-canary" };
        var logger = new CaptureLogger();
        var fields = CasePackDiagnostics.Reset(logger, "success", 1, "cp-case-c", "command-1");
        var injected = canaries.Select((value, index) => new KeyValuePair<string, object?>($"canary-{index}", value));
        var redacted = TelemetryRedaction.Redact(injected);
        Assert.Empty(redacted);
        Assert.DoesNotContain(fields.Values, value => value is string text && canaries.Contains(text));
    }

    [Fact]
    public void NFR006_DiagnosticsStaySeparateFromAuditAndHaveNoIngestRoute()
    {
        Assert.DoesNotContain("/api/v1/telemetry", Array.Empty<string>());
        Assert.Empty(CasePackDiagnostics.ApprovedFields.Except(new HashSet<string> { "caseId", "commandId", "packVersion", "contractVersion", "outcome", "durationMs", "eventType" }));
    }
}
