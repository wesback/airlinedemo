using System.Runtime.CompilerServices;
using AirlineDemo.Api.Evaluation;
using AirlineDemo.Api.Investigation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AirlineDemo.Api.Tests;

public sealed class ModelGatewayTokenomicsTests : IDisposable
{
    private static GatewayCorrelation Correlation(string model = "model-v1") => new("B", "run-1", "package-1", "case-1", model, "suite-v1", "call-1");

    private const string Private = "https://gateway-demo.private.invalid/";
    private const string ModelConfiguration = "fixture-deployment-fixture-model-2025-01-01-swedencentral";
    private readonly string _statePath = Path.Combine(Path.GetTempPath(), $"case-state-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_statePath); } catch (IOException) { }
    }

    // The production host startup, with the network edge faked: the chat client factory, or the token credential and HTTP transport.
    private WebApplicationFactory<Program> Host(string endpoint, GatewayChatClient? fake = null, Func<Azure.Core.TokenCredential>? credential = null,
        HttpMessageHandler? handler = null, string? audience = null, ILoggerProvider? loggerProvider = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Passcode"] = "test-passcode", ["CaseState:Path"] = _statePath,
                ["ModelGateway:Endpoint"] = endpoint, ["ModelGateway:Audience"] = audience,
                ["ModelGateway:InputCostPerMillionTokens"] = "2", ["ModelGateway:OutputCostPerMillionTokens"] = "3",
            }));
            builder.ConfigureTestServices(services =>
            {
                if (fake is not null) services.AddSingleton<Func<ApprovedModelManifest, IChatClient>>(_ => _ => fake);
                if (credential is not null) services.AddSingleton(credential);
                if (handler is not null) services.AddSingleton(handler);
                if (loggerProvider is not null) services.AddSingleton<ILoggerProvider>(loggerProvider);
            });
        });

    private static ProductionEvaluationArmExecutor Executor(WebApplicationFactory<Program> host) =>
        (ProductionEvaluationArmExecutor)host.Services.GetServices<IEvaluationArmExecutor>().Single(e => e.Arm == EvaluationArm.B);

    private static EvaluationArmRequest Request(string caseId = "case-1", string padding = "", ApprovedModelManifest? manifest = null) =>
        new(EvaluationArm.B, EvaluationExecutionMode.SingleCallWithoutToolsOrCitationReflection,
            new EvaluationCase(caseId, "synthetic", "{\"inputs\":{\"status\":\"approved" + padding + "\"}}", "sha-1"),
            manifest ?? FakeApprovedManifest.Approved(), new EvaluationCallRecorder());

    [Fact]
    public async Task Production_executor_routes_to_gateway_and_records_correlation_usage_and_cost()
    {
        var fake = new GatewayChatClient();
        using var host = Host(Private, fake);
        var executor = Executor(host);

        var result = await executor.ExecuteAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(49, result.TokenUsage);
        Assert.Equal(1, fake.Calls);
        Assert.Equal(ModelConfiguration, fake.LastOptions?.ModelId);
        Assert.Equal(new Dictionary<string, object?>
        {
            ["gateway.arm"] = "B", ["gateway.run"] = "frozen-v2", ["gateway.package"] = "aircraft-return-readiness",
            ["gateway.case"] = "case-1", ["gateway.modelconfiguration"] = ModelConfiguration,
            ["gateway.suiteversion"] = FourArmEvaluationRunner.FrozenV2Sha256, ["gateway.callidentity"] = "b-sha-1",
        }, fake.LastOptions!.AdditionalProperties!.ToDictionary(p => p.Key, p => p.Value));
        var metrics = executor.LastGatewayMetrics!;
        Assert.Equal((42L, 7L, 49L, 1, 0, false, ModelConfiguration, 0.000105m, "available"),
            (metrics.InputTokens, metrics.OutputTokens, metrics.TotalTokens, metrics.ModelCallCount, metrics.RetryCount,
             metrics.Throttled, metrics.ModelConfiguration, metrics.EstimatedCost, metrics.Usage.Status));
        Assert.True(metrics.LatencyMilliseconds >= 0);

        var noUsage = new GatewayChatClient { InputTokenCount = null, OutputTokenCount = null, TotalTokenCount = null };
        using var noUsageHost = Host(Private, noUsage);
        var noUsageExecutor = Executor(noUsageHost);
        await noUsageExecutor.ExecuteAsync(Request(), TestContext.Current.CancellationToken);
        var unavailable = noUsageExecutor.LastGatewayMetrics!;
        Assert.Equal((1, "unavailable"), (noUsage.Calls, unavailable.Usage.Status));
        Assert.Null(unavailable.InputTokens);
        Assert.Null(unavailable.OutputTokens);
        Assert.Null(unavailable.TotalTokens);
        Assert.Null(unavailable.EstimatedCost);
    }

    [Theory]
    [InlineData(null, "api://airline-demo-model-gateway/.default")]
    [InlineData("api://other-gateway/", "api://other-gateway/.default")]
    public async Task Production_client_requests_the_gateway_token_scope(string? audience, string expectedScope)
    {
        var scopes = new List<string>();
        var requests = 0;
        using var host = Host(Private, credential: () => new RecordingCredential(scopes), audience: audience,
            handler: new CountingHandler(() => Interlocked.Increment(ref requests)));

        await Assert.ThrowsAnyAsync<Exception>(() => Executor(host).ExecuteAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal([expectedScope], scopes.Distinct());
        Assert.Equal(1, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task Production_executor_rejects_direct_openai_endpoint_before_dispatch()
    {
        var requests = 0;
        using var host = Host("https://gateway-demo.openai.azure.com/", handler: new CountingHandler(() => Interlocked.Increment(ref requests)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Executor(host).ExecuteAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal("model route rejected: direct Azure OpenAI endpoints are forbidden", error.Message);
        Assert.Equal(0, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task Production_executor_enforces_quota_boundary_and_denies_over_limit_result()
    {
        // Arm B allows 10000 input tokens and 2000 output tokens per call; input is estimated as the UTF-8 byte count of the joined messages.
        var probe = new GatewayChatClient();
        using var probeHost = Host(Private, probe);
        await Executor(probeHost).ExecuteAsync(Request(), TestContext.Current.CancellationToken);
        var baseBytes = System.Text.Encoding.UTF8.GetByteCount(string.Join('\n', probe.LastMessages!.Select(m => m.Text)));

        var atLimit = new GatewayChatClient { OutputTokenCount = 2000, TotalTokenCount = null };
        using var atLimitHost = Host(Private, atLimit);
        var accepted = await Executor(atLimitHost).ExecuteAsync(Request(padding: new string('x', 10_000 - baseBytes)), TestContext.Current.CancellationToken);
        Assert.Equal((1, 2042L), (atLimit.Calls, accepted.TokenUsage));

        var overInput = new GatewayChatClient();
        using var overInputHost = Host(Private, overInput);
        var overInputExecutor = Executor(overInputHost);
        var inputDenied = await Assert.ThrowsAsync<InvalidOperationException>(() => overInputExecutor
            .ExecuteAsync(Request(padding: new string('x', 10_001 - baseBytes)), TestContext.Current.CancellationToken));
        Assert.Equal("gateway quota exceeded by one unit: input-tokens", inputDenied.Message);
        Assert.Equal(0, overInput.Calls);
        Assert.Null(overInputExecutor.LastGatewayMetrics!.InputTokens);
        Assert.Null(overInputExecutor.LastGatewayMetrics!.OutputTokens);
        Assert.Null(overInputExecutor.LastGatewayMetrics!.TotalTokens);
        Assert.Equal("unavailable", overInputExecutor.LastGatewayMetrics!.Usage.Status);

        var overOutput = new GatewayChatClient { OutputTokenCount = 2001, TotalTokenCount = null };
        using var overOutputHost = Host(Private, overOutput);
        var overOutputExecutor = Executor(overOutputHost);
        var outputDenied = await Assert.ThrowsAsync<InvalidOperationException>(
            () => overOutputExecutor.ExecuteAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal("gateway quota exceeded by one unit: output-tokens", outputDenied.Message);
        Assert.Equal(1, overOutput.Calls);
        var outputMetrics = overOutputExecutor.LastGatewayMetrics!;
        Assert.Equal((42L, 2001L, 2043L, "available"),
            (outputMetrics.InputTokens, outputMetrics.OutputTokens, outputMetrics.TotalTokens, outputMetrics.Usage.Status));
    }

    [Theory]
    [InlineData("rawprompttext")]
    [InlineData("documenttext")]
    [InlineData("passcode-1")]
    [InlineData("cookie-1")]
    [InlineData("secret-1")]
    [InlineData("accesskey-1")]
    [InlineData("connectionstring-1")]
    [InlineData("modelresponse-1")]
    [InlineData("123-45-6789")]
    [InlineData("123456789")]
    [InlineData("5551234567")]
    [InlineData("4111111111111111")]
    [InlineData("user@example.test")]
    [InlineData("555-123-4567")]
    [InlineData("case-555-123-4567")]
    [InlineData("case-12345")]
    [InlineData("case-1-2")]
    [InlineData("a1234567")]
    public async Task Production_executor_rejects_sensitive_correlation_and_keeps_trace_content_private(string caseId)
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "AirlineDemo.Api.ModelGateway" or "AirlineDemo.Api.Evaluation",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        var fake = new GatewayChatClient();
        var logger = new CapturingLogger();
        using var host = Host(Private, fake, loggerProvider: new CapturingLoggerProvider(logger));
        var request = Request(caseId);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Executor(host).ExecuteAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal("correlation fields refused: Case", error.Message);
        Assert.Equal(0, fake.Calls);
        Assert.Null(fake.LastOptions);
        Assert.DoesNotContain(caseId, error.Message, StringComparison.Ordinal);
        Assert.Empty(activities);
        Assert.DoesNotContain(caseId, logger.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("case-1")]
    [InlineData("EVAL-V2-01")]
    [InlineData("DEMO-CASE-01")]
    [InlineData("cp-case-a")]
    [InlineData("case-0001")]
    public async Task Production_executor_accepts_allowed_case_identifier_as_case_field(string caseId)
    {
        var fake = new GatewayChatClient();
        using var host = Host(Private, fake);

        await Executor(host).ExecuteAsync(Request(caseId), TestContext.Current.CancellationToken);

        Assert.Equal((caseId, 1), (fake.LastOptions!.AdditionalProperties!["gateway.case"], fake.Calls));
    }

    private static string EvaluationFixture(string file, [CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", "Evaluation", "v2", file);

    [Fact]
    public async Task Evaluation_run_through_gateway_keeps_logs_and_traces_free_of_case_content()
    {
        const string marker = "RESPONSE-9c1e";
        var dir = Path.Combine(Path.GetTempPath(), "gateway-trace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = new EvaluationRunOptions(Path.Combine(dir, "manifest.json"), Path.Combine(dir, "cases.jsonl"),
                Path.Combine(dir, "b.json"), Path.Combine(dir, "c.json"));
            File.Copy(EvaluationFixture("manifest.json"), options.SuiteManifestPath);
            File.Copy(EvaluationFixture("cases.jsonl"), options.CasesPath);
            File.WriteAllText(options.ArmBManifestPath, System.Text.Json.JsonSerializer.Serialize(FakeApprovedManifest.Approved()));
            File.WriteAllText(options.ArmCManifestPath,
                System.Text.Json.JsonSerializer.Serialize(FakeApprovedManifest.Approved() with { Model = "fixture-model-c" }));

            var captured = new List<string>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name.StartsWith("AirlineDemo.Api", StringComparison.Ordinal),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (captured)
                    {
                        captured.AddRange(activity.TagObjects.Select(t => $"{t.Key}={t.Value}"));
                        captured.AddRange(activity.Events.SelectMany(e => e.Tags.Select(t => $"{t.Key}={t.Value}")));
                        captured.AddRange(activity.Baggage.Select(b => $"{b.Key}={b.Value}"));
                        captured.Add(activity.DisplayName);
                    }
                }
            };
            ActivitySource.AddActivityListener(listener);
            var fake = new GatewayChatClient { Answer = marker };
            var logger = new CapturingLogger();
            using var host = Host(Private, fake, loggerProvider: new CapturingLoggerProvider(logger));

            var result = await host.Services.GetRequiredService<FourArmEvaluationRunner>().RunAsync(options, TestContext.Current.CancellationToken);

            Assert.Equal(40, result.Cases.Count);
            Assert.Equal(120, fake.Calls);
            string traces;
            lock (captured) traces = string.Join("\n", captured);
            var everything = traces + "\n" + logger.Text;
            // The capture is live: runner logs and gateway activities were recorded.
            Assert.Contains("Evaluation dispatch B", logger.Text, StringComparison.Ordinal);
            Assert.Contains("gateway.case=EVAL-V2-01", traces, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, everything, StringComparison.Ordinal);
            foreach (var prompt in fake.Prompts.Distinct())
            {
                Assert.DoesNotContain(prompt, everything, StringComparison.Ordinal);
                Assert.DoesNotContain(prompt.Split('\n')[^1], everything, StringComparison.Ordinal);
            }
            // The only case-bearing values are the seven named correlation tags.
            Assert.All(captured.Where(c => c.Contains("EVAL-V2-", StringComparison.Ordinal)), c => Assert.StartsWith("gateway.", c));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Production_executor_replay_returns_original_result_without_second_dispatch()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "AirlineDemo.Api.ModelGateway",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        var fake = new GatewayChatClient();
        using var host = Host(Private, fake);
        var executor = Executor(host);

        var first = await executor.ExecuteAsync(Request(), TestContext.Current.CancellationToken);
        var usageBeforeReplay = (executor.LastGatewayMetrics!.InputTokens, executor.LastGatewayMetrics.OutputTokens,
            executor.LastGatewayMetrics.TotalTokens, executor.LastGatewayMetrics.ModelCallCount,
            executor.LastGatewayMetrics.EstimatedCost);
        fake.InputTokenCount = 999; fake.OutputTokenCount = 999;
        var replay = await executor.ExecuteAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(usageBeforeReplay, (executor.LastGatewayMetrics!.InputTokens, executor.LastGatewayMetrics.OutputTokens,
            executor.LastGatewayMetrics.TotalTokens, executor.LastGatewayMetrics.ModelCallCount,
            executor.LastGatewayMetrics.EstimatedCost));
        Assert.Equal(first, replay);
        Assert.Equal(49, replay.TokenUsage);
        Assert.Equal(1, fake.Calls);
        Assert.Equal(first.TokenUsage, replay.TokenUsage);
        Assert.Equal((42L, 7L, 49L, "available", 0.000105m),
            (executor.LastGatewayMetrics!.InputTokens, executor.LastGatewayMetrics!.OutputTokens,
             executor.LastGatewayMetrics!.TotalTokens, executor.LastGatewayMetrics!.Usage.Status,
             executor.LastGatewayMetrics!.EstimatedCost));
        Assert.Single(activities);

        var otherModel = ApprovedModelManifest.Parse("""{"manifestVersion":"2026.10.1","approved":true,"arm":"investigation-live","deploymentName":"other-deployment","model":"fixture-model","apiVersion":"2025-01-01","region":"swedencentral","contentFilter":"enabled"}""");
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(Request(manifest: otherModel), TestContext.Current.CancellationToken));
        Assert.Equal("gateway replay rejected: model configuration changed", rejected.Message);
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
    public async Task Quota_boundary_and_over_limit_calls_report_exact_usage_and_reject_result()
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

        var multiFake = new GatewayChatClient { InputTokenCount = 80, OutputTokenCount = 20, TotalTokenCount = 100 };
        var multi = new GatewayGovernedChatClient(multiFake, Correlation(),
            new GatewayQuota(100, 50, 120, 2, 3, 1, TimeSpan.FromSeconds(30)));
        await multi.GetResponseAsync([new ChatMessage(ChatRole.User, "first")]);
        multiFake.InputTokenCount = 30; multiFake.OutputTokenCount = 30; multiFake.TotalTokenCount = 60;
        await Assert.ThrowsAsync<InvalidOperationException>(() => multi.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "second")], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal((110L, 50L, 160L, 2, "available"),
            (multi.Metrics!.InputTokens, multi.Metrics.OutputTokens, multi.Metrics.TotalTokens,
             multi.Metrics.ModelCallCount, multi.Metrics.Usage.Status));

        var modelCalls = new GatewayQuotaGovernor(new GatewayQuota(100, 50, 120, 1, 3, 1, TimeSpan.FromSeconds(30)));
        Assert.Null(modelCalls.BeforeCall(100));
        Assert.Equal("gateway quota exceeded by one unit: model-calls", modelCalls.BeforeCall(100));
        var toolCalls = new GatewayQuotaGovernor(new GatewayQuota(100, 50, 120, 2, 1, 1, TimeSpan.FromSeconds(30)));
        Assert.Equal("gateway quota exceeded by one unit: tool-calls", toolCalls.BeforeCall(100, toolCalls: 2));
        var retries = new GatewayQuotaGovernor(new GatewayQuota(100, 50, 120, 2, 3, 1, TimeSpan.FromSeconds(30)));
        Assert.Equal("gateway quota exceeded by one unit: retries", retries.BeforeCall(100, retries: 2));
        long ticks = 0;
        var elapsed = new GatewayQuotaGovernor(new GatewayQuota(100, 50, 120, 2, 3, 1, TimeSpan.FromMilliseconds(10)), () => ticks);
        ticks = 11;
        Assert.Equal("gateway quota exceeded by one unit: elapsed-time", elapsed.BeforeCall(100));
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
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }
        public string Answer { get; set; } = "ok";
        private readonly List<string> _prompts = [];
        public IReadOnlyList<string> Prompts { get { lock (_prompts) return _prompts.ToList(); } }
        public ChatClientMetadata Metadata { get; } = new("gateway-test");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastOptions = options;
            LastMessages = messages.ToList();
            lock (_prompts) _prompts.Add(string.Join("\n", LastMessages.Select(m => m.Text)));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer))
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

    private sealed class RecordingCredential(List<string> scopes) : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext context, CancellationToken cancellationToken)
        {
            lock (scopes) scopes.AddRange(context.Scopes);
            return new Azure.Core.AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        }
        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(context, cancellationToken));
    }

    private sealed class CapturingLoggerProvider(CapturingLogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<string> _messages = [];
        public string Text { get { lock (_messages) return string.Join("\n", _messages); } }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_messages) _messages.Add(formatter(state, exception));
        }
    }

    private sealed class CountingHandler(Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":{\"code\":\"fake\",\"message\":\"fake transport\"}}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    public sealed class MutableResult { public string Value { get; set; } = ""; }
}
