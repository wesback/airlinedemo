using System.Reflection;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Tests;

public sealed class InvestigationTraceStoreTests : IDisposable
{
    private const string Owner = "owner-a";
    private const string CaseId = "case-a";
    private const string RunId = "run-1";
    private const string InvestigationId = "inv-1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trace-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string PathFor(string name = "cases.json") => Path.Combine(_dir, name);

    // Exact names only: "ContentFilterSetting" is a required categorical setting, not document content.
    private static readonly string[] ForbiddenContentProperties =
    [
        "Content", "DocumentContent", "Text", "Body",
        "Prompt", "PromptContent", "Response", "ResponseContent",
    ];

    private static InvestigationTrace BuildTrace(
        string caseId = CaseId,
        string runId = RunId,
        string investigationId = InvestigationId) =>
        new(
            caseId,
            runId,
            investigationId,
            BasisVersion: 3,
            ModelDeploymentName: "model-deploy-x",
            ApiVersion: "2025-01-01",
            Region: "westeurope",
            ContentFilterSetting: "strict-filter",
            Decision: "return-to-service",
            ActorToken: "tok-7f3a",
            Outcome: "escalated",
            Steps:
            [
                new InvestigationTraceStep(1, "search-manual", "doc-ata-32", 14, "verified", "None", "None"),
                new InvestigationTraceStep(2, "check-limit", "doc-mel-05", 2, "rewritten", "CitationRewrite", "ManualReview"),
            ]);

    private static void AssertSameTrace(InvestigationTrace expected, InvestigationTrace actual)
    {
        Assert.Equal(expected.CaseId, actual.CaseId);
        Assert.Equal(expected.RunId, actual.RunId);
        Assert.Equal(expected.InvestigationId, actual.InvestigationId);
        Assert.Equal(expected.BasisVersion, actual.BasisVersion);
        Assert.Equal(expected.ModelDeploymentName, actual.ModelDeploymentName);
        Assert.Equal(expected.ApiVersion, actual.ApiVersion);
        Assert.Equal(expected.Region, actual.Region);
        Assert.Equal(expected.ContentFilterSetting, actual.ContentFilterSetting);
        Assert.Equal(expected.Decision, actual.Decision);
        Assert.Equal(expected.ActorToken, actual.ActorToken);
        Assert.Equal(expected.Outcome, actual.Outcome);
        Assert.Equal(expected.Steps.Count, actual.Steps.Count);
        for (var i = 0; i < expected.Steps.Count; i++)
        {
            Assert.Equal(expected.Steps[i], actual.Steps[i]);
        }
    }

    [Fact]
    public void M08_PersistsCorrelatedTraceWithoutRawContent()
    {
        var store = new CaseStateStore(PathFor());
        var scope = store.CreateCase(CaseId, Owner);
        var trace = BuildTrace();

        store.AppendTrace(scope, trace);

        var reloaded = new CaseStateStore(PathFor());
        var reloadedScope = reloaded.AuthorizeCase(CaseId, Owner);
        var stored = reloaded.FindTrace(reloadedScope, CaseId, RunId, InvestigationId);
        Assert.NotNull(stored);
        AssertSameTrace(trace, stored!);
        Assert.Equal(1, stored!.Steps[0].Sequence);
        Assert.Equal(2, stored.Steps[1].Sequence);
        Assert.Equal("None", stored.Steps[0].CorrectionCode);
        Assert.Equal("CitationRewrite", stored.Steps[1].CorrectionCode);
        Assert.Equal("None", stored.Steps[0].PolicyHandover);
        Assert.Equal("ManualReview", stored.Steps[1].PolicyHandover);

        var missingCorrection = BuildTrace(runId: "run-2") with
        {
            Steps = [new InvestigationTraceStep(1, "search-manual", "doc-1", 1, "verified", null!, "None")],
        };
        Assert.Throws<ArgumentException>(() => store.AppendTrace(scope, missingCorrection));

        var missingHandover = BuildTrace(runId: "run-3") with
        {
            Steps = [new InvestigationTraceStep(1, "search-manual", "doc-1", 1, "verified", "None", null!)],
        };
        Assert.Throws<ArgumentException>(() => store.AppendTrace(scope, missingHandover));
        Assert.Null(reloaded.FindTrace(reloadedScope, CaseId, "run-2", InvestigationId));
        Assert.Null(reloaded.FindTrace(reloadedScope, CaseId, "run-3", InvestigationId));

        var forbiddenProperties = typeof(InvestigationTrace).GetProperties()
            .Concat(typeof(InvestigationTraceStep).GetProperties())
            .Select(p => p.Name)
            .Where(n => ForbiddenContentProperties.Contains(n))
            .ToList();
        Assert.Empty(forbiddenProperties);
    }

    [Fact]
    public void TraceOrderAndDuplicateAppendAreStable()
    {
        var store = new CaseStateStore(PathFor());
        var scope = store.CreateCase(CaseId, Owner);
        var original = BuildTrace();
        store.AppendTrace(scope, original);

        var reloaded = new CaseStateStore(PathFor());
        var reloadedScope = reloaded.AuthorizeCase(CaseId, Owner);
        AssertSameTrace(original, reloaded.FindTrace(reloadedScope, CaseId, RunId, InvestigationId)!);

        var duplicate = BuildTrace() with
        {
            Decision = "tampered-decision",
            Outcome = "tampered-outcome",
            Steps = [new InvestigationTraceStep(1, "other-tool", "doc-x", 9, "unverified", "None", "None")],
        };
        Assert.Throws<TraceConflictException>(() => reloaded.AppendTrace(reloadedScope, duplicate));

        var afterDuplicate = new CaseStateStore(PathFor());
        var afterScope = afterDuplicate.AuthorizeCase(CaseId, Owner);
        AssertSameTrace(original, afterDuplicate.FindTrace(afterScope, CaseId, RunId, InvestigationId)!);
    }

    [Fact]
    public void TraceLookupRequiresExactCaseRunAndInvestigation()
    {
        var store = new CaseStateStore(PathFor());
        var scope = store.CreateCase(CaseId, Owner);
        store.AppendTrace(scope, BuildTrace());

        Assert.NotNull(store.FindTrace(scope, CaseId, RunId, InvestigationId));
        Assert.Null(store.FindTrace(scope, "case-other", RunId, InvestigationId));
        Assert.Null(store.FindTrace(scope, CaseId, "run-other", InvestigationId));
        Assert.Null(store.FindTrace(scope, CaseId, RunId, "inv-other"));
    }

    [Fact]
    public void AbsentTraceIsNotReconstructed()
    {
        var store = new CaseStateStore(PathFor());
        var scope = store.CreateCase(CaseId, Owner);
        const string sentinel = "BASIS-SENTINEL-7d41c0";
        var basis = CaseStateStore.CreateBasisVersion(1, sentinel, string.Empty);
        store.Commit(scope, 0, new CaseCommit(basis, "receipt-basis", "presenter", "submit-package", "basis 1"));

        Assert.Null(store.FindTrace(scope, CaseId, RunId, InvestigationId));
        Assert.Empty(store.Read(scope).Traces ?? Array.Empty<InvestigationTrace>());
    }
}
