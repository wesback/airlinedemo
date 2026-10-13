using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Runs;

namespace AirlineDemo.Api.Tests;

public sealed class DemoRunResetTests : IDisposable
{
    private const string Owner = "presenter";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "demo-run-reset-" + Guid.NewGuid().ToString("N"));

    public DemoRunResetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string PathFor() => Path.Combine(_dir, "runs.json");

    private static void Seed(CaseStateStore store, string runId, string owner, string receipt)
    {
        var scope = store.CreateCase(runId, owner);
        store.Commit(scope, 0, new CaseCommit(null, receipt, owner, "open", "opened " + runId));
    }

    [Fact]
    public void Reset_leaves_other_run_and_selected_run_history_unchanged()
    {
        var store = new CaseStateStore(PathFor());
        Seed(store, "run-a", Owner, "receipt-a");
        Seed(store, "run-b", Owner, "receipt-b");
        var runBBefore = store.Read(store.AuthorizeCase("run-b", Owner));
        var runABefore = store.Read(store.AuthorizeCase("run-a", Owner));

        var result = new DemoRunReset(store).Reset("run-a", Owner, "run-c");

        Assert.Equal("run-c", result.NewRunId);
        var runBAfter = store.Read(store.AuthorizeCase("run-b", Owner));
        Assert.Equal(runBBefore.Revision, runBAfter.Revision);
        Assert.Equal(runBBefore.ReceiptIds, runBAfter.ReceiptIds);
        Assert.Equal(runBBefore.Audit.Count, runBAfter.Audit.Count);

        var runAAfter = store.Read(store.AuthorizeCase("run-a", Owner));
        Assert.Equal(runABefore.Revision, runAAfter.Revision);
        Assert.Equal(runABefore.ReceiptIds, runAAfter.ReceiptIds);
        Assert.Equal(runABefore.Audit.Count, runAAfter.Audit.Count);
    }

    [Fact]
    public void Reset_starts_new_run_without_inherited_receipts_or_audit()
    {
        var store = new CaseStateStore(PathFor());
        Seed(store, "run-a", Owner, "receipt-a");

        var result = new DemoRunReset(store).Reset("run-a", Owner, "run-c");

        Assert.Empty(result.NewRun.ReceiptIds);
        Assert.Empty(result.NewRun.Audit);
        Assert.Equal(0, result.NewRun.Revision);
    }

    [Fact]
    public void Reset_by_non_owner_is_denied_without_mutating_store()
    {
        var store = new CaseStateStore(PathFor());
        Seed(store, "run-a", Owner, "receipt-a");
        var before = File.ReadAllBytes(PathFor());

        Assert.Throws<CaseAccessDeniedException>(() => new DemoRunReset(store).Reset("run-a", "reviewer", "run-c"));

        Assert.Equal(before, File.ReadAllBytes(PathFor()));
        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("run-c", Owner));
    }

    [Fact]
    public void Reset_of_unknown_run_is_denied_without_mutating_store()
    {
        var store = new CaseStateStore(PathFor());
        Seed(store, "run-a", Owner, "receipt-a");
        var before = File.ReadAllBytes(PathFor());

        Assert.Throws<CaseAccessDeniedException>(() => new DemoRunReset(store).Reset("missing", Owner, "run-c"));

        Assert.Equal(before, File.ReadAllBytes(PathFor()));
    }

    [Fact]
    public void Reset_onto_existing_run_id_is_rejected_without_mutating_store()
    {
        var store = new CaseStateStore(PathFor());
        Seed(store, "run-a", Owner, "receipt-a");
        Seed(store, "run-b", Owner, "receipt-b");
        var before = File.ReadAllBytes(PathFor());

        Assert.Throws<InvalidOperationException>(() => new DemoRunReset(store).Reset("run-a", Owner, "run-b"));
        Assert.Throws<DemoRunResetException>(() => new DemoRunReset(store).Reset("run-a", Owner, "run-a"));

        Assert.Equal(before, File.ReadAllBytes(PathFor()));
    }

    [Fact]
    public void Reset_does_not_ingest_evaluator_answers_into_any_run()
    {
        var store = new CaseStateStore(PathFor());
        Seed(store, "run-a", Owner, "receipt-a");

        var result = new DemoRunReset(store).Reset("run-a", Owner, "run-c");

        Assert.DoesNotContain(result.NewRun.Audit, e => e.Actor.Contains("evaluator", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(result.NewRun.ReceiptIds);
        Assert.Single(store.Read(store.AuthorizeCase("run-a", Owner)).ReceiptIds);
    }

    [Fact]
    public void Fallback_identifies_fixture_and_application_version()
    {
        var fallback = DemoRunReset.ResolveFallback("aircraft-return-fixture-v1", "1.4.2");

        Assert.Equal("aircraft-return-fixture-v1", fallback.FixtureId);
        Assert.Equal("1.4.2", fallback.ApplicationVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Fallback_is_blocked_when_application_version_is_missing(string? version)
    {
        Assert.Throws<DemoRunResetException>(() => DemoRunReset.ResolveFallback("aircraft-return-fixture-v1", version));
    }

    [Fact]
    public void Fallback_is_blocked_when_fixture_id_is_missing()
    {
        Assert.Throws<DemoRunResetException>(() => DemoRunReset.ResolveFallback(null, "1.4.2"));
    }
}
