using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AirlineDemo.Api.Diagnostics;

public static class CasePackDiagnostics
{
    public const int ContractVersion = 1;
    public static IReadOnlySet<string> AllowedEventTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
        { "pack_load", "case_list", "scene_projection", "reset" };
    public static IReadOnlySet<string> AllowedOutcomes { get; } = new HashSet<string>(StringComparer.Ordinal)
        { "success", "failed", "validation_failed", "denied", "invalid", "conflict", "replayed", "rate_limited" };
    public static IReadOnlySet<string> ApprovedFields { get; } = new HashSet<string>(StringComparer.Ordinal)
        { "caseId", "commandId", "packVersion", "contractVersion", "outcome", "durationMs", "eventType" };
    private static readonly ActivitySource ActivitySource = new(TelemetryRedaction.ServiceName);

    public static IReadOnlyDictionary<string, object?> Emit(ILogger logger, string eventType, string outcome, long durationMs,
        string? caseId = null, string? commandId = null, string? packVersion = null)
    {
        if (!AllowedEventTypes.Contains(eventType)) throw new ArgumentException("Unknown case-pack event type.", nameof(eventType));
        if (!AllowedOutcomes.Contains(outcome)) throw new ArgumentException("Unknown case-pack outcome.", nameof(outcome));
        if (durationMs < 0) throw new ArgumentOutOfRangeException(nameof(durationMs));
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        { ["contractVersion"] = ContractVersion, ["outcome"] = outcome, ["durationMs"] = durationMs, ["eventType"] = eventType };
        if (eventType is "pack_load" && IsPackVersion(packVersion)) fields["packVersion"] = packVersion;
        if (eventType is "scene_projection" or "reset")
        {
            if (IsCaseId(caseId)) fields["caseId"] = caseId;
        }
        if (eventType == "reset" && IsCommandId(commandId)) fields["commandId"] = commandId;
        logger.Log(LogLevel.Information, new EventId(0, eventType), fields, null,
            static (values, _) => "case-pack diagnostic event");
        using var activity = ActivitySource.StartActivity(eventType, ActivityKind.Internal);
        if (activity is not null) foreach (var field in fields) activity.SetTag(field.Key, field.Value);
        return fields;
    }

    public static IReadOnlyDictionary<string, object?> PackLoad(ILogger logger, string outcome, long durationMs, string? packVersion = null) => Emit(logger, "pack_load", outcome, durationMs, packVersion: packVersion);
    public static IReadOnlyDictionary<string, object?> CaseList(ILogger logger, string outcome, long durationMs) => Emit(logger, "case_list", outcome, durationMs);
    public static IReadOnlyDictionary<string, object?> SceneProjection(ILogger logger, string outcome, long durationMs, string? caseId = null) => Emit(logger, "scene_projection", outcome, durationMs, caseId: caseId);
    public static IReadOnlyDictionary<string, object?> Reset(ILogger logger, string outcome, long durationMs, string? caseId = null, string? commandId = null) => Emit(logger, "reset", outcome, durationMs, caseId, commandId);
    public static long ElapsedMilliseconds(long startTimestamp) => Math.Max(0, (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
    private static bool IsCaseId(string? value) => value is { Length: > 0 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    private static bool IsCommandId(string? value) => value is { Length: > 0 and <= 64 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool IsPackVersion(string? value) => value is { Length: > 0 and <= 64 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
