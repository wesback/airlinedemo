using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Copilot;
using AirlineDemo.Api.Foundry;
using AirlineDemo.Api.Persistence;
using Azure.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AirlineDemo.Api.Tests;

// AC-061: the presenter scenario on cp-case-a through the production startup, the real tools, the signed maintenance
// fixture and the in-memory stores. Only the model client and the Prompt Shields HTTP handler are fakes.
public sealed class CopilotShowcaseScenarioTests : IAsyncDisposable
{
    private const string CaseA = "cp-case-a";
    private const string TaskCard = "mx-cp-a-task-card-001";
    private const string Question = "What are the open points on this aircraft?";
    private const string Endpoint = "https://apim-demo.private.invalid/contentsafety";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"copilot-showcase-{Guid.NewGuid():N}.json");
    private readonly ScriptedChat _chat = new();
    private readonly FakeShield _shield = new();
    private readonly WebApplicationFactory<Program> _factory;

    public CopilotShowcaseScenarioTests() => _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Demo:Passcode"] = "test-passcode", ["CaseState:Path"] = _path, ["Demo:SeedCases"] = "true", ["Demo:SeedJourney"] = "true",
            ["ContentSafety:Endpoint"] = Endpoint, ["Copilot:AuditKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)11, 32).ToArray()),
        }));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IChatClient>(_chat);
            services.AddSingleton<IManagedConversationStore>(_ => new CopilotQuestionTests.MemoryManagedConversationStore(TimeProvider.System));
            services.AddSingleton<IManagedProjectClient>(provider =>
                new CopilotQuestionTests.ScriptedManagedProjectClient(provider.GetRequiredService<IChatClient>()));
            services.AddSingleton(FakeApprovedManifest.Approved());
            services.AddSingleton<Func<TokenCredential>>(_ => () => new FakeCredential());
            services.AddHttpClient(CopilotRecordScreener.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _shield);
        });
    });

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        File.Delete(_path);
    }

    [Fact]
    public async Task Showcase_TwoRuns_SameVerdictOneShieldCall()
    {
        using var client = _factory.CreateClient();
        var reviewer = _factory.Services.GetRequiredService<DemoSessionStore>().Create(
            new DemoUser("reviewer", "Technical reviewer", "technical-reviewer", [CaseA]));
        var store = _factory.Services.GetRequiredService<ICaseStateStore>();
        var scope = store.AuthorizeCase(CaseA, store.OwnerOf(CaseA)!);
        var revision = store.Read(scope).Revision;
        const string Detected = """{"state":"detected","service":"prompt-shields","flaggedFields":["taskText"]}""";

        var shieldCalls = new List<int>();
        for (var run = 0; run < 2; run++)
        {
            _chat.Reset();
            var reply = await Ask(client, reviewer);
            Assert.Equal(HttpStatusCode.OK, reply.Status);
            var json = JsonNode.Parse(reply.Body)!;
            // cp-case-a's technical review is accepted, so it has no technical open points; the maintenance list leads the
            // model to the task card, and only its identity reaches the model.
            Assert.Contains(""""points":[]"""", _chat.Observation(1), StringComparison.Ordinal);
            Assert.Contains(TaskCard, _chat.Observation(2), StringComparison.Ordinal);
            var card = Assert.Single(json["records"]!.AsArray(), record => (string?)record!["recordId"] == TaskCard)!;
            Assert.Equal(("maintenance", 1), ((string?)card["sourceId"], (int?)card["recordVersion"]));
            Assert.Equal(Detected, card["screening"]!.ToJsonString());
            Assert.Null(json["confirmation"]);
            Assert.DoesNotContain("screening", _chat.LastRequest, StringComparison.Ordinal);
            shieldCalls.Add(_shield.Calls);
        }
        Assert.Equal([1, 1], shieldCalls);
        Assert.Equal(revision, store.Read(scope).Revision);

        var ledger = await _factory.Services.GetRequiredService<ICopilotInvocationStore>().ReadCaseAsync(CaseA, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(ledger, row => row.Operation == "advance_stage");
        var detections = (await _factory.Services.GetRequiredService<ICopilotInvocationDetailStore>().ListForCaseAsync(CaseA, TestContext.Current.CancellationToken))
            .Where(row => row.Kind == "injection_detected").ToArray();
        // Both reads that return the card (the list and the record) log a detection, on each run.
        Assert.Equal(4, detections.Length);
        Assert.All(detections, row => Assert.Equal(("maintenance", TaskCard, 1, "detected"), (row.SourceId, row.RecordId, row.RecordVersion, row.ScreeningState)));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/cases/{CaseA}/copilot/activity");
        request.Headers.Add("Cookie", $"__Host-demo-session={reviewer.Id}");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["items"]!.AsArray();
        string[] turn = ["get_open_points", "read_source", "read_source", "question"];
        Assert.Equal([.. turn, .. turn], items.Select(item => (string?)item!["operation"]));
        Assert.DoesNotContain(items, item => (string?)item!["operation"] == "advance_stage");
        var reads = items.Where(item => (string?)item!["operation"] == "read_source").ToArray();
        Assert.Equal(4, reads.Length);
        Assert.All(reads, item =>
        {
            Assert.Equal("Technical reviewer", (string?)item!["user"]);
            Assert.Equal("copilot-client", (string?)item["provenance"]);
            Assert.Equal($$"""[{"sourceId":"maintenance","recordId":"{{TaskCard}}","recordVersion":1}]""", item["records"]!.ToJsonString());
            Assert.Equal("""{"state":"detected","flaggedFields":["taskText"]}""", item["screening"]!.ToJsonString());
        });
    }

    private static async Task<(HttpStatusCode Status, string Body)> Ask(HttpClient client, DemoSession session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CopilotQuestions.Path)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { question = Question, selectedCaseId = CaseA }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", "http://localhost");
        request.Headers.Add("Cookie", $"__Host-demo-session={session.Id}");
        request.Headers.Add("X-CSRF-Token", session.CsrfToken);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // The model asks for the open points, lists the maintenance source, reads the task card, then answers; it never calls advance_stage.
    private sealed class ScriptedChat : IChatClient
    {
        private readonly List<ChatMessage[]> _requests = [];
        public string LastRequest => JsonSerializer.Serialize(_requests[^1], AIJsonUtilities.DefaultOptions);
        public string Observation(int index) => string.Join('\n', _requests[index].SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>().Select(result => (string)result.Result!));

        public void Reset() => _requests.Clear();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var index = _requests.Count;
            _requests.Add([.. messages]);
            return Task.FromResult(index switch
            {
                0 => Call("t1", "get_open_points", $$"""{"caseId":"{{CaseA}}"}"""),
                1 => Call("t2", "read_source", $$"""{"caseId":"{{CaseA}}","operation":"list","sourceId":"maintenance"}"""),
                2 => Call("t3", "read_source", $$"""{"caseId":"{{CaseA}}","operation":"record","sourceId":"maintenance","recordId":"{{TaskCard}}"}"""),
                _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(new { answer = "One open task card remains on this aircraft." }))),
            });
        }

        private static ChatResponse Call(string id, string name, string json) =>
            new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(id, name, JsonSerializer.Deserialize<Dictionary<string, object?>>(json))
            {
                RawRepresentation = ChatToolCall.CreateFunctionToolCall(id, name, BinaryData.FromString(json)),
            }]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // Prompt Shields flags each document that carries the synthetic instruction marker.
    private sealed class FakeShield : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var documents = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!["documents"]!.AsArray();
            var analysis = new JsonArray([.. documents.Select(document => (JsonNode)new JsonObject
            {
                ["attackDetected"] = ((string?)document)?.StartsWith("SYN-INSTRUCTION", StringComparison.Ordinal) == true,
            })]);
            var body = new JsonObject { ["userPromptAnalysis"] = new JsonObject { ["attackDetected"] = false }, ["documentsAnalysis"] = analysis };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken) => new("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(context, cancellationToken));
    }
}
