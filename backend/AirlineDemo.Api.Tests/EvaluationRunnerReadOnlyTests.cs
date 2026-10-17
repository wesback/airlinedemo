using System.Runtime.CompilerServices;
using System.Text.Json;
using AirlineDemo.Api.Evaluation;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Requests;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class EvaluationRunnerReadOnlyTests
{
    private static string Fixture(string file, [CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", "Evaluation", "v2", file);

    private sealed class Inputs : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "evaluation-read-only-" + Guid.NewGuid().ToString("N"));
        public EvaluationRunOptions Options => new(
            Path.Combine(Dir, "manifest.json"),
            Path.Combine(Dir, "cases.jsonl"),
            Path.Combine(Dir, "b.json"),
            Path.Combine(Dir, "c.json"));

        public Inputs()
        {
            Directory.CreateDirectory(Dir);
            File.Copy(Fixture("manifest.json"), Options.SuiteManifestPath);
            File.Copy(Fixture("cases.jsonl"), Options.CasesPath);
            File.WriteAllText(Options.ArmBManifestPath, JsonSerializer.Serialize(FakeApprovedManifest.Approved()));
            File.WriteAllText(Options.ArmCManifestPath,
                JsonSerializer.Serialize(FakeApprovedManifest.Approved() with { Model = "fixture-model-c" }));
        }

        public void Dispose() => Directory.Delete(Dir, recursive: true);
    }

    private static readonly string[] ReadTools =
    [
        "evaluation_get_case_overview",
        "evaluation_get_document_page",
        "evaluation_list_case_documents"
    ];

    private static readonly string[] ForbiddenOperations =
    [
        "create_case",
        "update_case",
        "submit_mock_request",
        "accept_mock_response",
        "accept_evidence",
        "record_reviewer_decision"
    ];

    [Fact]
    public async Task ForbiddenRunnerOperationsAreDeniedWithoutStateChanges()
    {
        using var files = new Inputs();
        var state = CreateStateStore(files.Dir);
        var inbox = new MockRequestInbox();
        var chat = new ForbiddenOperationChatClient(ForbiddenOperations);
        var services = CreateServices(state.Store, inbox, chat, () => { });
        using var provider = services.BuildServiceProvider();
        var caseStateBefore = SnapshotCase(state.Store, state.Scope);
        var inboxBefore = SnapshotInbox(inbox);
        var output = new StringWriter();

        var result = await Program.RunEvaluationCommandAsync(
            Args(files.Options),
            provider.GetRequiredService<FourArmEvaluationRunner>(),
            output,
            new StringWriter(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
        Assert.Equal(120, chat.Calls);
        Assert.Equal(ForbiddenOperations.Order(StringComparer.Ordinal),
            chat.RequestedOperations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.Equal(120, chat.DeclaredToolSets.Count);
        Assert.Equal(40, chat.DeclaredToolSets.Count(tools => tools.Length == 0));
        Assert.All(chat.DeclaredToolSets.Where(tools => tools.Length > 0), tools => Assert.Equal(ReadTools, tools));
        Assert.All(chat.DeclaredToolSets.SelectMany(tools => tools),
            tool => Assert.DoesNotContain(tool, ForbiddenOperations));
        using var evaluated = JsonDocument.Parse(output.ToString());
        Assert.Equal(40, evaluated.RootElement.GetProperty("Cases").GetArrayLength());
        foreach (var evaluationCase in evaluated.RootElement.GetProperty("Cases").EnumerateArray())
        {
            var arms = evaluationCase.GetProperty("Arms");
            Assert.Equal(4, arms.GetArrayLength());
            for (var index = 1; index < arms.GetArrayLength(); index++)
            {
                Assert.Equal(typeof(InvalidOperationException).FullName,
                    arms[index].GetProperty("Failure").GetProperty("ErrorType").GetString());
            }
        }

        Assert.Equal(caseStateBefore, SnapshotCase(state.Store, state.Scope));
        Assert.Equal(inboxBefore, SnapshotInbox(inbox));
        Assert.Equal(0, state.Store.CountFor(nameof(ICaseStateStore.CreateCase)));
        Assert.Equal(0, state.Store.CountFor(nameof(ICaseStateStore.Commit)));
        Assert.Equal(0, state.Store.CountFor(nameof(ICaseStateStore.CreateWorkItem)));
        Assert.Equal(0, state.Store.CountFor(nameof(ICaseStateStore.ChangeWorkItemState)));
        Assert.Equal(0, state.Store.CountFor(nameof(ICaseStateStore.AppendTrace)));
    }

    [Fact]
    public async Task MissingApprovalBlocksReadsAndModelCalls()
    {
        using var files = new Inputs();
        var scenarios = new[]
        {
            files.Options with { SuiteManifestPath = Path.Combine(files.Dir, "missing-suite.json") },
            files.Options with { ArmBManifestPath = Path.Combine(files.Dir, "missing-arm-b.json") },
            files.Options with { ArmCManifestPath = Path.Combine(files.Dir, "missing-arm-c.json") }
        };

        foreach (var runOptions in scenarios)
        {
            var state = CreateStateStore(files.Dir, Guid.NewGuid().ToString("N"));
            var chat = new ForbiddenOperationChatClient(ForbiddenOperations);
            var inbox = new MockRequestInbox();
            var factoryCalls = 0;
            using var provider = CreateServices(state.Store, inbox, chat, () => factoryCalls++).BuildServiceProvider();

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                provider.GetRequiredService<FourArmEvaluationRunner>()
                    .RunAsync(runOptions, TestContext.Current.CancellationToken));

            foreach (var method in new[]
                     {
                         "AuthorizeCase", "OwnerOf", "Read", "GetBasisVersion", "ReadAudit", "ListAssessments",
                         "FindAssessment", "ListRequestReceipts", "ListWorkItems", "FindTrace"
                     })
            {
                Assert.Equal(0, state.Store.CountFor(method));
            }
            Assert.Equal(0, chat.Calls);
            Assert.Equal(0, factoryCalls);
        }
    }

    private static ServiceCollection CreateServices(
        ICaseStateStore store,
        MockRequestInbox inbox,
        ForbiddenOperationChatClient chat,
        Action onClientFactory)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICaseStateStore>(store);
        services.AddSingleton(inbox);
        services.AddSingleton<Func<ApprovedModelManifest, IChatClient>>(_ => _ =>
        {
            onClientFactory();
            return chat;
        });
        Program.RegisterProductionEvaluationExecutors(services);
        services.AddTransient<FourArmEvaluationRunner>();
        return services;
    }

    private static (CountingCaseStateStore Store, CaseScope Scope) CreateStateStore(string directory, string? suffix = null)
    {
        var store = new CountingCaseStateStore(
            new CaseStateStore(Path.Combine(directory, "case-state-" + (suffix ?? "default") + ".json")));
        var scope = store.CreateCase("evaluation-case", "owner");
        var basis = CaseStateStore.CreateBasisVersion(1, "seeded evaluation basis", string.Empty);
        store.Commit(scope, 0, new CaseCommit(basis, null, "owner", "submit-evidence", "seeded basis"));
        store.Reset();
        return (store, scope);
    }

    private static string[] Args(EvaluationRunOptions options) =>
    [
        "--suite-manifest", options.SuiteManifestPath,
        "--cases", options.CasesPath,
        "--arm-b-manifest", options.ArmBManifestPath,
        "--arm-c-manifest", options.ArmCManifestPath
    ];

    private static string SnapshotCase(ICaseStateStore store, CaseScope scope) =>
        JsonSerializer.Serialize(new
        {
            State = store.Read(scope),
            RequestReceipts = store.ListRequestReceipts(scope),
            Audit = store.ReadAudit(scope)
        });

    private static string SnapshotInbox(MockRequestInbox inbox) =>
        JsonSerializer.Serialize(new { Requests = inbox.Requests, Audit = inbox.Audit });

    private sealed class ForbiddenOperationChatClient(string[] operations) : IChatClient
    {
        private int _nextOperation;

        public int Calls { get; private set; }
        public List<string> RequestedOperations { get; } = [];
        public List<string[]> DeclaredToolSets { get; } = [];
        public ChatClientMetadata Metadata { get; } = new("read-only-evaluation-test");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            DeclaredToolSets.Add((options?.Tools ?? []).Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray());
            var operation = operations[_nextOperation++ % operations.Length];
            RequestedOperations.Add(operation);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("forbidden-operation", operation, new Dictionary<string, object?>())])));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CountingCaseStateStore(ICaseStateStore inner) : ICaseStateStore
    {
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);

        public int CountFor(string method) => _calls.GetValueOrDefault(method);
        public void Reset() => _calls.Clear();

        private void Count([CallerMemberName] string method = "") =>
            _calls[method] = CountFor(method) + 1;

        public CaseScope AuthorizeCase(string caseId, string owner) { Count(); return inner.AuthorizeCase(caseId, owner); }
        public string? OwnerOf(string caseId) { Count(); return inner.OwnerOf(caseId); }
        public CaseScope CreateCase(string caseId, string owner) { Count(); return inner.CreateCase(caseId, owner); }
        public CaseState Read(CaseScope scope) { Count(); return inner.Read(scope); }
        public BasisVersion GetBasisVersion(CaseScope scope, int version) { Count(); return inner.GetBasisVersion(scope, version); }
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) { Count(); return inner.ReadAudit(scope); }
        public IReadOnlyList<CaseAssessment> ListAssessments(CaseScope scope) { Count(); return inner.ListAssessments(scope); }
        public CaseAssessment? FindAssessment(CaseScope scope, string assessmentId) { Count(); return inner.FindAssessment(scope, assessmentId); }
        public IReadOnlyList<DeliveryReceipt> ListRequestReceipts(CaseScope scope) { Count(); return inner.ListRequestReceipts(scope); }
        public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit) { Count(); return inner.Commit(scope, expectedRevision, commit); }
        public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope) { Count(); return inner.ListWorkItems(scope); }
        public CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor) { Count(); return inner.CreateWorkItem(scope, expectedRevision, draft, actor); }
        public CommitResult ChangeWorkItemState(CaseScope scope, long expectedRevision, string itemId, string state, string actor) { Count(); return inner.ChangeWorkItemState(scope, expectedRevision, itemId, state, actor); }
        public void AppendTrace(CaseScope scope, InvestigationTrace trace) { Count(); inner.AppendTrace(scope, trace); }
        public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId)
        { Count(); return inner.FindTrace(scope, caseId, runId, investigationId); }
    }
}
