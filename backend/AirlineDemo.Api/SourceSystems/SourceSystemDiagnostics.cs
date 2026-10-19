using System.Diagnostics;
using AirlineDemo.Api.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AirlineDemo.Api.SourceSystems;

public static class SourceSystemDiagnostics
{
    public static void ModeChangeAccepted(ILogger logger, string environment) =>
        Emit(logger, TelemetryRedaction.SourceEvents.ModeChangeAccepted, LogLevel.Information, environment);

    public static void ModeChangeAccepted(ILogger logger, string environment, string sourceId, string caseId, long durationMilliseconds) =>
        EmitWithContext(logger, TelemetryRedaction.SourceEvents.ModeChangeAccepted, LogLevel.Information, environment,
            sourceId, caseId, "accepted", durationMilliseconds);

    public static void CrossCaseReadDenied(ILogger logger, string environment) =>
        Emit(logger, TelemetryRedaction.SourceEvents.CrossCaseReadDenied, LogLevel.Warning, environment);

    public static void CrossCaseReadDenied(ILogger logger, string environment, string sourceId, string caseId, long durationMilliseconds) =>
        EmitWithContext(logger, TelemetryRedaction.SourceEvents.CrossCaseReadDenied, LogLevel.Warning, environment,
            sourceId, caseId, "denied", durationMilliseconds);

    public static void OutOfRoleReadDenied(ILogger logger, string environment) =>
        Emit(logger, TelemetryRedaction.SourceEvents.OutOfRoleReadDenied, LogLevel.Warning, environment);

    public static void OutOfRoleReadDenied(ILogger logger, string environment, string sourceId, string caseId, long durationMilliseconds) =>
        EmitWithContext(logger, TelemetryRedaction.SourceEvents.OutOfRoleReadDenied, LogLevel.Warning, environment,
            sourceId, caseId, "denied", durationMilliseconds);

    public static void FixtureRejected(ILogger logger, string environment) =>
        Emit(logger, TelemetryRedaction.SourceEvents.FixtureRejected, LogLevel.Error, environment);

    public static void FixtureRejected(ILogger logger, string environment, long durationMilliseconds) =>
        EmitWithContext(logger, TelemetryRedaction.SourceEvents.FixtureRejected, LogLevel.Error, environment,
            "source-fixture-pack", "source-fixture-pack", "rejected", durationMilliseconds);

    public static void SourceUnavailable(ILogger logger, string environment) =>
        Emit(logger, TelemetryRedaction.SourceEvents.SourceUnavailable, LogLevel.Warning, environment);

    public static void SourceUnavailable(ILogger logger, string environment, string sourceId, string caseId, long durationMilliseconds) =>
        EmitWithContext(logger, TelemetryRedaction.SourceEvents.SourceUnavailable, LogLevel.Warning, environment,
            sourceId, caseId, "unavailable", durationMilliseconds);

    public static void ReadLimited(ILogger logger, string environment) =>
        Emit(logger, TelemetryRedaction.SourceEvents.ReadLimited, LogLevel.Warning, environment);

    public static void ReadLimited(ILogger logger, string environment, string sourceId, string caseId, long durationMilliseconds) =>
        EmitWithContext(logger, TelemetryRedaction.SourceEvents.ReadLimited, LogLevel.Warning, environment,
            sourceId, caseId, "limited", durationMilliseconds);

    public static void RecordLimitRejected(ILogger logger, string environment) =>
        Emit(logger, TelemetryRedaction.SourceEvents.RecordLimitRejected, LogLevel.Warning, environment);

    public static void RecordLimitRejected(ILogger logger, string environment, string sourceId, string caseId, long durationMilliseconds) =>
        EmitWithContext(logger, TelemetryRedaction.SourceEvents.RecordLimitRejected, LogLevel.Warning, environment,
            sourceId, caseId, "rejected", durationMilliseconds);

    public static void ReadTimedOut(ILogger logger, string environment) =>
        Emit(logger, TelemetryRedaction.SourceEvents.ReadTimedOut, LogLevel.Warning, environment);

    public static void ReadTimedOut(ILogger logger, string environment, string sourceId, string caseId, long durationMilliseconds) =>
        EmitWithContext(logger, TelemetryRedaction.SourceEvents.ReadTimedOut, LogLevel.Warning, environment,
            sourceId, caseId, "timed_out", durationMilliseconds);

    public static void AuditWriteFailed(ILogger logger, string environment) =>
        Emit(logger, TelemetryRedaction.SourceEvents.AuditWriteFailed, LogLevel.Error, environment);

    public static void AuditWriteFailed(ILogger logger, string environment, string sourceId, string caseId, long durationMilliseconds) =>
        EmitWithContext(logger, TelemetryRedaction.SourceEvents.AuditWriteFailed, LogLevel.Error, environment,
            sourceId, caseId, "failed", durationMilliseconds);

    internal static long ElapsedMilliseconds(long startTimestamp) =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);

    private static void Emit(ILogger logger, string eventName, LogLevel level, string environment) =>
        TelemetryRedaction.EmitSourceSystemEvent(logger, eventName, level, environment);

    private static void EmitWithContext(
        ILogger logger,
        string eventName,
        LogLevel level,
        string environment,
        string sourceId,
        string caseId,
        string outcome,
        long durationMilliseconds) =>
        TelemetryRedaction.EmitSourceSystemEvent(logger, eventName, level, environment,
            sourceId, caseId, outcome, durationMilliseconds);
}
