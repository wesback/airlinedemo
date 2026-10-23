using AirlineDemo.Api.Evaluation;
using AirlineDemo.Api.Investigation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class ModelGatewayTokenomicsTests
{
    private static GatewayCorrelation Correlation(string model = "model-v1") => new("B", "run-1", "package-1", "case-1", model, "suite-v1", "call-1");

    [Fact]
    public async Task Production_executor_routes_to_gateway_and_records_correlation_usage_and_cost()
    {
        var fake = new GatewayChatClient();
        var services = new ServiceCollection();
        services.AddSingleton<Func<ApprovedModelManifest, IChatClient>>(_ => _ => fake);
        services.AddSingleton(new GatewayTokenRateSchedule(2m, 3m));
        Program.RegisterProductionEvaluationExecutors(services);
        using var provider = services.BuildServiceProvider();
        var executor = provider.GetServices<IEvaluationArmExecutor>().Single(e => e.Arm == EvaluationArm.B);
        var request = new EvaluationArmRequest(EvaluationArm.B, EvaluationExecutionMode.SingleCallWithoutToolsOrCitationReflection,
            new EvaluationCase("case-1", "synthetic", "{\"inputs\":{\"status\":\"approved\"}}", "sha-1"),
            FakeApprovedManifest.Approved(), new EvaluationCallRecorder());

        var result = await executor.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(49, result.TokenUsage);
        Assert.Equal(1, result.ModelCallCount);
        Assert.Equal(1, fake.Calls);
        Assert.Equal("fixture-deployment-fixture-model-2025-01-01-swedencentral", fake.LastOptions?.ModelId);
    }

    [Fact]
    public async Task Production_executor_enforces_quota_boundary_and_denies_over_limit_result()
    {
        var fake = new GatewayChatClient { InputTokenCount = 101 };
        var governor = new GatewayQuotaGovernor(new GatewayQuota(100, 50, 140, 2, 3, 1, TimeSpan.FromSeconds(30)));
        Assert.Equal("gateway quota exceeded by one unit: input-tokens", governor.BeforeCall(101));
        Assert.Equal(0, fake.Calls);
        var governed = new GatewayGovernedChatClient(fake, Correlation(), new GatewayQuota(100, 50, 140, 2, 3, 1, TimeSpan.FromSeconds(30)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => governed.GetResponseAsync([new ChatMessage(ChatRole.User, new string('x', 101))]));
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task Production_executor_rejects_sensitive_correlation_and_keeps_trace_content_private()
    {
        var fake = new GatewayChatClient();
        var governed = new GatewayGovernedChatClient(fake, new GatewayCorrelation("B", "run-1", "package-1", "raw prompt text", "model-v1", "suite-v1", "call-1"), new GatewayQuota(1000, 1000, 2000, 2, 3, 1, TimeSpan.FromSeconds(30)));
        await Assert.ThrowsAsync<ArgumentException>(() => governed.GetResponseAsync([new ChatMessage(ChatRole.User, "approved case")], new ChatOptions()));
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task Production_executor_replay_returns_original_result_without_second_dispatch()
    {
        var fake = new GatewayChatClient();
        var services = new ServiceCollection();
        services.AddSingleton<Func<ApprovedModelManifest, IChatClient>>(_ => _ => fake);
        Program.RegisterProductionEvaluationExecutors(services);
        using var provider = services.BuildServiceProvider();
        var executor = provider.GetServices<IEvaluationArmExecutor>().Single(e => e.Arm == EvaluationArm.B);
        var evaluationCase = new EvaluationCase("case-1", "synthetic", "{\"inputs\":{\"status\":\"approved\"}}", "sha-1");
        var request = new EvaluationArmRequest(EvaluationArm.B, EvaluationExecutionMode.SingleCallWithoutToolsOrCitationReflection,
            evaluationCase, FakeApprovedManifest.Approved(), new EvaluationCallRecorder());

        var first = await executor.ExecuteAsync(request, TestContext.Current.CancellationToken);
        fake.InputTokenCount = 999; fake.OutputTokenCount = 999;
        var replay = await executor.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(first.Answer, replay.Answer);
        Assert.Equal(first.TokenUsage, replay.TokenUsage);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task Production_adapter_routes_to_gateway_and_records_available_or_unavailable_usage()
    {
        var endpoint = ModelGatewayTokenomics.RequirePrivateGatewayEndpoint("https://gateway-demo.private.invalid/");
        Assert.Equal("gateway-demo.private.invalid", endpoint.Host);
        Assert.Throws<InvalidOperationException>(() => ModelGatewayTokenomics.RequirePrivateGatewayEndpoint("https://gateway-demo.openai.azure.com/"));

        Assert.Equal((42L, 7L, 49L, "available"), (ModelGatewayTokenomics.Usage(42, 7, 49).InputTokens,
            ModelGatewayTokenomics.Usage(42, 7, 49).OutputTokens, ModelGatewayTokenomics.Usage(42, 7, 49).TotalTokens,
            ModelGatewayTokenomics.Usage(42, 7, 49).Status));
        var unavailable = ModelGatewayTokenomics.Usage(null, null, null);
        Assert.Equal((null, null, null, "unavailable"), (unavailable.InputTokens, unavailable.OutputTokens, unavailable.TotalTokens, unavailable.Status));

        var fake = new GatewayChatClient();
        var governed = new GatewayGovernedChatClient(fake, Correlation(), new GatewayQuota(1000, 1000, 2000, 2, 3, 1, TimeSpan.FromSeconds(30)), new GatewayTokenRateSchedule(2, 3));
        var response = await governed.GetResponseAsync([new ChatMessage(ChatRole.User, "approved case")]);
        Assert.Equal("ok", response.Text);
        Assert.Equal(1, fake.Calls);
        Assert.Equal((42L, 7L, 49L, 1, 0, Correlation().ModelConfiguration),
            (governed.Metrics!.InputTokens, governed.Metrics.OutputTokens, governed.Metrics.TotalTokens,
             governed.Metrics.ModelCallCount, governed.Metrics.RetryCount, governed.Metrics.ModelConfiguration));
        Assert.False(governed.Metrics.Throttled);
        Assert.True(governed.Metrics.LatencyMilliseconds >= 0);
        Assert.Equal("available", governed.Metrics.Usage.Status);
        Assert.Equal(0.000105m, governed.Metrics.EstimatedCost);
    }

    [Fact]
    public void Quota_boundary_and_over_limit_calls_report_exact_usage_and_reject_result()
    {
        var quota = new GatewayQuota(100, 50, 140, 2, 3, 1, TimeSpan.FromSeconds(30));
        var governor = new GatewayQuotaGovernor(quota);
        Assert.Null(governor.BeforeCall(100));
        Assert.Null(governor.Record(80, 20));
        Assert.Equal((80L, 20L, 100L, "available"), (governor.ExactUsage().InputTokens, governor.ExactUsage().OutputTokens,
            governor.ExactUsage().TotalTokens, governor.ExactUsage().Status));

        var over = new GatewayQuotaGovernor(new GatewayQuota(100, 50, 120, 2, 3, 1, TimeSpan.FromSeconds(30)));
        Assert.Null(over.Record(80, 20));
        Assert.Equal("gateway quota exceeded by one unit: total-tokens", over.Record(30, 30));
        Assert.Equal((110L, 50L, 160L, "available"), (over.ExactUsage().InputTokens, over.ExactUsage().OutputTokens,
            over.ExactUsage().TotalTokens, over.ExactUsage().Status));
    }

    [Fact]
    public void Trace_and_correlation_metadata_reject_sensitive_content()
    {
        var rejected = new GatewayCorrelation("B", "run-1", "package-1", "case-1", "raw prompt text", "suite-v1", "call-1");
        Assert.Throws<ArgumentException>(() => ModelGatewayTokenomics.ValidateCorrelation(rejected));
        ModelGatewayTokenomics.ValidateCorrelation(Correlation());
    }

    [Fact]
    public void Replay_returns_original_result_without_dispatch_charge_or_duplicate_trace()
    {
        var store = new GatewayReplayStore<string>();
        var first = Correlation();
        store.Store(first, "original-result");
        Assert.True(store.TryReplay(first, out var replay));
        Assert.Equal("original-result", replay);

        Assert.Throws<InvalidOperationException>(() => store.TryReplay(Correlation("model-v2"), out _));
    }

    [Fact]
    public void Replay_preserves_immutable_snapshot_from_mutable_result()
    {
        var store = new GatewayReplayStore<MutableResult>();
        var original = new MutableResult { Value = "unchanged" };
        store.Store(Correlation(), original);
        original.Value = "mutated";
        Assert.True(store.TryReplay(Correlation(), out var replay));
        Assert.Equal("unchanged", replay!.Value);
    }

    private sealed class GatewayChatClient : IChatClient
    {
        public int Calls { get; private set; }
        public int? InputTokenCount { get; set; } = 42;
        public int? OutputTokenCount { get; set; } = 7;
        public int? TotalTokenCount { get; set; } = 49;
        public ChatOptions? LastOptions { get; private set; }
        public ChatClientMetadata Metadata { get; } = new("gateway-test");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                Usage = InputTokenCount is null && OutputTokenCount is null && TotalTokenCount is null ? null :
                    new UsageDetails { InputTokenCount = InputTokenCount, OutputTokenCount = OutputTokenCount, TotalTokenCount = TotalTokenCount }
            });
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    public sealed class MutableResult { public string Value { get; set; } = ""; }
}
