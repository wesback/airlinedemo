using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Tests;

public sealed class InvestigationTraceStoreTests : IDisposable
{
    private const string Owner = "owner-a";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trace-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string PathFor(string name = "cases.json") => Path.Combine(_dir, name);

    internal static InvestigationTrace SampleTrace(string decision = "approve", string runId = "run-1", string investigationId = "inv-1") =>
        new(
            "case-a", runId, investigationId, 3, "gpt-deploy-1", "2024-10-21", "eastus2", "default-filter",
            decision, "actor-7f3a", "completed",
            [
                new InvestigationTraceStep(1, "search_basis", "doc-101", 4, "verified", "None", "None"),
                new InvestigationTraceStep(2, "cite_page", "doc-202", 12, "corrected", "CITE-FIX", "POLICY-HANDOVER"),
            ]);

    // M-08: correlated trace with provenance persists and reloads; no raw content property exists.
    [Fact]
    public void M08_PersistsCorrelatedTraceWithoutRawContent()
    {
        var path = PathFor();
        var store = new CaseStateStore(path);
        var scope = store.CreateCase("case-a", Owner);
        var trace = SampleTrace();
        store.AppendInvestigationTrace(scope, trace);

        var reloaded = new CaseStateStore(path);
        var loaded = reloaded.ReadInvestigationTrace(reloaded.AuthorizeCase("case-a", Owner), "run-1", "inv-1");
        Assert.NotNull(loaded);
        Assert.Equal("case-a", loaded.CaseId);
        Assert.Equal("run-1", loaded.RunId);
        Assert.Equal("inv-1", loaded.InvestigationId);
        Assert.Equal(3, loaded.BasisVersion);
        Assert.Equal("gpt-deploy-1", loaded.ModelDeploymentName);
        Assert.Equal("2024-10-21", loaded.ApiVersion);
        Assert.Equal("eastus2", loaded.Region);
        Assert.Equal("default-filter", loaded.ContentFilterSetting);
        Assert.Equal("approve", loaded.Decision);
        Assert.Equal("actor-7f3a", loaded.ActorToken);
        Assert.Equal("completed", loaded.Outcome);

        Assert.Equal(2, loaded.Steps.Count);
        Assert.Equal(new[] { 1, 2 }, loaded.Steps.Select(s => s.Sequence).ToArray());
        Assert.Equal("search_basis", loaded.Steps[0].ToolName);
        Assert.Equal("doc-101", loaded.Steps[0].DocumentId);
        Assert.Equal(4, loaded.Steps[0].PageNumber);
        Assert.Equal("verified", loaded.Steps[0].CitationCheckOutcome);
        Assert.Equal("None", loaded.Steps[0].CorrectionCode);
        Assert.Equal("None", loaded.Steps[0].PolicyHandover);
        Assert.Equal("CITE-FIX", loaded.Steps[1].CorrectionCode);
        Assert.Equal("POLICY-HANDOVER", loaded.Steps[1].PolicyHandover);

        var missingCorrection = SampleTrace(runId: "run-2", investigationId: "inv-2") with
        {
            Steps = [new InvestigationTraceStep(1, "search_basis", "doc-101", 4, "verified", null!, "None")],
        };
        Assert.Throws<ArgumentException>(() => store.AppendInvestigationTrace(scope, missingCorrection));
        var missingHandover = SampleTrace(runId: "run-3", investigationId: "inv-3") with
        {
            Steps = [new InvestigationTraceStep(1, "search_basis", "doc-101", 4, "verified", "None", " ")],
        };
        Assert.Throws<ArgumentException>(() => store.AppendInvestigationTrace(scope, missingHandover));

        var forbidden = new[] { "Prompt", "Response" };
        var properties = typeof(InvestigationTrace).GetProperties()
            .Concat(typeof(InvestigationTraceStep).GetProperties())
            .Select(p => p.Name)
            .ToArray();
        Assert.DoesNotContain(properties, n => forbidden.Any(f => n.Contains(f, StringComparison.Ordinal)));
        Assert.DoesNotContain(properties, n => n.Contains("Content", StringComparison.Ordinal) && n != "ContentFilterSetting");
    }

    // M-03: append and read only; ordered steps persist, and a duplicate run and investigation leaves the original intact.
    [Fact]
    public void TraceOrderAndDuplicateAppendAreStable()
    {
        var path = PathFor();
        var store = new CaseStateStore(path);
        var scope = store.CreateCase("case-a", Owner);
        store.AppendInvestigationTrace(scope, SampleTrace());

        var reloaded = new CaseStateStore(path);
        var reloadedScope = reloaded.AuthorizeCase("case-a", Owner);
        var before = reloaded.ReadInvestigationTrace(reloadedScope, "run-1", "inv-1");
        Assert.NotNull(before);
        Assert.Equal(new[] { 1, 2 }, before.Steps.Select(s => s.Sequence).ToArray());

        var duplicate = SampleTrace(decision: "reject");
        Assert.Throws<InvalidOperationException>(() => reloaded.AppendInvestigationTrace(reloadedScope, duplicate));

        var after = new CaseStateStore(path).ReadInvestigationTrace(reloaded.AuthorizeCase("case-a", Owner), "run-1", "inv-1");
        Assert.NotNull(after);
        Assert.Equal("approve", after.Decision);
        Assert.Equal(before.Steps, after.Steps);
    }

    // TRACE-STORE-03: all three identifiers must match exactly; any mismatch returns null.
    [Fact]
    public void TraceLookupRequiresExactCaseRunAndInvestigation()
    {
        var path = PathFor();
        var store = new CaseStateStore(path);
        var scope = store.CreateCase("case-a", Owner);
        var otherScope = store.CreateCase("case-b", Owner);
        store.AppendInvestigationTrace(scope, SampleTrace());

        Assert.NotNull(store.ReadInvestigationTrace(scope, "run-1", "inv-1"));
        Assert.Null(store.ReadInvestigationTrace(scope, "run-2", "inv-1"));
        Assert.Null(store.ReadInvestigationTrace(scope, "run-1", "inv-2"));
        Assert.Null(store.ReadInvestigationTrace(otherScope, "run-1", "inv-1"));
        Assert.Null(store.ReadInvestigationTrace(scope, "RUN-1", "inv-1"));
    }

    // TRACE-STORE-04: a basis document never produces a trace.
    [Fact]
    public void AbsentTraceIsNotReconstructed()
    {
        var store = new CaseStateStore(PathFor());
        var scope = store.CreateCase("case-a", Owner);
        const string sentinel = "BASIS-SENTINEL-6d1e9";
        var basis = CaseStateStore.CreateBasisVersion(1, sentinel, string.Empty);
        store.Commit(scope, 0, new CaseCommit(basis, null, "presenter", "submit-package", "basis 1"));

        Assert.Equal(sentinel, store.GetBasisVersion(scope, 1).Content);
        Assert.Null(store.ReadInvestigationTrace(scope, "run-1", "inv-1"));
    }
}
