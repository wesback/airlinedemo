using System.Text.Json;
using System.Text.RegularExpressions;
using AirlineDemo.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AirlineDemo.Api.Copilot;

/// <summary>
/// One append-only detail row for a copilot invocation: record identities, the screening verdict, the proposal link and
/// the release gate result. Every value is an identifier or a code; no field holds record text, a question, an answer
/// or a release note.
/// </summary>
public sealed record CopilotInvocationDetail(
    Guid InvocationId, string Kind, DateTimeOffset At, string? SourceId = null, string? RecordId = null, int? RecordVersion = null,
    string? ScreeningState = null, IReadOnlyList<string>? FlaggedFields = null, Guid? ProposalId = null, string? Gate = null,
    string? GateResult = null, IReadOnlyList<string>? BlockerCodes = null)
{
    public bool Equals(CopilotInvocationDetail? other) =>
        other is not null && InvocationId == other.InvocationId && Kind == other.Kind && At == other.At && SourceId == other.SourceId
        && RecordId == other.RecordId && RecordVersion == other.RecordVersion && ScreeningState == other.ScreeningState
        && (FlaggedFields ?? []).SequenceEqual(other.FlaggedFields ?? []) && ProposalId == other.ProposalId && Gate == other.Gate
        && GateResult == other.GateResult && (BlockerCodes ?? []).SequenceEqual(other.BlockerCodes ?? []);

    public override int GetHashCode() => HashCode.Combine(InvocationId, Kind, At, RecordId, ProposalId);
}

public static partial class CopilotInvocationDetails
{
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "record_read", "injection_detected", "screening_unavailable", "proposal_created", "proposal_decided", "release_gate",
    };

    public static readonly IReadOnlySet<string> ScreeningStates = new HashSet<string>(StringComparer.Ordinal) { ScreeningVerdict.Clear, ScreeningVerdict.Detected, ScreeningVerdict.UnavailableState };

    public const string FinalReleaseGate = "final-release";

    public const int MaxListEntries = 32;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    /// <summary>Rejects any row outside the closed contract, so free text can never reach the store.</summary>
    public static void Validate(CopilotInvocationDetail detail)
    {
        if (!Kinds.Contains(detail.Kind)) throw new ArgumentOutOfRangeException(nameof(detail), "Unknown detail kind.");
        if (detail.ScreeningState is { } state && !ScreeningStates.Contains(state))
            throw new ArgumentOutOfRangeException(nameof(detail), "Unknown screening state.");
        if (detail.Gate is { } gate && gate != FinalReleaseGate) throw new ArgumentOutOfRangeException(nameof(detail), "Unknown gate.");
        if (detail.GateResult is { } result && result is not ("passed" or "replayed") && !CopilotInvocationLedger.ResultCodes.Contains(result))
            throw new ArgumentOutOfRangeException(nameof(detail), "Unknown gate result.");
        if (detail.RecordVersion is < 1) throw new ArgumentOutOfRangeException(nameof(detail), "Record version must be positive.");
        foreach (var value in new[] { detail.SourceId, detail.RecordId })
            if (value is not null && !Identifier().IsMatch(value)) throw new ArgumentOutOfRangeException(nameof(detail), "Not an identifier.");
        foreach (var list in new[] { detail.FlaggedFields, detail.BlockerCodes })
        {
            if (list is null) continue;
            if (list.Count > MaxListEntries) throw new ArgumentOutOfRangeException(nameof(detail), "Too many entries.");
            if (list.Any(value => value is null || !Identifier().IsMatch(value)))
                throw new ArgumentOutOfRangeException(nameof(detail), "Not an identifier.");
        }
    }
}

/// <summary>Append-only: the store can add a row and list a case's rows, and nothing else.</summary>
public interface ICopilotInvocationDetailStore
{
    Task AppendAsync(IReadOnlyList<CopilotInvocationDetail> details, CancellationToken cancellationToken);
    Task<IReadOnlyList<CopilotInvocationDetail>> ListForCaseAsync(string authorizedCaseRef, CancellationToken cancellationToken);
}

public sealed class InMemoryCopilotInvocationDetailStore(ICopilotInvocationStore invocations) : ICopilotInvocationDetailStore
{
    private readonly Lock _gate = new();
    private readonly List<CopilotInvocationDetail> _details = [];

    public IReadOnlyList<CopilotInvocationDetail> Details { get { lock (_gate) return [.. _details]; } }

    public Task AppendAsync(IReadOnlyList<CopilotInvocationDetail> details, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var detail in details) CopilotInvocationDetails.Validate(detail);
        lock (_gate) _details.AddRange(details);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<CopilotInvocationDetail>> ListForCaseAsync(string authorizedCaseRef, CancellationToken cancellationToken)
    {
        var ids = (await invocations.ReadCaseAsync(authorizedCaseRef, cancellationToken)).Select(record => record.InvocationId).ToHashSet();
        lock (_gate) return [.. _details.Where(detail => ids.Contains(detail.InvocationId)).OrderBy(detail => detail.At)];
    }
}

public sealed class CopilotInvocationDetailRow
{
    public Guid DetailId { get; set; }
    public Guid InvocationId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public long AtTicks { get; set; }
    public string? SourceId { get; set; }
    public string? RecordId { get; set; }
    public int? RecordVersion { get; set; }
    public string? ScreeningState { get; set; }
    public string? FlaggedFields { get; set; }
    public Guid? ProposalId { get; set; }
    public string? Gate { get; set; }
    public string? GateResult { get; set; }
    public string? BlockerCodes { get; set; }
}

public sealed class SqlCopilotInvocationDetailStore(DbContextOptions<CaseStateDbContext> options) : ICopilotInvocationDetailStore
{
    public async Task AppendAsync(IReadOnlyList<CopilotInvocationDetail> details, CancellationToken cancellationToken)
    {
        await using var db = new CaseStateDbContext(options);
        db.CopilotInvocationDetails.AddRange(details.Select(ToRow));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Validates and maps a detail; also used by a store that appends it inside its own transaction.</summary>
    internal static CopilotInvocationDetailRow ToRow(CopilotInvocationDetail detail)
    {
        CopilotInvocationDetails.Validate(detail);
        return new()
        {
            DetailId = Guid.NewGuid(), InvocationId = detail.InvocationId, Kind = detail.Kind, AtTicks = detail.At.UtcTicks,
            SourceId = detail.SourceId, RecordId = detail.RecordId, RecordVersion = detail.RecordVersion, ScreeningState = detail.ScreeningState,
            FlaggedFields = detail.FlaggedFields is null ? null : JsonSerializer.Serialize(detail.FlaggedFields), ProposalId = detail.ProposalId,
            Gate = detail.Gate, GateResult = detail.GateResult,
            BlockerCodes = detail.BlockerCodes is null ? null : JsonSerializer.Serialize(detail.BlockerCodes),
        };
    }

    public async Task<IReadOnlyList<CopilotInvocationDetail>> ListForCaseAsync(string authorizedCaseRef, CancellationToken cancellationToken)
    {
        await using var db = new CaseStateDbContext(options);
        var rows = await db.CopilotInvocationDetails.AsNoTracking()
            .Where(detail => db.CopilotInvocations.Any(row => row.InvocationId == detail.InvocationId && row.AuthorizedCaseRef == authorizedCaseRef))
            .OrderBy(detail => detail.AtTicks).ToListAsync(cancellationToken);
        return [.. rows.Select(row => new CopilotInvocationDetail(row.InvocationId, row.Kind, new DateTimeOffset(row.AtTicks, TimeSpan.Zero),
            row.SourceId, row.RecordId, row.RecordVersion, row.ScreeningState, List(row.FlaggedFields), row.ProposalId, row.Gate, row.GateResult,
            List(row.BlockerCodes)))];
    }

    private static string[]? List(string? json) => json is null ? null : JsonSerializer.Deserialize<string[]>(json);
}
