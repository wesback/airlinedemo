using System.Text.Json;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Tests;

public sealed class CaseHistoryStoreTests : IDisposable
{
    private const string Owner = "history-owner";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "case-history-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "case-state.json");

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }

    [Fact]
    public void HistoryPersistsExactRunBasisAndWitnessTuples()
    {
        var store = new CaseStateStore(FilePath);
        var scope = store.CreateCase("case-history", Owner);
        var drafts = CaseHistoryStoreTestSupport.Populate(store, scope);
        var restored = new CaseStateStore(FilePath);
        var restoredScope = restored.AuthorizeCase(scope.CaseId, Owner);
        CaseHistoryStoreTestSupport.AssertHistory(drafts, restored.Read(restoredScope).JourneyEvents!);

        var before = restored.Read(restoredScope);
        var other = restored.CreateCase("other-case", "other-owner");
        Assert.Throws<ArgumentException>(() => restored.Commit(restoredScope, before.Revision, new CaseCommit(
            null, null, Owner, "journey", "wrong scope",
            JourneyEvent: CaseHistoryStoreTestSupport.Event(other.CaseId, "run", "wrong-event", "basis-two", 2, 2, 1))));
        CaseHistoryStoreTestSupport.AssertStateEqual(before, restored.Read(restoredScope));
    }

    [Fact]
    public void ConcurrentRevisionChangeRejectsHistoryAppendWithoutPartialState() =>
        CaseHistoryStoreTestSupport.AssertStaleCommitRejected(() => new CaseStateStore(FilePath), "case-race");
}

internal static class CaseHistoryStoreTestSupport
{
    private const string Owner = "history-owner";

    public static List<CaseJourneyEventDraft> Populate(ICaseStateStore store, CaseScope scope)
    {
        var basis1 = CaseStateStore.CreateBasisVersion(1, "basis one", string.Empty);
        store.Commit(scope, 0, new CaseCommit(basis1, null, Owner, "basis", "basis one"));
        var drafts = new List<CaseJourneyEventDraft>();
        for (var index = 0; index < 121; index++)
        {
            var prior = index < 100;
            var isBasis2 = index == 120;
            var basisId = isBasis2 ? "basis-two" : "basis-one";
            var version = isBasis2 ? 2 : 1;
            var eventId = (prior ? "prior-event-" + (index + 1) : "current-event-" + (index - 99));
            var draft = Event(scope.CaseId, prior ? "prior-run-" + (index + 1) : "current-run",
                eventId, basisId, version, version, prior ? index + 1 : index - 99);
            store.Commit(scope, index + 1, new CaseCommit(
                isBasis2 ? CaseStateStore.CreateBasisVersion(2, "basis two", basis1.BasisDigest) : null,
                null, Owner, "journey", "journey event", JourneyEvent: draft));
            drafts.Add(draft);
        }
        return drafts;
    }

    public static CaseJourneyEventDraft Event(
        string caseId, string runId, string eventId, string basisId, int basisVersion, int documentVersion, int page) =>
        new(eventId, runId, caseId, "evidence-reviewed", basisId, basisVersion, "presenter",
            "Reviewed document evidence", "Records",
            [new CaseJourneyWitness("witness-" + eventId, "document-" + eventId, documentVersion, page, basisId, basisVersion)]);

    public static void AssertHistory(IReadOnlyList<CaseJourneyEventDraft> expected, IReadOnlyList<CaseJourneyEvent> actual)
    {
        Assert.Equal(121, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var draft = expected[i];
            var item = actual[i];
            var expectedEvent = new CaseJourneyEvent(draft.EventId, draft.RunId, i + 1, draft.CaseId,
                draft.EventType, draft.BasisId, draft.BasisVersion, draft.ActorRole, draft.Summary,
                draft.CapabilityLabel, draft.Witnesses);
            Assert.Equal(JsonSerializer.Serialize(expectedEvent), JsonSerializer.Serialize(item));
        }
    }

    public static void AssertStaleCommitRejected(Func<ICaseStateStore> createStore, string caseId)
    {
        var first = createStore();
        var scope = first.CreateCase(caseId, Owner);
        var basis1 = CaseStateStore.CreateBasisVersion(1, "basis one", string.Empty);
        first.Commit(scope, 0, new CaseCommit(basis1, "receipt-initial", Owner, "basis", "basis one"));
        var observed = first.Read(scope).Revision;
        var competitor = createStore();
        var competingScope = competitor.AuthorizeCase(caseId, Owner);
        var basis2 = CaseStateStore.CreateBasisVersion(2, "basis two", basis1.BasisDigest);
        competitor.Commit(competingScope, observed, new CaseCommit(basis2, "receipt-competing", Owner, "basis", "competing basis"));
        var afterCompetitor = competitor.Read(competingScope);
        Assert.Throws<RevisionConflictException>(() => first.Commit(scope, observed, new CaseCommit(
            null, "receipt-stale", Owner, "journey", "must not append",
            JourneyEvent: Event(caseId, "run-race", "stale-event", "basis-two", 2, 2, 1))));
        var restarted = createStore();
        AssertStateEqual(afterCompetitor, restarted.Read(restarted.AuthorizeCase(caseId, Owner)));
    }

    public static void AssertStateEqual(CaseState expected, CaseState actual)
        => Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
}
