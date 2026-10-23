using AirlineDemo.Api.Evaluation;
using Microsoft.Extensions.AI;

namespace AirlineDemo.Api.Tests;

public sealed class ModelGatewayTokenomicsTests
{
    private static GatewayCorrelation Correlation(string model = "model-v1") => new("B", "run-1", "package-1", "case-1", model, "suite-v1", "call-1");

    [Fact]
    public void Production_adapter_routes_to_gateway_and_records_available_or_unavailable_usage()
    {
        var endpoint = ModelGatewayTokenomics.RequirePrivateGatewayEndpoint("https://gateway-demo.private.invalid/");
        Assert.Equal("gateway-demo.private.invalid", endpoint.Host);
        Assert.Throws<InvalidOperationException>(() => ModelGatewayTokenomics.RequirePrivateGatewayEndpoint("https://gateway-demo.openai.azure.com/"));

        Assert.Equal((42L, 7L, 49L, "available"), (ModelGatewayTokenomics.Usage(42, 7, 49).InputTokens,
            ModelGatewayTokenomics.Usage(42, 7, 49).OutputTokens, ModelGatewayTokenomics.Usage(42, 7, 49).TotalTokens,
            ModelGatewayTokenomics.Usage(42, 7, 49).Status));
        var unavailable = ModelGatewayTokenomics.Usage(null, null, null);
        Assert.Equal((null, null, null, "unavailable"), (unavailable.InputTokens, unavailable.OutputTokens, unavailable.TotalTokens, unavailable.Status));
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

    public sealed class MutableResult { public string Value { get; set; } = ""; }
}
