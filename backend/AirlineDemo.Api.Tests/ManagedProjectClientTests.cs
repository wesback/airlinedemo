using System.Net;
using System.Data.Common;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Evaluation;
using AirlineDemo.Api.Foundry;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Deployment;
using Azure.Core;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class ManagedProjectClientTests : IDisposable
{
    private const string ConfigurationId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _statePath = Path.Combine(Path.GetTempPath(), $"managed-project-{Guid.NewGuid():N}.json");
    private static GatewayCorrelation Correlation(string digest = ConfigurationId) =>
        new("copilot", "run-1", "managed", "case-1", digest, "v1", "call-1");

    private WebApplicationFactory<Program> Host(Edge edge, Dictionary<string, string?>? overrides = null,
        DbContextOptions<CaseStateDbContext>? sqlOptions = null, TimeProvider? time = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Demo:Passcode"] = "test-passcode", ["CaseState:Path"] = _statePath,
            ["ModelGateway:Endpoint"] = "https://gateway.private.invalid/",
            ["ModelGateway:Audience"] = "api://managed-gateway",
            ["Foundry:GatewayEndpoint"] = "https://gateway.private.invalid/foundry/projects/airline-demo",
            ["Foundry:ProjectName"] = "airline-demo",
            ["Foundry:Agents:copilot:Name"] = "airline-copilot",
            ["Foundry:Agents:copilot:Version"] = "1",
            ["Foundry:Agents:copilot:ConfigurationId"] = ConfigurationId
        };
        foreach (var pair in overrides ?? []) settings[pair.Key] = pair.Value;
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<HttpMessageHandler>(edge);
                services.AddSingleton<Func<TokenCredential>>(_ => () => { edge.Credentials++; return edge; });
                if (sqlOptions is not null) services.AddSingleton(sqlOptions);
                if (time is not null) services.AddSingleton(time);
            });
        });
    }

    [Fact]
    public async Task ProductionHost_ManagedClient_IsLazyAndUsesOnlyGatewayScope()
    {
        var edge = new Edge();
        using var host = Host(edge);
        var client = host.Services.GetRequiredService<IManagedProjectClient>();
        Assert.Equal((0, 0, 0), (edge.Credentials, edge.Tokens, edge.Calls));
        var result = await client.CreateConversationAsync("copilot", "reservation-1", "attempt-1", Correlation(), TestContext.Current.CancellationToken);
        Assert.Equal("conv_1", result["id"]!.GetValue<string>());
        Assert.Equal((1, 1, 1), (edge.Credentials, edge.Tokens, edge.Calls));
        Assert.Equal(["api://managed-gateway/.default"], edge.Scopes);
        Assert.Equal("https://gateway.private.invalid/foundry/projects/airline-demo/openai/v1/conversations", edge.Uri);
        Assert.Equal("POST", edge.Method);
        Assert.Equal("{\"metadata\":{\"reservation_id\":\"reservation-1\",\"attempt_id\":\"attempt-1\"}}", edge.Body);
        Assert.Equal(["copilot", "run-1", "managed", "case-1", ConfigurationId, "v1", "call-1"],
            GatewayCorrelationHeadersPolicy.HeaderNames.Select(name => edge.Headers[name]).ToArray());
    }

    [Theory]
    [InlineData("Foundry:GatewayEndpoint", "https://account.services.ai.azure.com/api/projects/airline-demo")]
    [InlineData("Foundry:GatewayEndpoint", "https://other.private.invalid/foundry/projects/airline-demo")]
    [InlineData("Foundry:GatewayEndpoint", "https://gateway.private.invalid/foundry/projects/other")]
    [InlineData("Foundry:GatewayEndpoint", "https://gateway.private.invalid/foundry/projects/airline-demo?url=other")]
    [InlineData("Foundry:Agents:copilot:Version", "latest")]
    [InlineData("Foundry:Agents:copilot:ConfigurationId", "")]
    public async Task ProductionHost_ManagedClient_RefusesConfigurationBeforeCredentialsOrNetwork(string key, string value)
    {
        var edge = new Edge();
        using var host = Host(edge, new() { [key] = value });
        var failure = await Assert.ThrowsAsync<ManagedProjectException>(() => host.Services.GetRequiredService<IManagedProjectClient>()
            .ReadConversationAsync("copilot", "conv_1", Correlation(), TestContext.Current.CancellationToken));
        Assert.Equal("managed_agent_unconfigured", failure.Code);
        Assert.False(failure.UncertainWrite);
        Assert.Equal((0, 0, 0), (edge.Credentials, edge.Tokens, edge.Calls));
    }

    [Fact]
    public async Task ProductionHost_ManagedGateway_AllowlistAndNoFallback()
    {
        var edge = new Edge { Status = HttpStatusCode.Forbidden, Response = "PRIVATE REMOTE ERROR MUST NOT ESCAPE" };
        using var host = Host(edge);
        var failure = await Assert.ThrowsAsync<ManagedProjectException>(() => host.Services.GetRequiredService<IManagedProjectClient>()
            .CreateResponseAsync("copilot", "conv_1", new JsonArray(), Correlation(), TestContext.Current.CancellationToken));
        Assert.Equal("model_unavailable", failure.Message);
        Assert.False(failure.UncertainWrite);
        Assert.Equal(1, edge.Calls);
        Assert.Equal("https://gateway.private.invalid/foundry/projects/airline-demo/openai/v1/responses", edge.Uri);
        Assert.Equal(new JsonObject
        {
            ["agent_reference"] = new JsonObject { ["type"] = "agent_reference", ["name"] = "airline-copilot", ["version"] = "1" },
            ["conversation"] = "conv_1", ["input"] = new JsonArray(), ["max_output_tokens"] = 2000,
            ["truncation"] = "disabled", ["background"] = false, ["stream"] = false,
            ["metadata"] = new JsonObject { ["submission_id"] = "call-1" }
        }.ToJsonString(), edge.Body);
    }

    [Theory]
    [InlineData("create", "POST", "conversations")]
    [InlineData("read", "GET", "conversations/conv_1")]
    [InlineData("delete", "DELETE", "conversations/conv_1")]
    [InlineData("items", "GET", "conversations/conv_1/items?limit=100&order=asc&after=item_1")]
    [InlineData("item-delete", "DELETE", "conversations/conv_1/items/item_1")]
    [InlineData("response-create", "POST", "responses")]
    [InlineData("response-read", "GET", "responses/resp_1")]
    [InlineData("response-delete", "DELETE", "responses/resp_1")]
    public async Task ProductionHost_ManagedClient_ExactEightOperationTable(string operation, string method, string path)
    {
        var edge = new Edge();
        using var host = Host(edge);
        var client = host.Services.GetRequiredService<IManagedProjectClient>();
        var token = TestContext.Current.CancellationToken;
        _ = await (operation switch
        {
            "create" => client.CreateConversationAsync("copilot", "reservation-1", "attempt-1", Correlation(), token),
            "read" => client.ReadConversationAsync("copilot", "conv_1", Correlation(), token),
            "delete" => client.DeleteConversationAsync("copilot", "conv_1", Correlation(), token),
            "items" => client.ListItemsAsync("copilot", "conv_1", "item_1", Correlation(), token),
            "item-delete" => client.DeleteItemAsync("copilot", "conv_1", "item_1", Correlation(), token),
            "response-create" => client.CreateResponseAsync("copilot", "conv_1", new JsonArray(), Correlation(), token),
            "response-read" => client.ReadResponseAsync("copilot", "resp_1", Correlation(), token),
            _ => client.DeleteResponseAsync("copilot", "resp_1", Correlation(), token)
        });
        Assert.Equal(method, edge.Method);
        Assert.Equal("https://gateway.private.invalid/foundry/projects/airline-demo/openai/v1/" + path, edge.Uri);
        Assert.Equal(1, edge.Calls);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ManagedWrite_LostReply_IsUncertainWithoutSecondWrite(bool write, bool uncertain)
    {
        var edge = new Edge { LoseReply = true };
        using var host = Host(edge);
        var client = host.Services.GetRequiredService<IManagedProjectClient>();
        var failure = await Assert.ThrowsAsync<ManagedProjectException>(() => write
            ? client.CreateConversationAsync("copilot", "reservation-1", "attempt-1", Correlation(), TestContext.Current.CancellationToken)
            : client.ReadConversationAsync("copilot", "conv_1", Correlation(), TestContext.Current.CancellationToken));
        Assert.Equal("model_unavailable", failure.Code);
        Assert.Equal(uncertain, failure.UncertainWrite);
        Assert.Equal(1, edge.Calls);
    }

    [Theory]
    [InlineData(true, "[]")]
    [InlineData(false, "null")]
    [InlineData(true, "{\"id\":\"conv_1\",\"id\":\"conv_other\"}")]
    [InlineData(false, "{\"data\":[{\"id\":\"item_1\",\"id\":\"item_other\"}]}")]
    public async Task ManagedClient_InvalidSuccessfulJsonFailsClosedBeforeObjectAdmission(bool write, string body)
    {
        var edge = new Edge { Response = body };
        using var host = Host(edge);
        var client = host.Services.GetRequiredService<IManagedProjectClient>();
        var failure = await Assert.ThrowsAsync<ManagedProjectException>(() => write
            ? client.CreateConversationAsync("copilot", "reservation-1", "attempt-1", Correlation(), TestContext.Current.CancellationToken)
            : client.ListItemsAsync("copilot", "conv_1", null, Correlation(), TestContext.Current.CancellationToken));
        Assert.Equal("model_unavailable", failure.Code);
        Assert.Equal(write, failure.UncertainWrite);
        Assert.Equal(1, edge.Calls);
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("conv_1%2Fitems")]
    [InlineData("conv_1?url=other")]
    [InlineData("conv_1\n")]
    [InlineData("")]
    public async Task ManagedClient_InvalidServiceIdNeverReachesNetwork(string id)
    {
        var edge = new Edge();
        using var host = Host(edge);
        var failure = await Assert.ThrowsAsync<ManagedProjectException>(async () =>
            await host.Services.GetRequiredService<IManagedProjectClient>().ReadConversationAsync("copilot", id, Correlation(), TestContext.Current.CancellationToken));
        Assert.Equal("conversation_history_invalid", failure.Code);
        Assert.Equal((0, 0, 0), (edge.Credentials, edge.Tokens, edge.Calls));
    }

    private static ManagedConversationTuple Tuple() => new("demo-user-1", "presenter", "cp-case-a", "entitlements-digest",
        0, "airline-copilot", "1", ConfigurationId);

    [Fact]
    public async Task ProductionHost_ManagedStoreUnavailable_PreventsRemoteCreate()
    {
        var edge = new Edge();
        using var host = Host(edge);
        var lifecycle = host.Services.GetRequiredService<ManagedConversationLifecycle>();
        var failure = await Assert.ThrowsAsync<ManagedConversationStoreException>(() =>
            lifecycle.CreateAsync("copilot", Tuple(), Correlation(), Tuple, TestContext.Current.CancellationToken));
        Assert.Equal("conversation_store_unavailable", failure.Code);
        Assert.Equal((0, 0, 0), (edge.Credentials, edge.Tokens, edge.Calls));
    }

    [Fact]
    public async Task ManagedCreate_LostReply_IsReconciledWithoutSecondCreate()
    {
        using var anchor = new SqliteConnection("Data Source=:memory:");
        await anchor.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlite(anchor).Options;
        using (var context = new CaseStateDbContext(options)) await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var edge = new Edge { LoseReply = true };
        using var host = Host(edge, sqlOptions: options);
        var store = host.Services.GetRequiredService<IManagedConversationStore>();
        Assert.IsType<SqlManagedConversationStore>(store);
        var failure = await Assert.ThrowsAsync<ManagedProjectException>(() => host.Services.GetRequiredService<ManagedConversationLifecycle>()
            .CreateAsync("copilot", Tuple(), Correlation(), Tuple, TestContext.Current.CancellationToken));
        Assert.Equal("managed_operation_ambiguous", failure.Code);
        Assert.True(failure.UncertainWrite);
        var remote = Assert.Single(edge.CreatedConversations);
        var handle = remote["metadata"]!["reservation_id"]!.GetValue<string>();
        var tombstone = store.ReadManagedConversation(handle)!;
        Assert.True(tombstone.Retired);
        Assert.Null(tombstone.RemoteConversationId);
        Assert.Equal(2, tombstone.RowRevision);
        var reads = 0;
        var recoveredId = await ManagedCreateRecovery.ResolveConversationAsync(tombstone.Handle, tombstone.AttemptId, (_, _) =>
        {
            reads++;
            return Task.FromResult(new JsonObject { ["data"] = new JsonArray(remote.DeepClone()), ["has_more"] = false });
        }, () => reads < 2, TestContext.Current.CancellationToken);
        Assert.Equal("conv_1", recoveredId);
        var recovered = store.AttachRecoveredRemoteConversationId(handle, Tuple(), tombstone.AttemptId, recoveredId!, tombstone.RowRevision);
        Assert.Equal(("conv_1", true, 3L, null), (recovered.RemoteConversationId, recovered.Retired, recovered.RowRevision, recovered.ActiveFence));
        Assert.Equal((1, 1, 1), (edge.Calls, edge.CreatedConversations.Count, reads));
    }

    [Fact]
    public async Task ProductionHost_ManagedCreate_ContextChangedAfterAwaitRetiresReservation()
    {
        using var anchor = new SqliteConnection("Data Source=:memory:");
        await anchor.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlite(anchor).Options;
        using (var context = new CaseStateDbContext(options)) await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var current = Tuple();
        var edge = new Edge { OnRequest = () => current = current with { CaseRevision = 1 } };
        using var host = Host(edge, sqlOptions: options);
        var failure = await Assert.ThrowsAsync<ManagedProjectException>(() => host.Services.GetRequiredService<ManagedConversationLifecycle>()
            .CreateAsync("copilot", Tuple(), Correlation(), () => current, TestContext.Current.CancellationToken));
        Assert.Equal("conversation_context_mismatch", failure.Code);
        var handle = Assert.Single(edge.CreatedConversations)["metadata"]!["reservation_id"]!.GetValue<string>();
        var tombstone = host.Services.GetRequiredService<IManagedConversationStore>().ReadManagedConversation(handle)!;
        Assert.Equal((true, 2L, null), (tombstone.Retired, tombstone.RowRevision, tombstone.RemoteConversationId));
        Assert.Equal(1, edge.Calls);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("completion", "managed_operation_ambiguous")]
    [InlineData("context", "conversation_context_mismatch")]
    [InlineData("deadline", "copilot_timeout")]
    [InlineData("lost-reply", "managed_operation_ambiguous")]
    [InlineData("cancellation", "managed_operation_ambiguous")]
    public async Task ProductionHost_Conversation_AmbiguousWritePoisonsHistory(string? fault, string? expectedCode)
    {
        using var anchor = new SqliteConnection("Data Source=:memory:");
        await anchor.OpenAsync(TestContext.Current.CancellationToken);
        var writes = new FailNextManagedUpdate();
        var options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlite(anchor).AddInterceptors(writes).Options;
        using (var context = new CaseStateDbContext(options)) await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        new SqlCaseStateStore(options).CreateCase(Tuple().CaseId, Tuple().UserId);
        var time = new MutableTime();
        var current = Tuple();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var edge = new Edge();
        using var host = Host(edge, sqlOptions: options, time: time);
        var lifecycle = host.Services.GetRequiredService<ManagedConversationLifecycle>();
        var store = host.Services.GetRequiredService<IManagedConversationStore>();
        var conversation = await lifecycle.CreateAsync("copilot", current, Correlation(), () => current, cancellation.Token);
        edge.Response = "{\"id\":\"resp_1\",\"output\":[]}";
        edge.OnRequest = () =>
        {
            if (fault == "completion") writes.Fail = true;
            if (fault == "context") current = current with { EntitlementFingerprint = "changed" };
            if (fault == "deadline") time.Now = new DateTimeOffset(store.ReadManagedConversation(conversation.Handle)!.TurnDeadlineTicks!.Value, TimeSpan.Zero);
            if (fault == "cancellation") cancellation.Cancel();
        };
        edge.LoseReply = fault == "lost-reply";
        var operation = lifecycle.ExecuteTurnAsync(conversation, () => current, async token =>
        {
            var response = await host.Services.GetRequiredService<IManagedProjectClient>()
                .CreateResponseAsync("copilot", conversation.RemoteConversationId!, new JsonArray(), Correlation(), token);
            return (response["id"]!.GetValue<string>(), new ManagedTurnCompletionMetadata("resp_1", [], ConfigurationId));
        }, cancellation.Token);
        if (expectedCode is null) Assert.Equal("resp_1", await operation);
        else Assert.Equal(expectedCode, (await Assert.ThrowsAsync<ManagedProjectException>(() => operation)).Code);
        var final = store.ReadManagedConversation(conversation.Handle)!;
        Assert.Equal(fault is not null, final.Retired);
        Assert.Null(final.ActiveFence);
        Assert.Equal(1, final.AdmittedTurnCount);
        Assert.Equal(2, edge.Calls);
        if (fault is not null)
        {
            Assert.Equal("conversation_unavailable", Assert.Throws<ManagedConversationStoreException>(() =>
                store.AdmitManagedTurn(final.Handle, Tuple(), final.RowRevision, Tuple().CaseRevision)).Code);
        }
    }

    [Fact]
    public async Task ProductionHost_Conversation_LateOldFenceCannotClearNewBinding()
    {
        using var anchor = new SqliteConnection("Data Source=:memory:");
        await anchor.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlite(anchor).Options;
        using (var context = new CaseStateDbContext(options)) await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        new SqlCaseStateStore(options).CreateCase(Tuple().CaseId, Tuple().UserId);
        var edge = new Edge();
        using var host = Host(edge, sqlOptions: options);
        var lifecycle = host.Services.GetRequiredService<ManagedConversationLifecycle>();
        var store = host.Services.GetRequiredService<IManagedConversationStore>();
        var old = await lifecycle.CreateAsync("copilot", Tuple(), Correlation(), Tuple, TestContext.Current.CancellationToken);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = lifecycle.ExecuteTurnAsync(old, Tuple, async token =>
        {
            await host.Services.GetRequiredService<IManagedProjectClient>().CreateResponseAsync(
                "copilot", old.RemoteConversationId!, new JsonArray(), Correlation(), token);
            accepted.SetResult();
            await release.Task.WaitAsync(token);
            return ("LATE_OUTPUT", new ManagedTurnCompletionMetadata("resp_old", [], ConfigurationId));
        }, TestContext.Current.CancellationToken);
        await accepted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var active = store.ReadManagedConversation(old.Handle)!;
        Assert.True(store.RetireManagedConversation(active.Handle, active.Tuple, active.RowRevision, active.ActiveFence));
        edge.Response = "{\"id\":\"conv_new\"}";
        var next = await lifecycle.CreateAsync("copilot", Tuple(), Correlation(), Tuple, TestContext.Current.CancellationToken);
        var nextAdmission = store.AdmitManagedTurn(next.Handle, next.Tuple, next.RowRevision, next.Tuple.CaseRevision);
        release.SetResult();
        Assert.Equal("managed_operation_ambiguous", (await Assert.ThrowsAsync<ManagedProjectException>(() => late)).Code);
        Assert.True(store.ReadManagedConversation(old.Handle)!.Retired);
        var unchanged = store.ReadManagedConversation(next.Handle)!;
        Assert.Equal((false, nextAdmission.Fence, nextAdmission.RowRevision, 1),
            (unchanged.Retired, unchanged.ActiveFence, unchanged.RowRevision, unchanged.AdmittedTurnCount));
        Assert.Equal(3, edge.Calls);
    }

    public void Dispose()
    {
        File.Delete(_statePath);
    }

    private sealed class MutableTime : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FailNextManagedUpdate : DbCommandInterceptor
    {
        public bool Fail;
        private void Check(DbCommand command)
        {
            if (Fail && command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("managed_conversations", StringComparison.Ordinal))
            {
                Fail = false;
                throw new InvalidOperationException("injected SQL completion failure");
            }
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Check(command);
            return result;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Check(command);
            return result;
        }
    }

    private sealed class Edge : HttpMessageHandler
    {
        public int Credentials, Tokens, Calls;
        public string[] Scopes = [];
        public string? Uri, Method, Body;
        public Dictionary<string, string> Headers = [];
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Response = "{\"id\":\"conv_1\",\"object\":\"conversation\"}";
        public bool LoseReply;
        public Action? OnRequest;
        public List<JsonObject> CreatedConversations = [];

        public static implicit operator TokenCredential(Edge edge) => new EdgeCredential(edge);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri!.AbsoluteUri; Method = request.Method.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Headers = request.Headers.ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase);
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/conversations", StringComparison.Ordinal))
                CreatedConversations.Add(new JsonObject { ["id"] = "conv_1", ["metadata"] = JsonNode.Parse(Body!)!["metadata"]!.DeepClone() });
            OnRequest?.Invoke();
            if (LoseReply) throw new HttpRequestException("REMOTE CONTENT");
            return new HttpResponseMessage(Status) { Content = new StringContent(Response) };
        }

        private sealed class EdgeCredential(Edge edge) : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                edge.Tokens++; edge.Scopes = requestContext.Scopes;
                return new AccessToken("synthetic-token", DateTimeOffset.UtcNow.AddMinutes(5));
            }
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                ValueTask.FromResult(GetToken(requestContext, cancellationToken));
        }
    }
}
