using AirlineDemo.Api.Investigation;

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

    private static readonly byte[] TestKey = System.Text.Encoding.UTF8.GetBytes("synthetic-test-key-not-a-secret");

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
