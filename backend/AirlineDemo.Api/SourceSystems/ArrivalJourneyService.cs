using System.Text.Json;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.SourceSystems;

public sealed record ArrivalStatus(
    int ContractVersion,
    string CaseId,
    long CaseRevision,
    string State,
    IReadOnlyList<string> MissingSources,
    bool CanComplete);

public sealed record ArrivalCommand(string? CommandId, long? ExpectedRevision);

public sealed record ArrivalReceipt(
    int ContractVersion,
    string CommandId,
    string CaseId,
    string State,
    long PriorRevision,
    long NewRevision,
    string AuditEventId);

public sealed record ArrivalSourceReadAuditDetail(
    int ContractVersion,
    string SourceId,
    string Role,
    string Outcome,
    string? ErrorCode,
    IReadOnlyList<SourceReadRecordRef> RecordRefs,
    ArrivalRetrievalReceipt? RetrievalReceipt = null);

public sealed record ArrivalRetrievalReceipt(
    int ContractVersion,
    string CommandId,
    string CaseId,
    string SourceId,
    string Outcome,
    long PriorRevision,
    long NewRevision,
    string AuditEventId,
    IReadOnlyList<SourceReadRecordRef> RecordRefs);

public sealed record ArrivalCompletionAuditDetail(
    ArrivalReceipt Receipt,
    int BasisVersion,
    string BasisDigest);

public enum ArrivalStartOutcome { Started, Replayed, CommandIdConflict, StaleRevision, AlreadyStarted, Completed }

public sealed record ArrivalStartResult(ArrivalStartOutcome Outcome, ArrivalReceipt? Receipt);

public enum ArrivalRetrievalOutcome { Pinned, Empty, Stale, Conflicting, Replayed, CommandIdConflict, StaleRevision, NotStarted, Completed }

public sealed record ArrivalRetrievalResult(ArrivalRetrievalOutcome Outcome, ArrivalRetrievalReceipt? Receipt);

public enum ArrivalCompletionOutcome { Completed, Replayed, CommandIdConflict, StaleRevision, NotStarted, MissingSources }

public sealed record ArrivalCompletionResult(
    ArrivalCompletionOutcome Outcome,
    ArrivalReceipt? Receipt,
    IReadOnlyList<string> MissingSources);

public sealed class ArrivalJourneyService(ICaseStateStore cases, TimeProvider time, SourceModeStore modes)
{
    public const string CaseId = "case-001";
    public const string Owner = "presenter";
    public const string EnrollmentAction = "arrival.enrolled";
    public const string StartAction = "arrival.started";
    public const string SourceReadAction = "arrival.source_read";
    public const string CompletionAction = "arrival.completed";
    private const string StartReceiptPrefix = "arrival-start:";
    private const string RetrievalReceiptPrefix = "arrival-retrieve:";
    private const string CompletionReceiptPrefix = "arrival-complete:";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void EnrollOptInCase(IConfiguration configuration, ICaseStateStore store)
    {
        if (!bool.TryParse(configuration["Demo:ArrivalJourney"], out var enabled) || !enabled
            || store.OwnerOf(CaseId) != Owner) return;

        var scope = store.AuthorizeCase(CaseId, Owner);
        var state = store.Read(scope);
        if (state.Revision != 0) return;

        try
        {
            store.Commit(scope, 0, new CaseCommit(null, null, "arrival-journey-seeder", EnrollmentAction,
                JsonSerializer.Serialize(new { contractVersion = 1, caseId = CaseId, enrolledFromRevision = 0 }, Json),
                DateTimeOffset.UtcNow));
        }
        catch (RevisionConflictException)
        {
            // An intervening operation wins; never enroll a case after it has started changing.
        }
    }

    public bool IsEnrolled(CaseState state) => state.CaseId == CaseId
        && string.Equals(state.Owner, Owner, StringComparison.Ordinal)
        && state.Audit.Any(item => item.Action == EnrollmentAction);

    public ArrivalStatus Status(CaseState state)
    {
        var completed = state.Audit.Any(item => item.Action == CompletionAction);
        var started = completed || state.Audit.Any(item => item.Action == StartAction);
        var missingSources = completed ? [] : MissingSources(state);
        return new ArrivalStatus(1, state.CaseId, state.Revision,
            completed ? "ready" : started ? "collecting" : "not_started",
            missingSources,
            CanComplete: started && !completed && missingSources.Count == 0);
    }

    public void RecordSourceRead(CaseScope scope, string actor, string role, GovernedSourceReadResult read)
    {
        var refs = read.ErrorCode is null && read.Outcome == "success" && read.Count > 0 ? read.RecordRefs : [];
        var detail = new ArrivalSourceReadAuditDetail(1, read.SourceId, role, read.Outcome, read.ErrorCode, refs);
        var serialized = JsonSerializer.Serialize(detail, Json);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var state = cases.Read(scope);
            if (!IsEnrolled(state))
                throw new InvalidOperationException("Arrival enrollment changed while recording source activity.");
            try
            {
                cases.Commit(scope, state.Revision, new CaseCommit(null, null, actor, SourceReadAction,
                    serialized, time.GetUtcNow()));
                return;
            }
            catch (RevisionConflictException) when (attempt < 2)
            {
                // Preserve the source result and retry only its audit against the latest revision.
            }
        }

        throw new RevisionConflictException();
    }

    public IReadOnlyList<string> MissingSources(CaseState state)
    {
        var pins = state.Basis.Count == 0
            ? []
            : EvidenceIntake.ReadSourceBasisPins(state.Basis[^1].Content);
        var currentModes = modes.ModesFor(state);
        return SourceModes.Sources.Where(source =>
            currentModes[source] != SourceModes.Available
            || !pins.Any(pin => string.Equals(pin.SourceId, source, StringComparison.Ordinal))).ToArray();
    }

    public ArrivalRetrievalReceipt? FindRetrieval(CaseState state, string commandId, out AuditEvent? audit)
    {
        foreach (var item in state.Audit)
        {
            if (item.Action != SourceReadAction) continue;
            try
            {
                var detail = JsonSerializer.Deserialize<ArrivalSourceReadAuditDetail>(item.Detail, Json);
                if (detail?.RetrievalReceipt?.CommandId == commandId)
                {
                    audit = item;
                    return detail.RetrievalReceipt;
                }
            }
            catch (JsonException)
            {
                // Non-retrieval source-read audit detail is not a command receipt.
            }
        }

        audit = null;
        return null;
    }

    public ArrivalRetrievalResult? CheckRetrievalReplay(
        CaseState state, string commandId, string actor, string role, string sourceId, long expectedRevision)
    {
        var existing = FindRetrieval(state, commandId, out var audit);
        if (existing is null || audit is null) return null;
        return SameRetrieval(existing, audit, new CaseScope(state.CaseId), actor, role, sourceId, expectedRevision)
            ? new(ArrivalRetrievalOutcome.Replayed, existing)
            : new(ArrivalRetrievalOutcome.CommandIdConflict, null);
    }

    public ArrivalRetrievalResult CommitRetrieval(
        CaseScope scope,
        string actor,
        string role,
        string commandId,
        long expectedRevision,
        GovernedSourceReadResult read,
        IReadOnlyList<SourceBasisPin> sourcePins)
    {
        var state = cases.Read(scope);
        if (state.Revision != expectedRevision)
        {
            RecordSourceRead(scope, actor, role, read with { Outcome = "stale_revision", ErrorCode = "stale_revision", RecordRefs = [] });
            return new(ArrivalRetrievalOutcome.StaleRevision, null);
        }

        if (read.ErrorCode is not null)
            throw new ArgumentException("Only a completed source read can be committed as an arrival retrieval.", nameof(read));

        var pins = state.Basis.Count == 0
            ? []
            : EvidenceIntake.ReadSourceBasisPins(state.Basis[^1].Content);
        var mergedPins = pins.Where(pin => pin.SourceId != read.SourceId).Concat(sourcePins).ToArray();
        var nextBasis = read.Outcome == "success"
            ? EvidenceIntake.CreateSourceBasis(mergedPins)
            : null;
        var receiptOutcome = read.Outcome switch
        {
            "success" => "pinned",
            "empty" => "empty",
            "stale" => "stale",
            "conflicting" => "conflicting",
            _ => throw new ArgumentOutOfRangeException(nameof(read), "The source read outcome cannot be pinned."),
        };
        var recordRefs = receiptOutcome == "pinned"
            ? sourcePins.Select(pin => new SourceReadRecordRef(pin.SourceId, pin.RecordId, pin.RecordVersion)).ToArray()
            : [];
        var receipt = new ArrivalRetrievalReceipt(1, commandId, scope.CaseId, read.SourceId, receiptOutcome,
            state.Revision, state.Revision + 1, "audit-" + (state.Audit.Count + 1), recordRefs);
        var detail = JsonSerializer.Serialize(new ArrivalSourceReadAuditDetail(
            1, read.SourceId, role, receiptOutcome, null, recordRefs, receipt), Json);
        var basis = nextBasis is null
            ? null
            : CaseStateStore.CreateBasisVersion(checked(state.Basis.Count + 1), nextBasis.Content,
                state.Basis.Count == 0 ? string.Empty : state.Basis[^1].BasisDigest);

        try
        {
            var commit = cases.Commit(scope, expectedRevision, new CaseCommit(basis,
                RetrievalReceiptPrefix + commandId, actor, SourceReadAction, detail, time.GetUtcNow()));
            var stored = FindRetrieval(commit.State, commandId, out _);
            return stored is null
                ? throw new InvalidDataException("The committed arrival retrieval receipt was not persisted.")
                : new(commit.Replayed ? ArrivalRetrievalOutcome.Replayed : RetrievalOutcomeFor(stored.Outcome), stored);
        }
        catch (ReceiptConflictException)
        {
            var latest = cases.Read(scope);
            var existing = FindRetrieval(latest, commandId, out var existingAudit);
            if (existing is not null && SameRetrieval(existing, existingAudit!, scope, actor, role, read.SourceId, expectedRevision))
                return new(ArrivalRetrievalOutcome.Replayed, existing);
            return new(ArrivalRetrievalOutcome.CommandIdConflict, null);
        }
        catch (RevisionConflictException)
        {
            var stale = read with { Outcome = "stale_revision", ErrorCode = "stale_revision", RecordRefs = [] };
            RecordSourceRead(scope, actor, role, stale);
            return new(ArrivalRetrievalOutcome.StaleRevision, null);
        }
    }

    public ArrivalCompletionResult Complete(CaseScope scope, string actor, ArrivalCommand command)
    {
        var state = cases.Read(scope);
        var existing = FindCompletion(state, command.CommandId!);
        if (existing is { } existingCompletion)
        {
            var same = existingCompletion.Audit.Actor == actor
                && existingCompletion.Receipt.CaseId == scope.CaseId
                && existingCompletion.Receipt.PriorRevision == command.ExpectedRevision;
            return same
                ? new(ArrivalCompletionOutcome.Replayed, existingCompletion.Receipt, [])
                : new(ArrivalCompletionOutcome.CommandIdConflict, null, []);
        }

        if (!state.Audit.Any(item => item.Action == StartAction))
            return new(ArrivalCompletionOutcome.NotStarted, null, []);
        if (state.Audit.Any(item => item.Action == CompletionAction))
        {
            var completed = FindCompletion(state, null);
            return new(ArrivalCompletionOutcome.Replayed, completed is { } saved ? saved.Receipt : null, []);
        }
        if (command.ExpectedRevision != state.Revision)
            return new(ArrivalCompletionOutcome.StaleRevision, null, []);

        var missing = MissingSources(state);
        if (missing.Count > 0)
            return new(ArrivalCompletionOutcome.MissingSources, null, missing);
        var latestBasis = state.Basis[^1];
        var receipt = new ArrivalReceipt(1, command.CommandId!, scope.CaseId, "ready",
            state.Revision, state.Revision + 1, "audit-" + (state.Audit.Count + 1));
        var detail = JsonSerializer.Serialize(new ArrivalCompletionAuditDetail(receipt, latestBasis.Version, latestBasis.BasisDigest), Json);
        try
        {
            var commit = cases.Commit(scope, state.Revision, new CaseCommit(null,
                CompletionReceiptPrefix + command.CommandId, actor, CompletionAction, detail, time.GetUtcNow()));
            var saved = FindCompletion(commit.State, command.CommandId)?.Receipt
                ?? throw new InvalidDataException("The committed arrival completion receipt was not persisted.");
            return new(commit.Replayed ? ArrivalCompletionOutcome.Replayed : ArrivalCompletionOutcome.Completed, saved, []);
        }
        catch (ReceiptConflictException)
        {
            var latest = cases.Read(scope);
            var raced = FindCompletion(latest, command.CommandId);
            if (raced is null || raced.Value.Audit.Actor != actor
                || raced.Value.Receipt.PriorRevision != command.ExpectedRevision)
                return new(ArrivalCompletionOutcome.CommandIdConflict, null, []);
            return new(ArrivalCompletionOutcome.Replayed, raced.Value.Receipt, []);
        }
        catch (RevisionConflictException)
        {
            return new(ArrivalCompletionOutcome.StaleRevision, null, []);
        }
    }

    public ArrivalStartResult Start(CaseScope scope, string actor, ArrivalCommand command)
    {
        var state = cases.Read(scope);
        var previous = FindStart(state, command.CommandId!);
        if (previous is { } existing)
        {
            var same = existing.Audit.Actor == actor
                && existing.Receipt.CaseId == scope.CaseId
                && existing.Receipt.PriorRevision == command.ExpectedRevision;
            return same
                ? new(ArrivalStartOutcome.Replayed, existing.Receipt)
                : new(ArrivalStartOutcome.CommandIdConflict, null);
        }

        if (state.Audit.Any(item => item.Action == CompletionAction))
            return new(ArrivalStartOutcome.Completed, null);
        if (state.Audit.Any(item => item.Action == StartAction))
            return new(ArrivalStartOutcome.AlreadyStarted, null);
        if (command.ExpectedRevision != state.Revision)
            return new(ArrivalStartOutcome.StaleRevision, null);

        var receipt = new ArrivalReceipt(1, command.CommandId!, scope.CaseId, "collecting",
            state.Revision, state.Revision + 1, "audit-" + (state.Audit.Count + 1));
        try
        {
            var commit = cases.Commit(scope, state.Revision, new CaseCommit(null,
                StartReceiptPrefix + command.CommandId, actor, StartAction,
                JsonSerializer.Serialize(receipt, Json), time.GetUtcNow()));
            var recorded = FindStart(commit.State, command.CommandId!)?.Receipt
                ?? throw new InvalidOperationException("The committed arrival start receipt was not persisted.");
            return new(commit.Replayed ? ArrivalStartOutcome.Replayed : ArrivalStartOutcome.Started, recorded);
        }
        catch (ReceiptConflictException)
        {
            return new(ArrivalStartOutcome.CommandIdConflict, null);
        }
        catch (RevisionConflictException)
        {
            var latest = cases.Read(scope);
            var replay = FindStart(latest, command.CommandId!);
            if (replay is { } raced)
            {
                var same = raced.Audit.Actor == actor
                    && raced.Receipt.CaseId == scope.CaseId
                    && raced.Receipt.PriorRevision == command.ExpectedRevision;
                return same
                    ? new(ArrivalStartOutcome.Replayed, raced.Receipt)
                    : new(ArrivalStartOutcome.CommandIdConflict, null);
            }
            return new(ArrivalStartOutcome.StaleRevision, null);
        }
    }

    private static (AuditEvent Audit, ArrivalReceipt Receipt)? FindStart(CaseState state, string commandId)
    {
        foreach (var audit in state.Audit)
        {
            if (audit.Action != StartAction) continue;
            try
            {
                var receipt = JsonSerializer.Deserialize<ArrivalReceipt>(audit.Detail, Json);
                if (receipt?.CommandId == commandId) return (audit, receipt);
            }
            catch (JsonException)
            {
                // Older or unrelated audit detail is not an arrival command receipt.
            }
        }

        return null;
    }

    private static ArrivalRetrievalOutcome RetrievalOutcomeFor(string outcome) => outcome switch
    {
        "pinned" => ArrivalRetrievalOutcome.Pinned,
        "empty" => ArrivalRetrievalOutcome.Empty,
        "stale" => ArrivalRetrievalOutcome.Stale,
        "conflicting" => ArrivalRetrievalOutcome.Conflicting,
        _ => throw new InvalidDataException("The stored arrival retrieval outcome is invalid."),
    };

    private static bool SameRetrieval(
        ArrivalRetrievalReceipt receipt, AuditEvent audit, CaseScope scope, string actor, string role,
        string sourceId, long expectedRevision)
    {
        try
        {
            var detail = JsonSerializer.Deserialize<ArrivalSourceReadAuditDetail>(audit.Detail, Json);
            return audit.Actor == actor
                && detail?.Role == role
                && receipt.CaseId == scope.CaseId
                && receipt.SourceId == sourceId
                && receipt.PriorRevision == expectedRevision;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static (AuditEvent Audit, ArrivalReceipt Receipt)? FindCompletion(CaseState state, string? commandId)
    {
        foreach (var audit in state.Audit)
        {
            if (audit.Action != CompletionAction) continue;
            try
            {
                var detail = JsonSerializer.Deserialize<ArrivalCompletionAuditDetail>(audit.Detail, Json);
                if (detail is not null && (commandId is null || detail.Receipt.CommandId == commandId))
                    return (audit, detail.Receipt);
            }
            catch (JsonException)
            {
                // Older or unrelated audit detail is not an arrival completion receipt.
            }
        }

        return null;
    }
}
