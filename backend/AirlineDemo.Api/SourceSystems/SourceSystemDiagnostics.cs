using AirlineDemo.Api.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AirlineDemo.Api.SourceSystems;

public static class SourceSystemDiagnostics
{
    public static void ModeChangeAccepted(ILogger logger, string environment) =>
        Emit(logger, TelemetryRedaction.SourceEvents.ModeChangeAccepted, LogLevel.Information, environment);

    public static void CrossCaseReadDenied(ILogger logger, string environment) =>
        Emit(logger, TelemetryRedaction.SourceEvents.CrossCaseReadDenied, LogLevel.Warning, environment);

    public static void OutOfRoleReadDenied(ILogger logger, string environment) =>
        Emit(logger, TelemetryRedaction.SourceEvents.OutOfRoleReadDenied, LogLevel.Warning, environment);

    public static void FixtureRejected(ILogger logger, string environment) =>
        Emit(logger, TelemetryRedaction.SourceEvents.FixtureRejected, LogLevel.Error, environment);

    public static void SourceUnavailable(ILogger logger, string environment) =>
        Emit(logger, TelemetryRedaction.SourceEvents.SourceUnavailable, LogLevel.Warning, environment);

    public static void ReadLimited(ILogger logger, string environment) =>
        Emit(logger, TelemetryRedaction.SourceEvents.ReadLimited, LogLevel.Warning, environment);

    public static void RecordLimitRejected(ILogger logger, string environment) =>
        Emit(logger, TelemetryRedaction.SourceEvents.RecordLimitRejected, LogLevel.Warning, environment);

    public static void ReadTimedOut(ILogger logger, string environment) =>
        Emit(logger, TelemetryRedaction.SourceEvents.ReadTimedOut, LogLevel.Warning, environment);

    public static void AuditWriteFailed(ILogger logger, string environment) =>
        Emit(logger, TelemetryRedaction.SourceEvents.AuditWriteFailed, LogLevel.Error, environment);

    private static void Emit(ILogger logger, string eventName, LogLevel level, string environment) =>
        TelemetryRedaction.EmitSourceSystemEvent(logger, eventName, level, environment);
}
