using AirlineDemo.Api.Investigation;
using Microsoft.Extensions.AI;

namespace AirlineDemo.Api.Tests;

/// <summary>Covers M-09: per-investigation tool, model, time, token and retry limits, bounded backoff and release.</summary>
public class InvestigationLimitsTests
{
    private static InvestigationLimits Limits(InvestigationLimitOptions? options = null, Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(options, delay ?? ((_, _) => Task.CompletedTask));

    [Fact]
    public async Task Twenty_tool_calls_are_allowed_and_the_twenty_first_breaches()
    {
        using var limits = Limits();
        for (var i = 0; i < 20; i++)
        {
            await limits.BeforeToolCallAsync(CancellationToken.None);
        }

        var ex = await Assert.ThrowsAsync<InvestigationLimitExceededException>(async () => await limits.BeforeToolCallAsync(CancellationToken.None));
        Assert.Equal(InvestigationBreach.ToolCalls, ex.Breach);
        Assert.Equal(InvestigationBreach.ToolCalls, limits.Breach);
    }

    [Fact]
    public async Task Eight_model_calls_are_allowed_and_the_ninth_breaches()
    {
        using var limits = Limits();
        for (var i = 0; i < 8; i++)
        {
            await limits.BeforeModelCallAsync(CancellationToken.None);
        }

        var ex = await Assert.ThrowsAsync<InvestigationLimitExceededException>(async () => await limits.BeforeModelCallAsync(CancellationToken.None));
        Assert.Equal(InvestigationBreach.ModelCalls, ex.Breach);
    }

    [Fact]
    public async Task Input_tokens_at_the_cap_pass_and_one_over_breaches()
    {
        using var limits = Limits();
        await limits.BeforeModelCallAsync(16_000, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvestigationLimitExceededException>(async () => await limits.BeforeModelCallAsync(16_001, CancellationToken.None));
        Assert.Equal(InvestigationBreach.InputTokens, ex.Breach);
    }

    [Fact]
    public async Task Output_tokens_at_the_cap_pass_and_one_over_breaches()
    {
        using var limits = Limits();
        await limits.AfterModelCallAsync(1, 2_000, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvestigationLimitExceededException>(async () => await limits.AfterModelCallAsync(1, 2_001, CancellationToken.None));
        Assert.Equal(InvestigationBreach.OutputTokens, ex.Breach);
    }

    [Fact]
    public async Task Exceeding_the_time_limit_breaches_with_time()
    {
        using var limits = Limits(new InvestigationLimitOptions { Timeout = TimeSpan.FromMilliseconds(30) });
        await Task.Delay(200);

        var ex = await Assert.ThrowsAsync<InvestigationLimitExceededException>(async () => await limits.BeforeToolCallAsync(CancellationToken.None));
        Assert.Equal(InvestigationBreach.Time, ex.Breach);
    }

    [Fact]
    public async Task Two_retries_are_allowed_and_the_third_failure_breaches()
    {
        using var limits = Limits();
        Assert.True(await limits.TryBackoffAsync(0, CancellationToken.None));
        Assert.True(await limits.TryBackoffAsync(1, CancellationToken.None));

        var ex = await Assert.ThrowsAsync<InvestigationLimitExceededException>(async () => await limits.TryBackoffAsync(2, CancellationToken.None));
        Assert.Equal(InvestigationBreach.Retries, ex.Breach);
    }

    [Fact]
    public async Task Backoff_doubles_from_the_initial_delay()
    {
        var delays = new List<TimeSpan>();
        using var limits = Limits(delay: (duration, _) =>
        {
            delays.Add(duration);
            return Task.CompletedTask;
        });

        await limits.TryBackoffAsync(0, CancellationToken.None);
        await limits.TryBackoffAsync(1, CancellationToken.None);

        Assert.Equal([TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000)], delays);
        Assert.Equal(delays, limits.Backoffs);
    }

    [Fact]
    public async Task A_breach_is_sticky_so_later_calls_report_the_same_breach()
    {
        using var limits = Limits(new InvestigationLimitOptions { MaxToolCalls = 1 });
        await limits.BeforeToolCallAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvestigationLimitExceededException>(async () => await limits.BeforeToolCallAsync(CancellationToken.None));

        var ex = await Assert.ThrowsAsync<InvestigationLimitExceededException>(async () => await limits.BeforeModelCallAsync(CancellationToken.None));
        Assert.Equal(InvestigationBreach.ToolCalls, ex.Breach);
    }

    [Fact]
    public async Task Dispose_releases_the_deadline_so_no_later_timeout_is_recorded()
    {
        var limits = Limits(new InvestigationLimitOptions { Timeout = TimeSpan.FromMilliseconds(30) });
        limits.Dispose();
        await Task.Delay(150);

        Assert.True(limits.Released);
        Assert.Null(limits.Breach);
    }

    [Fact]
    public async Task A_model_call_breach_stops_the_investigation_with_no_result_and_records_a_trace()
    {
        var logger = new TraceLogger();
        using var limits = new InvestigationLimits(new InvestigationLimitOptions { MaxModelCalls = 0 });
        var investigator = new BoundedInvestigator(
            "case-1", new EmptySource(), ApprovedManifest(), SyntheticContentRegistry.FromCommittedFixtures(), "eastus2", "evidence-v1",
            new NoReplyClient(), limits, new InvestigationTelemetry(logger, "synthetic-analyst-01", TestKey, "1.2.3", "demo"));

        var ex = await Assert.ThrowsAsync<InvestigationLimitExceededException>(() =>
            investigator.InvestigateAsync(["SYNTHETIC EVIDENCE: Return readiness is pending reserve approval."]));

        Assert.Equal(InvestigationBreach.ModelCalls, ex.Breach);
        Assert.Equal(2, logger.States.Count);
        var values = logger.States.SelectMany(s => s).Select(kv => kv.Value?.ToString()).ToList();
        Assert.Contains("fixture-deployment", values);
        Assert.Contains("escalated", values);
        Assert.DoesNotContain(values, v => v is not null && v.Contains("Return readiness", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_finished_investigation_returns_claims_releases_limits_and_traces_its_fields()
    {
        var logger = new TraceLogger();
        using var limits = Limits();

        var calls = 0;
        var model = new ScriptedClient(_ => calls++ == 0 ? Task.FromResult(SourcePageCall()) : Reply(GoodReply));

        var result = await Investigator(model, limits, logger).InvestigateAsync([Doc]);

        Assert.Equal(2, calls);
        Assert.Single(result.Claims);
        Assert.True(limits.Released);
        Assert.Null(limits.Breach);
        Assert.Single(logger.States);
        var values = Values(logger);
        Assert.Contains("approved", values);
        Assert.Contains("escalate", values);
        Assert.Contains("fixture-deployment", values);
        Assert.Contains("fixture-model", values);
        Assert.Contains("basis-v1", values);
        Assert.Contains(CaseReadTools.SourcePageTool, values);
        Assert.Contains("doc-1#p1", values);
        Assert.Contains(values, v => v?.StartsWith("trace-", StringComparison.Ordinal) == true);
        Assert.Contains(values, v => v?.StartsWith("actor-", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(values, v => v is not null && (v.Contains("Return readiness", StringComparison.Ordinal) || v.Contains("synthetic-analyst-01", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_tool_call_breach_stops_the_investigation_with_an_explicit_recorded_breach()
    {
        var logger = new TraceLogger();
        using var limits = Limits(new InvestigationLimitOptions { MaxToolCalls = 0 });
        var model = new ScriptedClient(_ => Task.FromResult(ToolCall(CaseReadTools.InventoryTool)));

        var ex = await Assert.ThrowsAsync<InvestigationLimitExceededException>(() => Investigator(model, limits, logger).InvestigateAsync([Doc]));

        AssertBreach(ex, limits, logger, InvestigationBreach.ToolCalls);
        Assert.Contains(CaseReadTools.InventoryTool, Values(logger));
    }

    [Fact]
    public async Task An_input_token_breach_stops_the_investigation_with_no_result_and_a_recorded_breach()
    {
        var logger = new TraceLogger();
        using var limits = Limits(new InvestigationLimitOptions { MaxInputTokensPerCall = 1 });
        var model = new ScriptedClient(_ => Reply(GoodReply));

        var ex = await Assert.ThrowsAsync<InvestigationLimitExceededException>(() => Investigator(model, limits, logger).InvestigateAsync([Doc]));

        AssertBreach(ex, limits, logger, InvestigationBreach.InputTokens);
    }

    [Fact]
    public async Task An_output_token_breach_stops_the_investigation_with_no_result_and_a_recorded_breach()
    {
        var logger = new TraceLogger();
        using var limits = Limits();
        var model = new ScriptedClient(_ => Reply(GoodReply, outputTokens: 5_000));

        var ex = await Assert.ThrowsAsync<InvestigationLimitExceededException>(() => Investigator(model, limits, logger).InvestigateAsync([Doc]));

        AssertBreach(ex, limits, logger, InvestigationBreach.OutputTokens);
    }

    [Fact]
    public async Task A_time_breach_stops_the_investigation_with_no_result_and_a_recorded_breach()
    {
        var logger = new TraceLogger();
        using var limits = Limits(new InvestigationLimitOptions { Timeout = TimeSpan.FromMilliseconds(30) });
        var model = new ScriptedClient(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return await Reply(GoodReply);
        });

        var ex = await Assert.ThrowsAsync<InvestigationLimitExceededException>(() => Investigator(model, limits, logger).InvestigateAsync([Doc]));

        AssertBreach(ex, limits, logger, InvestigationBreach.Time);
    }

    [Fact]
    public async Task A_retry_breach_stops_the_investigation_with_no_result_and_a_recorded_breach()
    {
        var logger = new TraceLogger();
        using var limits = Limits(new InvestigationLimitOptions { MaxRetries = 0 });
        var model = new ScriptedClient(_ => throw new HttpRequestException("transient failure"));

        var ex = await Assert.ThrowsAsync<InvestigationLimitExceededException>(() => Investigator(model, limits, logger).InvestigateAsync([Doc]));

        AssertBreach(ex, limits, logger, InvestigationBreach.Retries);
    }

    private const string Doc = "SYNTHETIC EVIDENCE: Return readiness is pending reserve approval.";

    private const string GoodReply =
        """[{"text":"Return readiness is pending","witnesses":[{"documentId":"doc-1","page":1,"quote":"Return readiness is pending reserve approval."}]}]""";

    private static readonly byte[] TestKey = System.Text.Encoding.UTF8.GetBytes("synthetic-test-key-not-a-secret");

    // UTF-8 bytes of the tool names, descriptions and JSON schemas the investigator declares on every model call.
    private static int SchemaBytes() => new CaseReadTools("case-1", new CaseSource(), NoInvestigationLimits.Instance, _ => { })
        .Functions.Sum(f => System.Text.Encoding.UTF8.GetByteCount(f.Name + f.Description + f.JsonSchema.GetRawText()));

    private static BoundedInvestigator Investigator(IChatClient model, IInvestigationLimits limits, TraceLogger logger) => new(
        "case-1", new CaseSource(), ApprovedManifest(), SyntheticContentRegistry.FromCommittedFixtures(), "eastus2", "basis-v1",
        model, limits, new InvestigationTelemetry(logger, "synthetic-analyst-01", TestKey, "1.2.3", "demo"));

    private static Task<ChatResponse> Reply(string text, long? outputTokens = null) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
    {
        Usage = outputTokens is null ? null : new UsageDetails { InputTokenCount = 1, OutputTokenCount = outputTokens },
    });

    private static ChatResponse ToolCall(string name) => new(new ChatMessage(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("call-1", name) }));

    // Reads doc-1 page 1, whose text is the synthetic Doc, so the tool result passes the model-call gate.
    private static ChatResponse SourcePageCall() => new(new ChatMessage(ChatRole.Assistant, new List<AIContent>
    {
        new FunctionCallContent("call-1", CaseReadTools.SourcePageTool, new Dictionary<string, object?> { ["documentId"] = "doc-1", ["page"] = 1 }),
    }));

    private static List<string?> Values(TraceLogger logger) => logger.States.SelectMany(s => s).Select(kv => kv.Value?.ToString()).ToList();

    // A breach must release resources, leave no authoritative result, and emit one trace with outcome escalated plus the budget event.
    private static void AssertBreach(InvestigationLimitExceededException ex, InvestigationLimits limits, TraceLogger logger, InvestigationBreach breach)
    {
        Assert.Equal(breach, ex.Breach);
        Assert.Equal(breach, limits.Breach);
        Assert.True(limits.Released);
        Assert.Equal(2, logger.States.Count);
        var values = Values(logger);
        AssertBreachTraceFields(logger);
        Assert.Contains("escalated", values);
        Assert.Contains("escalate", values);
        Assert.Contains("fixture-deployment", values);
        Assert.Contains("fixture-model", values);
        Assert.Contains("basis-v1", values);
        Assert.Contains(values, v => v?.StartsWith("trace-", StringComparison.Ordinal) == true);
        Assert.Contains(values, v => v?.StartsWith("actor-", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(values, v => v is not null && v.Contains("synthetic-analyst-01", StringComparison.Ordinal));
        Assert.DoesNotContain(values, v => v is not null && v.Contains("Return readiness", StringComparison.Ordinal));
    }

    // The breach trace carries the tools and citations fields; a breach yields no claims, so it cites nothing.
    private static void AssertBreachTraceFields(TraceLogger logger)
    {
        var trace = logger.States[0];
        Assert.Contains(trace, kv => kv.Key == "investigation.tools");
        var citations = Assert.Single(trace, kv => kv.Key == "investigation.citations");
        Assert.Equal(string.Empty, citations.Value?.ToString());
    }

    [Fact]
    public async Task An_investigator_without_supplied_limits_still_enforces_the_default_limits()
    {
        var logger = new TraceLogger();
        var model = new ScriptedClient(_ => Reply(GoodReply, outputTokens: 5_000));

        var investigator = new BoundedInvestigator(
            "case-1", new CaseSource(), ApprovedManifest(), SyntheticContentRegistry.FromCommittedFixtures(), "eastus2", "basis-v1",
            model, null, new InvestigationTelemetry(logger, "synthetic-analyst-01", TestKey, "1.2.3", "demo"));

        var ex = await Assert.ThrowsAsync<InvestigationLimitExceededException>(() => investigator.InvestigateAsync([Doc]));

        Assert.Equal(InvestigationBreach.OutputTokens, ex.Breach);
        AssertBreachTraceFields(logger);
    }

    [Fact]
    public async Task The_input_guard_counts_tool_schemas_so_an_over_limit_request_is_never_sent()
    {
        var logger = new TraceLogger();
        using var limits = Limits(new InvestigationLimitOptions { MaxInputTokensPerCall = SchemaBytes() });
        var sent = 0;
        var model = new ScriptedClient(_ =>
        {
            sent++;
            return Reply(GoodReply);
        });

        var ex = await Assert.ThrowsAsync<InvestigationLimitExceededException>(() => Investigator(model, limits, logger).InvestigateAsync([Doc]));

        AssertBreach(ex, limits, logger, InvestigationBreach.InputTokens);
        Assert.Equal(0, sent);
    }

    private sealed class CaseSource : ICaseReadSource
    {
        public IReadOnlyList<string> GetInventory(string caseId) => ["doc-1"];

        public string? GetApprovedRequirements(string caseId) => null;

        public string? GetSourcePage(string caseId, string documentId, int page) => documentId == "doc-1" && page == 1 ? Doc : null;

        public IReadOnlyList<string> SearchWithinBasis(string caseId, string query) => [];

        public string? GetProcessingStatus(string caseId) => null;
    }

    /// <summary>Model stub that answers with the given response factory.</summary>
    private sealed class ScriptedClient(Func<CancellationToken, Task<ChatResponse>> respond) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("scripted");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            respond(cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static ApprovedModelManifest ApprovedManifest() => ApprovedModelManifest.Parse(
        """{"manifestVersion":"2026.10.1","approved":true,"arm":"investigation-live","deploymentName":"fixture-deployment","model":"fixture-model","apiVersion":"2025-01-01","region":"eastus2","contentFilter":"enabled"}""");

    private sealed class TraceLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<List<KeyValuePair<string, object?>>> States { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                States.Add(pairs.ToList());
            }
        }
    }

    private sealed class EmptySource : ICaseReadSource
    {
        public IReadOnlyList<string> GetInventory(string caseId) => [];

        public string? GetApprovedRequirements(string caseId) => null;

        public string? GetSourcePage(string caseId, string documentId, int page) => null;

        public IReadOnlyList<string> SearchWithinBasis(string caseId, string query) => [];

        public string? GetProcessingStatus(string caseId) => null;
    }

    /// <summary>Model stub that must never be reached when the model-call limit is zero.</summary>
    private sealed class NoReplyClient : Microsoft.Extensions.AI.IChatClient
    {
        public Microsoft.Extensions.AI.ChatClientMetadata Metadata { get; } = new("no-reply");

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("model must not be called after a breach");

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("model must not be called after a breach");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
