using System.Text.Json;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Runs;

namespace AirlineDemo.Api.Tests;

public sealed class CasePackBlockedVariantResetTests : IDisposable
{
    private const string Actor = "presenter";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "case-pack-reset-" + Guid.NewGuid().ToString("N"));
    public CasePackBlockedVariantResetTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    private string StatePath => Path.Combine(_directory, "state.json");

    [Fact]
    public void AC014_AcceptedResetCommitsRunReceiptAndAuditTogether()
    {
        var (store, caseA, caseB, caseC) = CreateCases(StatePath);
        store.Commit(caseA, 0, new CaseCommit(null, null, Actor, "seed", "Case A history"));
        store.Commit(caseB, 0, new CaseCommit(null, null, Actor, "seed", "Case B history"));
        store.Commit(caseC, 0, new CaseCommit(null, null, Actor, "seed", "prior Case C history",
            JourneyEvent: new CaseJourneyEventDraft(
                "prior-event", "prior-run", caseC.CaseId, "run.started", null, null,
                "presenter", "Prior run", "Case pack", [])));
        var beforeA = store.Read(caseA);
        var beforeB = store.Read(caseB);
        var beforeC = store.Read(caseC);
        var beforeCommit = DateTimeOffset.UtcNow;
        var result = new CasePackBlockedVariantReset(store).Reset(caseC, Actor, "reset-1", 1, "blocked");
        Assert.False(result.Replayed);
        Assert.Equal((1, "reset-1", "cp-case-c", 1L, 2L),
            (result.Receipt.ContractVersion, result.Receipt.CommandId, result.Receipt.CaseId,
                result.Receipt.PriorRevision, result.Receipt.NewRevision));
        var afterC = store.Read(caseC);
        Assert.Equal(2, afterC.Revision);
        Assert.Equal(beforeC.Audit[0], afterC.Audit[0]);
        var audit = Assert.IsType<AuditEvent>(afterC.Audit[1]);
        Assert.Equal((2L, 2L, Actor, "presenter_reset_blocked_variant",
                $"caseId=cp-case-c;priorRevision=1;newRevision=2;reasonCode=presenter_reset_blocked_variant;commandId=reset-1;auditEventId={result.Receipt.AuditEventId}"),
            (audit.Sequence, audit.Revision, audit.Actor, audit.Action, audit.Detail));
        Assert.InRange(audit.At, beforeCommit, DateTimeOffset.UtcNow);
        Assert.Equal((2, 2, "prior-event"),
            (afterC.JourneyEvents!.Count, afterC.JourneyEvents.Select(item => item.RunId).Distinct(StringComparer.Ordinal).Count(),
                afterC.JourneyEvents[0].EventId));
        Assert.Contains(afterC.JourneyEvents, item => item.RunId == result.Receipt.NewRunId);
        var storedReceipt = Assert.Single(afterC.BlockedVariantResetReceipts!);
        Assert.Equal(result.Receipt, storedReceipt.Receipt);
        Assert.Equal((Actor, 1L, "blocked"), (storedReceipt.Actor, storedReceipt.ExpectedRevision, storedReceipt.Variant));
        Assert.Throws<NotSupportedException>(() => ((IList<CasePackResetCommandReceipt>)afterC.BlockedVariantResetReceipts!).Clear());
        AssertUnchanged(beforeA, store.Read(caseA));
        AssertUnchanged(beforeB, store.Read(caseB));
    }

    [Fact]
    public void AC016_ExactReplayReturnsOriginalReceiptAndInvalidTargetsDoNotMutate()
    {
        var (store, caseA, caseB, caseC) = CreateCases(StatePath);
        var reset = new CasePackBlockedVariantReset(store);
        var accepted = reset.Reset(caseC, Actor, "replay-1", 0, "blocked");
        var beforeReplay = store.Read(caseC);
        var replay = reset.Reset(caseC, Actor, "replay-1", 0, "blocked");
        Assert.True(replay.Replayed);
        Assert.Equal(accepted.Receipt, replay.Receipt);
        Assert.Equal(Snapshot(beforeReplay), Snapshot(replay.State));
        var stale = Assert.Throws<CasePackBlockedVariantResetException>(() =>
            reset.Reset(caseC, Actor, "replay-2", 0, "blocked"));
        Assert.Equal((409, "stale_revision"), (stale.StatusCode, stale.Code));
        var invalidVariant = Assert.Throws<CasePackBlockedVariantResetException>(() =>
            reset.Reset(caseC, Actor, "invalid-variant", 1, "unblocked"));
        Assert.Equal((422, "unsupported_target"), (invalidVariant.StatusCode, invalidVariant.Code));
        var beforeA = store.Read(caseA);
        var beforeB = store.Read(caseB);
        foreach (var scope in new[] { caseA, caseB })
        {
            var invalid = Assert.Throws<CasePackBlockedVariantResetException>(() =>
                reset.Reset(scope, Actor, "invalid-target", 0, "blocked"));
            Assert.Equal((422, "unsupported_target"), (invalid.StatusCode, invalid.Code));
        }
        AssertUnchanged(beforeA, store.Read(caseA));
        AssertUnchanged(beforeB, store.Read(caseB));
        Assert.Equal(Snapshot(beforeReplay), Snapshot(store.Read(caseC)));
    }
    [Fact]
    public async Task AC023_ConflictsFailuresAndRevisionRaceAreAtomic()
    {
        var store = new CaseStateStore(StatePath);
        var scope = store.CreateCase("cp-case-c", Actor);
        var reset = new CasePackBlockedVariantReset(store);
        var accepted = reset.Reset(scope, Actor, "command-1", 0, "blocked");
        Assert.False(accepted.Replayed);
        var beforeConflict = store.Read(scope);
        var actorConflict = Assert.Throws<CasePackBlockedVariantResetException>(() =>
            reset.Reset(scope, "other-presenter", "command-1", 0, "blocked"));
        Assert.Equal((409, "command_id_conflict"), (actorConflict.StatusCode, actorConflict.Code));
        var payloadConflict = Assert.Throws<CasePackBlockedVariantResetException>(() =>
            reset.Reset(scope, Actor, "command-1", 1, "blocked"));
        Assert.Equal((409, "command_id_conflict"), (payloadConflict.StatusCode, payloadConflict.Code));
        var variantConflict = Assert.Throws<CasePackBlockedVariantResetException>(() =>
            reset.Reset(scope, Actor, "command-1", 0, "unblocked"));
        Assert.Equal((409, "command_id_conflict"), (variantConflict.StatusCode, variantConflict.Code));
        AssertUnchanged(beforeConflict, store.Read(scope));
        var raceStore = new CaseStateStore(Path.Combine(_directory, "race.json"));
        var raceScope = raceStore.CreateCase("cp-case-c", Actor);
        var raced = new CasePackBlockedVariantReset(new RevisionRaceStore(raceStore));
        var raceFailure = Assert.Throws<CasePackBlockedVariantResetException>(() =>
            raced.Reset(raceScope, Actor, "race-1", 0, "blocked"));
        Assert.Equal((409, "stale_revision"), (raceFailure.StatusCode, raceFailure.Code));
        var afterRace = raceStore.Read(raceScope);
        Assert.Equal((1L, 1, 0, 0),
            (afterRace.Revision, afterRace.Audit.Count, afterRace.BlockedVariantResetReceipts!.Count, afterRace.JourneyEvents!.Count));
        var failWrite = false;
        var failurePath = Path.Combine(_directory, "failure.json");
        var failureStore = new CaseStateStore(failurePath, () => { if (failWrite) throw new IOException("injected commit failure"); });
        var failureScope = failureStore.CreateCase("cp-case-c", Actor);
        var beforeFailure = failureStore.Read(failureScope);
        failWrite = true;
        Assert.Throws<IOException>(() =>
            new CasePackBlockedVariantReset(failureStore).Reset(failureScope, Actor, "failure-1", 0, "blocked"));
        failWrite = false;
        var restarted = new CaseStateStore(failurePath);
        var recovered = restarted.Read(restarted.AuthorizeCase("cp-case-c", Actor));
        AssertUnchanged(beforeFailure, recovered);
        var concurrentStore = new CaseStateStore(Path.Combine(_directory, "concurrent.json"));
        var concurrentScope = concurrentStore.CreateCase("cp-case-c", Actor);
        var concurrentReset = new CasePackBlockedVariantReset(concurrentStore);
        var results = await Task.WhenAll(
            Task.Run(() => concurrentReset.Reset(concurrentScope, Actor, "same-command", 0, "blocked")),
            Task.Run(() => concurrentReset.Reset(concurrentScope, Actor, "same-command", 0, "blocked")));
        Assert.Equal(results[0].Receipt, results[1].Receipt);
        var concurrentState = concurrentStore.Read(concurrentScope);
        Assert.Equal((1, 1, 1, 1), (concurrentState.Audit.Count, concurrentState.BlockedVariantResetReceipts!.Count,
            concurrentState.JourneyEvents!.Count, concurrentState.JourneyEvents.Select(item => item.RunId).Distinct(StringComparer.Ordinal).Count()));
    }

    private static void AssertUnchanged(CaseState expected, CaseState actual) =>
        Assert.Equal(Snapshot(expected), Snapshot(actual));
    private static (CaseStateStore Store, CaseScope A, CaseScope B, CaseScope C) CreateCases(string path)
    {
        var store = new CaseStateStore(path);
        return (store, store.CreateCase("cp-case-a", Actor), store.CreateCase("cp-case-b", Actor), store.CreateCase("cp-case-c", Actor));
    }

    private static string Snapshot(CaseState state) => JsonSerializer.Serialize(new { state.CaseId, state.Revision, state.Basis, state.ReceiptIds, state.Audit, state.JourneyEvents, state.BlockedVariantResetReceipts });
    private sealed class RevisionRaceStore(ICaseStateStore inner) : ICaseStateStore
    {
        public CaseScope AuthorizeCase(string caseId, string owner) => inner.AuthorizeCase(caseId, owner);
        public string? OwnerOf(string caseId) => inner.OwnerOf(caseId);
        public CaseScope CreateCase(string caseId, string owner) => inner.CreateCase(caseId, owner);
        public CaseState Read(CaseScope scope) => inner.Read(scope);
        public BasisVersion GetBasisVersion(CaseScope scope, int version) => inner.GetBasisVersion(scope, version);
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => inner.ReadAudit(scope);
        public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit) => inner.Commit(scope, expectedRevision, commit);
        public CasePackResetCommitResult CommitBlockedVariantReset(
            CaseScope scope, long expectedRevision, string actor, string commandId, string variant,
            DateTimeOffset? auditAt = null)
        {
            var observed = inner.Read(scope);
            inner.Commit(scope, observed.Revision, new CaseCommit(null, null, actor, "concurrent-write", "revision advanced"));
            return inner.CommitBlockedVariantReset(scope, expectedRevision, actor, commandId, variant, auditAt);
        }
        public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope) => inner.ListWorkItems(scope);
        public CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor) =>
            inner.CreateWorkItem(scope, expectedRevision, draft, actor);
        public CommitResult ChangeWorkItemState(CaseScope scope, long expectedRevision, string itemId, string state, string actor) =>
            inner.ChangeWorkItemState(scope, expectedRevision, itemId, state, actor);
        public void AppendTrace(CaseScope scope, InvestigationTrace trace) => inner.AppendTrace(scope, trace);
        public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId) =>
            inner.FindTrace(scope, caseId, runId, investigationId);
    }
}
