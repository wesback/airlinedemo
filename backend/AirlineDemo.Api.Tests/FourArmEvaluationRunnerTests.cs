using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using AirlineDemo.Api.Evaluation;
using AirlineDemo.Api.Investigation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AirlineDemo.Api.Tests;
public sealed class FourArmEvaluationRunnerTests
{
    private static string Fixture(string file, [CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", "Evaluation", "v2", file);
    private sealed class Inputs : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "four-arm-" + Guid.NewGuid().ToString("N"));
        public EvaluationRunOptions Options => new(Path.Combine(Dir, "manifest.json"), Path.Combine(Dir, "cases.jsonl"), Path.Combine(Dir, "b.json"), Path.Combine(Dir, "c.json"));
        public Inputs(string? cases = null, JsonObject? suiteManifest = null, ApprovedModelManifest? b = null, ApprovedModelManifest? c = null)
        {
            Directory.CreateDirectory(Dir);
            File.Copy(Fixture("manifest.json"), Options.SuiteManifestPath);
            File.WriteAllText(Options.CasesPath, cases ?? File.ReadAllText(Fixture("cases.jsonl")));
            if (suiteManifest is not null) File.WriteAllText(Options.SuiteManifestPath, suiteManifest.ToJsonString());
            File.WriteAllText(Options.ArmBManifestPath, JsonSerializer.Serialize(b ?? FakeApprovedManifest.Approved()));
            File.WriteAllText(Options.ArmCManifestPath, JsonSerializer.Serialize(c ?? FakeApprovedManifest.Approved() with { Model = "fixture-model-c" }));
        }
        public void Dispose() => Directory.Delete(Dir, true);
        public void WriteSuite(string cases, bool updateHash)
        {
            File.WriteAllText(Options.CasesPath, cases);
            if (!updateHash) return;
            var manifest = JsonNode.Parse(File.ReadAllText(Options.SuiteManifestPath))!.AsObject();
            manifest["casesSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Options.CasesPath))).ToLowerInvariant();
            File.WriteAllText(Options.SuiteManifestPath, manifest.ToJsonString());
        }
    }

    private sealed class FakeExecutor(EvaluationArm arm) : IEvaluationArmExecutor
    {
        public List<EvaluationArmRequest> Requests { get; } = [];
        public Func<EvaluationCase, long> Reservation { get; set; } = _ => 2;
        public Func<EvaluationCase, long> Usage { get; set; } = _ => 1;
        public Func<EvaluationArmRequest, bool>? Fail { get; set; }
        public Func<EvaluationArmRequest, int>? ForbiddenAttempts { get; set; }
        public bool InvalidDCounts { get; set; }
        public string Prompt { get; set; } = "prompt-canary";
        public string Response { get; set; } = "response-canary";
        public string FailureText { get; set; } = "failure-canary";
        public bool Disposed { get; private set; }
        public (int Calls, int Tools, int Reflections) Counts =>
            (InvalidDCounts && arm == EvaluationArm.D ? 2 : arm == EvaluationArm.A ? 0 : 1, 0, 0);
        public EvaluationArm Arm => arm;
        public long MaximumTokenUsage(EvaluationCase input) => Reservation(input);
        public Task<EvaluationArmExecution> ExecuteAsync(EvaluationArmRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            for (var attempt = ForbiddenAttempts?.Invoke(request) ?? 0; attempt > 0; attempt--)
                request.Calls.RecordForbiddenActionAttempt();
            if (Fail?.Invoke(request) == true) { request.Calls.RecordFailure(Prompt, FailureText); throw new InvalidOperationException("failure canary"); }
            if (Counts.Calls > 0) request.Calls.RecordResponse(Prompt, Response);
            return Task.FromResult(new EvaluationArmExecution("answer-" + request.Case.CaseId, Usage(request.Case),
                Counts.Calls, Counts.Tools, Counts.Reflections));
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class FixedStepTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long GetTimestamp() => Interlocked.Add(ref _timestamp, TimestampFrequency * 7 / 1000);
    }
    private sealed class BoundedAgentChatClient : IChatClient
    {
        public string? FunctionCallName { get; set; }
        public int Calls { get; private set; }
        public int ToolCalls { get; private set; }
        public int ToolResults { get; private set; }
        public int AgentFinalCalls { get; private set; }
        public int DirectCalls { get; private set; }
        public List<string[]> DeclaredToolSets { get; } = [];
        public ChatClientMetadata Metadata { get; } = new("bounded-agent-test");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var request = messages.ToList();
            if (request.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any())
            {
                ToolResults++;
                AgentFinalCalls++;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "bounded-answer")));
            }

            if (options?.Tools is { Count: > 0 } tools)
            {
                DeclaredToolSets.Add(tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray());
                ToolCalls++;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("evaluation-tool-call", FunctionCallName ?? tools[0].Name, new Dictionary<string, object?>())])));
            }

            DirectCalls++;
            if (FunctionCallName is { } functionName)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("evaluation-tool-call", functionName, new Dictionary<string, object?>())])));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "production-answer")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    private static FakeExecutor[] Fakes() => Enum.GetValues<EvaluationArm>().Select(a => new FakeExecutor(a)).ToArray();
    private static FourArmEvaluationRunner Runner(FakeExecutor[] fakes) => new(fakes);
    private static string Cases() => File.ReadAllText(Fixture("cases.jsonl"));
    private static string[] Args(EvaluationRunOptions o) => ["--suite-manifest", o.SuiteManifestPath, "--cases", o.CasesPath, "--arm-b-manifest", o.ArmBManifestPath, "--arm-c-manifest", o.ArmCManifestPath];
    [Fact]
    public async Task All_arms_receive_identical_frozen_case_inputs()
    {
        using var files = new Inputs();
        var fakes = Fakes();
        var result = await Runner(fakes).RunAsync(files.Options, TestContext.Current.CancellationToken);
        Assert.Equal(FourArmEvaluationRunner.FrozenV2Sha256, result.SuiteSha256);
        Assert.Equal(40, result.Cases.Count);
        Assert.Equal(40, result.Cases.Select(c => c.CaseId).Distinct().Count());
        Assert.Equal(160, fakes.Sum(f => f.Requests.Count));
        Assert.All(fakes, f => Assert.Equal(40, f.Requests.Select(r => r.Case.CaseId).Distinct().Count()));
        Assert.All(result.Cases, c => Assert.Equal(Enum.GetValues<EvaluationArm>(), c.Arms.Select(a => a.Arm)));
        foreach (var group in fakes.SelectMany(f => f.Requests).GroupBy(r => r.Case.CaseId))
        {
            Assert.Equal(4, group.Count());
            Assert.All(group, r => Assert.Same(group.First().Case, r.Case));
            Assert.All(group, r => Assert.Equal(group.First().Case.Sha256, r.Case.Sha256));
            Assert.Equal(group.First().Case.Sha256,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(group.First().Case.Content))).ToLowerInvariant());
        }
        var original = Cases();
        var changed = original.Replace("\"inputVersion\":2", "\"inputVersion\":3", StringComparison.Ordinal);
        files.WriteSuite(changed, updateHash: false);
        var changedFakes = Fakes();
        await Assert.ThrowsAnyAsync<Exception>(() => Runner(changedFakes).RunAsync(files.Options, TestContext.Current.CancellationToken));
        Assert.Equal(0, changedFakes.Sum(f => f.Requests.Count));
        var duplicate = original.Replace("\"caseId\":\"EVAL-V2-02\"", "\"caseId\":\"EVAL-V2-01\"", StringComparison.Ordinal);
        files.WriteSuite(duplicate, updateHash: true);
        var duplicateFakes = Fakes();
        await Assert.ThrowsAsync<EvaluationInputException>(() => Runner(duplicateFakes).RunAsync(files.Options, TestContext.Current.CancellationToken));
        Assert.Equal(0, duplicateFakes.Sum(f => f.Requests.Count));
    }

    [Fact]
    public async Task Arm_D_matches_B_configuration_and_is_single_call()
    {
        using var files = new Inputs();
        var fakes = Fakes();
        var result = await Runner(fakes).RunAsync(files.Options, TestContext.Current.CancellationToken);
        var b = fakes[(int)EvaluationArm.B].Requests[0].Manifest!;
        var d = fakes[(int)EvaluationArm.D].Requests[0].Manifest!;
        Assert.Same(b, d);
        Assert.All(fakes[(int)EvaluationArm.B].Requests.Zip(fakes[(int)EvaluationArm.D].Requests),
            pair => Assert.Same(pair.First.Manifest, pair.Second.Manifest));
        Assert.Equal(EvaluationExecutionMode.SingleCallWithoutToolsOrCitationReflection, fakes[3].Requests[0].Mode);
        Assert.All(fakes[1].Requests, r => Assert.Equal(EvaluationExecutionMode.BoundedAgent, r.Mode));
        Assert.All(fakes[2].Requests, r => Assert.Equal(EvaluationExecutionMode.BoundedAgent, r.Mode));
        Assert.All(fakes[0].Requests, r => Assert.Equal(EvaluationExecutionMode.RulesBaseline, r.Mode));
        Assert.Equal(new EvaluationEffectiveConfiguration(b.ManifestVersion!, b.Arm!, b.DeploymentName!, b.Model!, b.ModelVersion!, b.ApiVersion!, b.Region!, b.ContentFilter!),
            result.EffectiveConfigurations[EvaluationArm.D]);
        Assert.Equal(40, fakes[3].Requests.Count);
        Assert.Equal((1, 0, 0), fakes[3].Counts);

        using var invalidFiles = new Inputs();
        var invalidD = Fakes();
        invalidD[3].InvalidDCounts = true;
        var rejected = await Runner(invalidD).RunAsync(invalidFiles.Options, TestContext.Current.CancellationToken);
        Assert.All(rejected.Cases, c => Assert.Null(c.Arms.Single(a => a.Arm == EvaluationArm.D).Answer));
        Assert.All(rejected.Cases, c => Assert.Equal(nameof(InvalidOperationException), c.Arms.Single(a => a.Arm == EvaluationArm.D).Failure!.ErrorType.Split('.').Last()));
    }
    [Fact]
    public async Task Arm_failure_is_reported_without_fabricating_an_outcome()
    {
        using var files = new Inputs();
        var fakes = Fakes();
        fakes[1].Fail = r => r.Case.CaseId == "EVAL-V2-01";
        var result = await Runner(fakes).RunAsync(files.Options, TestContext.Current.CancellationToken);
        var failed = result.Cases[0].Arms[1];
        Assert.Null(failed.Answer);
        Assert.Equal(new EvaluationArmFailure("B", "EVAL-V2-01", typeof(InvalidOperationException).FullName!), failed.Failure);
        Assert.Equal(new EvaluationModelCall(fakes[1].Prompt, null, fakes[1].FailureText), Assert.Single(failed.ModelCalls));
        Assert.Equal(160, fakes.Sum(f => f.Requests.Count));
        Assert.Contains(fakes[2].Requests, r => r.Case.CaseId == "EVAL-V2-01");
        Assert.All(fakes, f => Assert.True(f.Disposed));
    }

    [Fact]
    public async Task Dispatch_records_monotonic_elapsed_time_and_forbidden_attempts()
    {
        using var files = new Inputs();
        var fakes = Fakes();
        fakes[0].Fail = request => request.Case.CaseId == "EVAL-V2-01";
        fakes[1].Fail = request => request.Case.CaseId == "EVAL-V2-01";
        fakes[1].ForbiddenAttempts = request => request.Case.CaseId == "EVAL-V2-01" ? 1 : 0;
        var result = await new FourArmEvaluationRunner(fakes, timeProvider: new FixedStepTimeProvider())
            .RunAsync(files.Options, TestContext.Current.CancellationToken);

        var ordinaryFailure = result.Cases[0].Arms[0];
        var rejectedCall = result.Cases[0].Arms[1];
        var success = result.Cases[0].Arms[2];
        Assert.Equal(7, ordinaryFailure.ElapsedMilliseconds);
        Assert.Equal(0, ordinaryFailure.ForbiddenActionAttempts);
        Assert.Equal(7, rejectedCall.ElapsedMilliseconds);
        Assert.Equal(1, rejectedCall.ForbiddenActionAttempts);
        Assert.NotNull(rejectedCall.Failure);
        Assert.Equal(7, success.ElapsedMilliseconds);
        Assert.Equal(0, success.ForbiddenActionAttempts);
    }

    [Fact]
    public async Task Forbidden_function_attempts_are_counted_before_rejection()
    {
        using var files = new Inputs();
        var chat = new BoundedAgentChatClient { FunctionCallName = "submit_mock_request" };
        var executors = Enum.GetValues<EvaluationArm>()
            .Select(arm => new ProductionEvaluationArmExecutor(arm, _ => chat))
            .ToArray();
        var result = await new FourArmEvaluationRunner(executors)
            .RunAsync(files.Options, TestContext.Current.CancellationToken);

        Assert.All(result.Cases, item =>
        {
            Assert.Equal(0, item.Arms[0].ForbiddenActionAttempts);
            foreach (var dispatch in item.Arms.Skip(1))
            {
                Assert.Equal(1, dispatch.ForbiddenActionAttempts);
                Assert.NotNull(dispatch.Failure);
            }
        });
    }

    [Fact]
    public async Task Global_token_budget_stops_before_the_over_limit_call()
    {
        using var files = new Inputs();
        var fakes = Fakes();
        fakes[0].Reservation = _ => 3_000_000;
        fakes[0].Usage = _ => 3_000_000;
        fakes[1].Reservation = _ => 1;
        var result = await Runner(fakes).RunAsync(files.Options, TestContext.Current.CancellationToken);
        Assert.Single(fakes[0].Requests);
        Assert.Single(result.Cases);
        Assert.Equal(3_000_000, result.TokenUsage);
        Assert.Equal(new EvaluationBudgetStop(EvaluationArm.B, "EVAL-V2-01", 1, 3_000_000), result.BudgetStop);
        Assert.All(fakes, f => Assert.True(f.Disposed));
        using var overFiles = new Inputs();
        var over = Fakes();
        over[0].Reservation = _ => 3_000_001;
        var stopped = await Runner(over).RunAsync(overFiles.Options, TestContext.Current.CancellationToken);
        Assert.Empty(over[0].Requests);
        Assert.Empty(stopped.Cases[0].Arms);
        Assert.Equal(0, stopped.TokenUsage);
        Assert.All(over, f => Assert.True(f.Disposed));
        using var reconcileFiles = new Inputs();
        var reconcile = Fakes();
        var reconciled = await Runner(reconcile).RunAsync(reconcileFiles.Options, TestContext.Current.CancellationToken);
        Assert.Equal(160, reconciled.TokenUsage);
        Assert.Equal(160, reconcile.Sum(f => f.Requests.Count));
    }

    [Fact]
    public async Task Invalid_suite_or_arm_configuration_is_rejected_before_execution()
    {
        var cases = Cases();
        var manifest = JsonNode.Parse(File.ReadAllText(Fixture("manifest.json")))!.AsObject();
        var badCategoryCounts = (JsonObject)manifest.DeepClone();
        badCategoryCounts["categoryCounts"]!["baseline-complete"] = 9;
        var invalids = new (string Cases, JsonObject Manifest, ApprovedModelManifest? B, ApprovedModelManifest? C, bool UpdateHash)[]
        {
            (cases, (JsonObject)manifest.DeepClone(), FakeApprovedManifest.Approved() with { Approved = false }, null, false),
            (cases, (JsonObject)manifest.DeepClone(), null, FakeApprovedManifest.Approved() with { Approved = false }, false),
            (cases, Set(manifest, "fixtureVersion", 1), null, null, false),
            (cases + " ", (JsonObject)manifest.DeepClone(), null, null, false),
            (cases, Set(manifest, "caseCount", 39), null, null, false),
            (string.Join('\n', cases.Split('\n').SkipLast(2)) + "\n", (JsonObject)manifest.DeepClone(), null, null, true),
            (cases, badCategoryCounts, null, null, false),
            (cases.Replace("\"caseId\":\"EVAL-V2-02\"", "\"caseId\":\"EVAL-V2-01\"", StringComparison.Ordinal), (JsonObject)manifest.DeepClone(), null, null, true),
            (cases.Replace("\"category\":\"baseline-complete\"", "\"category\":\"missing-required-history\"", StringComparison.Ordinal), (JsonObject)manifest.DeepClone(), null, null, true),
        };
        foreach (var (content, suite, b, c, updateHash) in invalids)
        {
            using var files = new Inputs(content, suite, b, c);
            if (updateHash) files.WriteSuite(content, updateHash: true);
            var fakes = Fakes();
            await Assert.ThrowsAnyAsync<Exception>(() => Runner(fakes).RunAsync(files.Options, TestContext.Current.CancellationToken));
            Assert.Equal(0, fakes.Sum(f => f.Requests.Count));
        }
    }

    [Fact]
    public async Task Result_records_keep_prompts_and_responses_out_of_telemetry()
    {
        const string prompt = "unique-prompt-canary";
        const string response = "unique-response-canary";
        const string errorText = "unique-failure-canary";
        using var files = new Inputs();
        var fakes = Fakes();
        fakes[1].Prompt = prompt;
        fakes[1].FailureText = errorText;
        fakes[3].Prompt = prompt;
        fakes[3].Response = response;
        fakes[1].Fail = r => r.Case.CaseId == "EVAL-V2-01";
        var provider = new CapturingProvider();
        using var logger = LoggerFactory.Create(b => b.AddProvider(provider));
        var tags = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => tags.Add(string.Join("|", activity.TagObjects.Select(tag => tag.Value?.ToString()))),
        };
        ActivitySource.AddActivityListener(listener);
        var result = await new FourArmEvaluationRunner(fakes, logger.CreateLogger<FourArmEvaluationRunner>())
            .RunAsync(files.Options, TestContext.Current.CancellationToken);
        var failed = result.Cases[0].Arms[1];
        Assert.Equal(prompt, failed.ModelCalls[0].Prompt);
        Assert.Equal(errorText, failed.ModelCalls[0].Error);
        var successful = result.Cases[0].Arms.Single(a => a.Arm == EvaluationArm.D).ModelCalls[0];
        Assert.Equal(prompt, successful.Prompt);
        Assert.Equal(response, successful.Response);
        Assert.Equal(FourArmEvaluationRunner.FrozenV2Sha256, result.SuiteSha256);
        Assert.DoesNotContain(prompt, provider.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(response, provider.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(errorText, provider.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(tags, tag => tag.Contains(prompt, StringComparison.Ordinal) ||
            tag.Contains(response, StringComparison.Ordinal) || tag.Contains(errorText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Command_dispatch_uses_the_testable_runner_seam()
    {
        using var files = new Inputs();
        var fakes = Fakes();
        var runner = Runner(fakes);
        var output = new StringWriter();
        var error = new StringWriter();
        var args = Args(files.Options);
        Assert.Equal(0, await Program.RunEvaluationCommandAsync(args, runner, output, error, TestContext.Current.CancellationToken));
        Assert.Equal(160, fakes.Sum(f => f.Requests.Count));
        Assert.Contains(FourArmEvaluationRunner.FrozenV2Sha256, output.ToString());
        Assert.Empty(error.ToString());

        var invalid = Fakes();
        Assert.Equal(2, await Program.RunEvaluationCommandAsync(args[..^2], Runner(invalid), new StringWriter(), error, TestContext.Current.CancellationToken));
        Assert.Equal(0, invalid.Sum(f => f.Requests.Count));
    }

    [Fact]
    public async Task Production_command_resolves_and_dispatches_registered_arm_executors()
    {
        using var files = new Inputs();
        var chat = new BoundedAgentChatClient();
        var services = new ServiceCollection();
        services.AddSingleton<Func<ApprovedModelManifest, IChatClient>>(_ => _ => chat);
        Program.RegisterProductionEvaluationExecutors(services);
        services.AddTransient<FourArmEvaluationRunner>();
        using var provider = services.BuildServiceProvider();
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(0, await Program.RunEvaluationCommandAsync(
            Args(files.Options), provider.GetRequiredService<FourArmEvaluationRunner>(),
            output, error, TestContext.Current.CancellationToken));
        Assert.Equal(200, chat.Calls);
        Assert.Equal(80, chat.ToolCalls);
        Assert.Equal(80, chat.ToolResults);
        Assert.Equal(80, chat.AgentFinalCalls);
        Assert.Equal(40, chat.DirectCalls);
        var expectedTools = new[]
        {
            "evaluation_get_case_overview",
            "evaluation_get_document_page",
            "evaluation_list_case_documents"
        };
        Assert.Equal(80, chat.DeclaredToolSets.Count);
        Assert.All(chat.DeclaredToolSets, tools => Assert.Equal(expectedTools, tools));
        Assert.Empty(error.ToString());
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal(FourArmEvaluationRunner.FrozenV2Sha256, result.RootElement.GetProperty("SuiteSha256").GetString());
        Assert.Equal(40, result.RootElement.GetProperty("Cases").GetArrayLength());
        Assert.Equal("bounded-answer", result.RootElement.GetProperty("Cases")[0].GetProperty("Arms")[1]
            .GetProperty("Answer").GetString());
        Assert.Equal(2, result.RootElement.GetProperty("Cases")[0].GetProperty("Arms")[1]
            .GetProperty("ModelCalls").GetArrayLength());
        Assert.Equal("bounded-answer", result.RootElement.GetProperty("Cases")[0].GetProperty("Arms")[2]
            .GetProperty("Answer").GetString());
        Assert.Equal(2, result.RootElement.GetProperty("Cases")[0].GetProperty("Arms")[2]
            .GetProperty("ModelCalls").GetArrayLength());
        Assert.Equal("production-answer", result.RootElement.GetProperty("Cases")[0].GetProperty("Arms")[3]
            .GetProperty("ModelCalls")[0].GetProperty("Response").GetString());
        Assert.Equal(1, result.RootElement.GetProperty("Cases")[0].GetProperty("Arms")[3]
            .GetProperty("ModelCalls").GetArrayLength());
    }

    [Fact]
    public async Task Evaluation_answers_are_not_ingested_into_case_state()
    {
        using var files = new Inputs();
        var fakes = Fakes();
        var options = files.Options;
        var output = new StringWriter();
        var error = new StringWriter();
        var args = Args(options);
        using var provider = new ServiceCollection().AddSingleton(new CountingStateStore()).AddSingleton(Runner(fakes)).BuildServiceProvider();
        Assert.Equal(0, await Program.RunEvaluationCommandAsync(args, provider.GetRequiredService<FourArmEvaluationRunner>(),
            output, error, TestContext.Current.CancellationToken));
        var store = provider.GetRequiredService<CountingStateStore>();
        Assert.Equal(new int[4], store.Writes);
        Assert.Equal(160, fakes.Sum(f => f.Requests.Count));
        Assert.Contains("\"Answer\":\"answer-EVAL-V2-01\"", output.ToString());
    }

    private static JsonObject Set(JsonObject source, string key, JsonNode? value)
    { source = (JsonObject)source.DeepClone(); source[key] = value?.DeepClone(); return source; }
    private sealed class CountingStateStore { public int[] Writes { get; } = new int[4]; }
    private sealed class CapturingProvider : ILoggerProvider
    {
        public string Text { get; private set; } = "";
        public ILogger CreateLogger(string categoryName) => new Capture(this);
        public void Dispose() { }
        private sealed class Capture(CapturingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => owner.Text += formatter(state, exception);
        }
    }
}
