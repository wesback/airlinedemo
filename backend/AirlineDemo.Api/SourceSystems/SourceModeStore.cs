using System.Text.Json;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.SourceSystems;

public static class SourceModes
{
    public const string Action = "presenter_set_source_mode";
    public const string ReceiptPrefix = "source-mode:";
    public const string Available = "available";
    public static readonly string[] Sources = ["technical", "maintenance", "finance", "records", "contracts"];
    public static readonly string[] Modes = ["available", "unavailable", "stale", "conflicting"];
}

public enum SourceModeOutcome { Recorded, Replayed, CommandIdConflict, StaleRevision, UnknownSource, InvalidMode, ModeUnchanged }

public sealed record SourceModeReceipt(
    int ContractVersion,
    string CommandId,
    string CaseId,
    string SourceId,
    string PriorMode,
    string NewMode,
    long PriorRevision,
    long NewRevision,
    string AuditEventId);

public sealed record SourceModeResult(SourceModeOutcome Outcome, SourceModeReceipt? Receipt);

// Source modes are not stored separately: each mode is the newest source-mode audit event for that source,
// so a mode always agrees with the case revision and audit history it was written under.
public sealed class SourceModeStore(ICaseStateStore cases, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record CommandDetail(
        string CommandId,
        string CaseId,
        string SourceId,
        string PriorMode,
        string NewMode,
        long PriorRevision,
        long NewRevision);

    public IReadOnlyDictionary<string, string> ModesFor(CaseState state)
    {
        var modes = SourceModes.Sources.ToDictionary(s => s, _ => SourceModes.Available, StringComparer.Ordinal);
        foreach (var audit in state.Audit.Where(a => a.Action == SourceModes.Action))
        {
            var detail = ParseDetail(audit.Detail);
            if (detail is not null)
            {
                modes[detail.SourceId] = detail.NewMode;
            }
        }

        return modes;
    }

    public SourceModeResult Set(CaseScope scope, string actor, string commandId, long expectedRevision, string sourceId, string mode)
    {
        var state = cases.Read(scope);

        // A command ID is bound to its first payload: the same payload replays the original receipt, anything else conflicts.
        var existing = FindCommand(state, commandId);
        if (existing is { } found)
        {
            var same = string.Equals(found.Audit.Actor, actor, StringComparison.Ordinal)
                && found.Detail.CaseId == scope.CaseId
                && found.Detail.SourceId == sourceId
                && found.Detail.NewMode == mode
                && found.Detail.PriorRevision == expectedRevision;
            return same ? Result(SourceModeOutcome.Replayed, found) : new(SourceModeOutcome.CommandIdConflict, null);
        }

        if (!SourceModes.Sources.Contains(sourceId, StringComparer.Ordinal)) return new(SourceModeOutcome.UnknownSource, null);
        if (!SourceModes.Modes.Contains(mode, StringComparer.Ordinal)) return new(SourceModeOutcome.InvalidMode, null);
        if (expectedRevision != state.Revision) return new(SourceModeOutcome.StaleRevision, null);

        var prior = ModesFor(state)[sourceId];
        if (string.Equals(prior, mode, StringComparison.Ordinal)) return new(SourceModeOutcome.ModeUnchanged, null);

        var detail = new CommandDetail(commandId, scope.CaseId, sourceId, prior, mode, expectedRevision, expectedRevision + 1);
        try
        {
            var commit = cases.Commit(scope, expectedRevision, new CaseCommit(
                null,
                SourceModes.ReceiptPrefix + commandId,
                actor,
                SourceModes.Action,
                JsonSerializer.Serialize(detail, Json),
                time.GetUtcNow()));
            var stored = FindCommand(commit.State, commandId)!.Value;
            return Result(commit.Replayed ? SourceModeOutcome.Replayed : SourceModeOutcome.Recorded, stored);
        }
        catch (ReceiptConflictException)
        {
            return new(SourceModeOutcome.CommandIdConflict, null);
        }
        catch (RevisionConflictException)
        {
            return new(SourceModeOutcome.StaleRevision, null);
        }
    }

    private static SourceModeResult Result(SourceModeOutcome outcome, (AuditEvent Audit, CommandDetail Detail) found) =>
        new(outcome, new SourceModeReceipt(
            1,
            found.Detail.CommandId,
            found.Detail.CaseId,
            found.Detail.SourceId,
            found.Detail.PriorMode,
            found.Detail.NewMode,
            found.Detail.PriorRevision,
            found.Detail.NewRevision,
            "audit-" + found.Audit.Sequence));

    private static (AuditEvent Audit, CommandDetail Detail)? FindCommand(CaseState state, string commandId)
    {
        foreach (var audit in state.Audit)
        {
            if (audit.Action != SourceModes.Action) continue;
            var detail = ParseDetail(audit.Detail);
            if (detail is not null && detail.CommandId == commandId) return (audit, detail);
        }

        return null;
    }

    private static CommandDetail? ParseDetail(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<CommandDetail>(text, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
