using AirlineDemo.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AirlineDemo.Api.Tests;

public sealed class SqlCaseHistoryStoreTests
{
    private const string Owner = "history-owner";

    [Fact]
    public void AC027_AC028_HistoryPersistsExactRunBasisAndWitnessTuples()
    {
        WithDatabase(options =>
        {
            var store = new SqlCaseStateStore(options);
            var scope = store.CreateCase("case-history", Owner);
            var drafts = CaseHistoryStoreTestSupport.Populate(store, scope);
            var restored = new SqlCaseStateStore(options);
            var restoredScope = restored.AuthorizeCase(scope.CaseId, Owner);
            CaseHistoryStoreTestSupport.AssertHistory(drafts, restored.Read(restoredScope).JourneyEvents!);

            var before = restored.Read(restoredScope);
            var other = restored.CreateCase("other-case", "other-owner");
            Assert.Throws<ArgumentException>(() => restored.Commit(restoredScope, before.Revision, new CaseCommit(
                null, null, Owner, "journey", "wrong scope",
                JourneyEvent: CaseHistoryStoreTestSupport.Event(other.CaseId, "run", "wrong-event", "basis-two", 2, 2, 1))));
            CaseHistoryStoreTestSupport.AssertStateEqual(before, restored.Read(restoredScope));
        });
    }

    [Fact]
    public void ConcurrentRevisionChangeRejectsHistoryAppendWithoutPartialState()
    {
        WithDatabase(options => CaseHistoryStoreTestSupport.AssertStaleCommitRejected(
            () => new SqlCaseStateStore(options), "case-race"));
    }

    private static void WithDatabase(Action<DbContextOptions<CaseStateDbContext>> action)
    {
        var path = Path.Combine(Path.GetTempPath(), "case-history-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlite($"Data Source={path}").Options;
            using (var db = new CaseStateDbContext(options)) db.Database.Migrate();
            action(options);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
