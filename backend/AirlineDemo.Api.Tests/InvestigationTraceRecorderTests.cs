using System.Reflection;
using System.Text.Json;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using Microsoft.Extensions.AI;

namespace AirlineDemo.Api.Tests;

public sealed class InvestigationTraceRecorderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"trace-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private static InvestigationTraceContext Context(string runId = "run-1") =>
        new(runId, "inv-1", 3, "deployment-a", "2024-10-01", "eu-west", "on", "escalate", "actor-token");

    private static IReadOnlyDictionary<string, BasisDocument> Basis() =>
        new Dictionary<string, BasisDocument> { ["doc-1"] = BasisDocument.Create("doc-1", ["Reserve approval granted on page one"]) };

    private const string Region = "swedencentral";

    private const string ApprovedJson = """{"manifestVersion":"2026.10.1","approved":true,"arm":"investigation-live","deploymentName":"fixture-deployment","model":"fixture-model","apiVersion":"2025-01-01","region":"swedencentral","contentFilter":"enabled"}""";

    private const string ClaimsJson =
        """[{"text":"Reserve approval","witnesses":[{"documentId":"doc-1","page":1,"quote":"Reserve approval granted on page one"}]}]""";

    private const string BadQuoteClaimsJson =
        """[{"text":"Reserve approval","witnesses":[{"documentId":"doc-1","page":1,"quote":"Reserve approval missing"}]}]""";

    private static readonly string[] Documents = ["Reserve approval granted on page one"];

    private static ApprovedModelManifest Approved() => ApprovedModelManifest.Parse(ApprovedJson);

    private static BoundedInvestigator Investigator(IChatClient model) =>
        new("case-1", new FixtureSource(), Approved(), SyntheticContentRegistry.FromCommittedFixtures(), Region, "evidence-v1", model);

    private static ChatResponse Call(string callId, string name, IDictionary<string, object?>? args = null) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, name, args)]));

    private static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private static IDictionary<string, object?> Page(int page) => new Dictionary<string, object?> { ["documentId"] = "doc-1", ["page"] = page };

    private sealed class FixtureSource : ICaseReadSource
    {
        public IReadOnlyList<string> GetInventory(string caseId) => ["doc-1"];

        public string? GetApprovedRequirements(string caseId) => null;

        public string? GetSourcePage(string caseId, string documentId, int page) =>
            page == 1 ? "Reserve approval granted on page one" : null;

        public IReadOnlyList<string> SearchWithinBasis(string caseId, string query) => [];

        public string? GetProcessingStatus(string caseId) => null;
    }

    /// <summary>Model stub that always returns the same reply.</summary>
    private sealed class FixedReplyClient(ChatResponse reply) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("fixed");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(reply);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public void PersistsActualOrderedToolAndCitationOutcomes()
    {
        var store = new CaseStateStore(_path);
        var scope = store.CreateCase("case-1", "owner-1");
        var recorder = new InvestigationTraceRecorder(store, scope, Context());

        recorder.RecordToolCall(CaseReadTools.InventoryTool);
        recorder.RecordToolCall(CaseReadTools.SourcePageTool);
        recorder.RecordToolCall(CaseReadTools.SourcePageTool);
        recorder.RecordCitationCheck("doc-1", 1, InvestigationTraceRecorder.CitationVerified);
        recorder.RecordCitationCheck("doc-1", 2, InvestigationTraceRecorder.CitationQuoteNotOnPage);
        recorder.Complete("approved");

        var trace = store.FindTrace(scope, "case-1", "run-1", "inv-1");

        Assert.NotNull(trace);
        Assert.Equal("approved", trace.Outcome);
        Assert.Equal(6, trace.Steps.Count);
        Assert.Equal(
            new[] { 1, 2, 3, 4, 5, 6 },
            trace.Steps.Select(s => s.Sequence).ToArray());
        Assert.Equal(
            new[] { CaseReadTools.InventoryTool, CaseReadTools.SourcePageTool, CaseReadTools.SourcePageTool, "citation-check", "citation-check", "policy-handover" },
            trace.Steps.Select(s => s.ToolName).ToArray());
        Assert.Equal(
            new[] { "requested", "requested", "requested", "verified", "quote-not-on-page", "None" },
            trace.Steps.Select(s => s.CitationCheckOutcome).ToArray());
        Assert.Equal(new[] { "None", "None", "None", "doc-1", "doc-1", "None" }, trace.Steps.Select(s => s.DocumentId).ToArray());
        Assert.Equal(new[] { 0, 0, 0, 1, 2, 0 }, trace.Steps.Select(s => s.PageNumber).ToArray());
        Assert.Equal("deterministic-policy", trace.Steps[^1].PolicyHandover);
    }

    [Fact]
    public void RecordsDowngradeWithoutUntrustedContent()
    {
        var store = new CaseStateStore(_path);
        var scope = store.CreateCase("case-1", "owner-1");
        var recorder = new InvestigationTraceRecorder(store, scope, Context());

        var downgraded = ClaimValidator.Validate(
            new InvestigationClaim("secret reserve approval passcode-123", [new DocumentWitness("doc-1", 1, "Reserve approval granted on page one")]),
            ClaimKind.MissingRequirement,
            Basis(),
            "");
        var supported = ClaimValidator.Validate(
            new InvestigationClaim("Reserve approval", [new DocumentWitness("doc-1", 1, "Reserve approval granted on page one")]),
            ClaimKind.MissingRequirement,
            Basis(),
            "Reserve approval");

        Assert.Equal(ClaimStatus.Downgraded, downgraded.Status);
        Assert.Equal(ClaimStatus.Supported, supported.Status);

        recorder.RecordValidation(downgraded);
        recorder.RecordValidation(supported);
        recorder.Complete("approved");

        var trace = store.FindTrace(scope, "case-1", "run-1", "inv-1");

        Assert.NotNull(trace);
        var correction = Assert.Single(trace.Steps, s => s.CorrectionCode != "None");
        Assert.Equal("Downgraded", correction.CorrectionCode);
        Assert.Equal("claim-validation", correction.ToolName);

        var json = JsonSerializer.Serialize(trace);
        Assert.DoesNotContain("passcode-123", json);
        Assert.DoesNotContain("Reserve approval granted on page one", json);

        var forbidden = new[] { "Arguments", "DocumentText", "Prompt", "Response", "Quote", "Passcode", "Cookie" };
        var names = typeof(InvestigationTrace).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)
            .Concat(typeof(InvestigationTraceStep).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name))
            .ToArray();
        Assert.Empty(names.Intersect(forbidden));
    }

    [Fact]
    public async Task StoreFailureDoesNotReturnSuccessfulInvestigation()
    {
        var real = new CaseStateStore(_path);
        var scope = real.CreateCase("case-1", "owner-1");
        var investigator = Investigator(new FixedReplyClient(Text(ClaimsJson)));
        var service = new InvestigationExecutionService(new ThrowingTraceStore());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteAsync(scope, Context(), investigator, Documents, ClaimKind.MissingRequirement, Basis(), "Reserve approval"));
        Assert.Equal("append failed", error.Message);
    }

    [Fact]
    public void RecordsOnlyStartedInvestigationsAndTheirActualOutcome()
    {
        var store = new CaseStateStore(_path);
        var scope = store.CreateCase("case-1", "owner-1");

        // Started, no tool or citation activity, completed: the outcome and handover are still recorded.
        var completed = new InvestigationTraceRecorder(store, scope, Context("run-complete"));
        completed.Complete("approved");
        var stored = store.FindTrace(scope, "case-1", "run-complete", "inv-1");
        Assert.NotNull(stored);
        Assert.Equal("approved", stored.Outcome);
        var handover = Assert.Single(stored.Steps);
        Assert.Equal("deterministic-policy", handover.PolicyHandover);

        // Rejected after start: the rejected outcome is recorded and there is no handover step.
        var rejected = new InvestigationTraceRecorder(store, scope, Context("run-rejected"));
        rejected.Reject("rejected");
        var rejectedTrace = store.FindTrace(scope, "case-1", "run-rejected", "inv-1");
        Assert.NotNull(rejectedTrace);
        Assert.Equal("rejected", rejectedTrace.Outcome);
        Assert.Empty(rejectedTrace.Steps);

        // Rejected before execution starts: no recorder is created, so no trace is written.
        Assert.Null(store.FindTrace(scope, "case-1", "run-never-started", "inv-1"));
    }

    private sealed class ThrowingTraceStore : ICaseStateStore
    {
        public void AppendTrace(CaseScope scope, InvestigationTrace trace) => throw new InvalidOperationException("append failed");

        public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId) => null;

        public CaseScope AuthorizeCase(string caseId, string owner) => throw new NotSupportedException();
        public string? OwnerOf(string caseId) => throw new NotSupportedException();
        public CaseScope CreateCase(string caseId, string owner) => throw new NotSupportedException();
        public CaseState Read(CaseScope scope) => throw new NotSupportedException();
        public BasisVersion GetBasisVersion(CaseScope scope, int version) => throw new NotSupportedException();
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => throw new NotSupportedException();
        public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit) => throw new NotSupportedException();
        public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope) => throw new NotSupportedException();
        public CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor) => throw new NotSupportedException();
        public CommitResult ChangeWorkItemState(CaseScope scope, long expectedRevision, string itemId, string state, string actor) => throw new NotSupportedException();
    }
}
