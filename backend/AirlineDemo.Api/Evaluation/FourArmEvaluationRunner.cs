using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Investigation;
using Microsoft.Extensions.Logging;
namespace AirlineDemo.Api.Evaluation;

public enum EvaluationArm { A, B, C, D }
public enum EvaluationExecutionMode { RulesBaseline, BoundedAgent, SingleCallWithoutToolsOrCitationReflection }
public sealed record EvaluationCase(string CaseId, string Category, string Content, string Sha256);
public sealed record EvaluationEffectiveConfiguration(string ManifestVersion, string Arm, string DeploymentName, string Model, string ApiVersion, string Region, string ContentFilter);
public sealed record EvaluationModelCall(string Prompt, string? Response, string? Error);
public sealed record EvaluationArmFailure(string Arm, string CaseId, string ErrorType);
public sealed record EvaluationDispatch(EvaluationArm Arm, string? Answer, long TokenUsage, IReadOnlyList<EvaluationModelCall> ModelCalls, EvaluationArmFailure? Failure);
public sealed record EvaluationCaseResult(string CaseId, IReadOnlyList<EvaluationDispatch> Arms);
public sealed record EvaluationBudgetStop(EvaluationArm Arm, string CaseId, long RequestedReservation, long MaximumTokens);
public sealed record FourArmEvaluationResult(string SuiteSha256, IReadOnlyDictionary<EvaluationArm, EvaluationEffectiveConfiguration?> EffectiveConfigurations,
    IReadOnlyList<EvaluationCaseResult> Cases, long TokenUsage, EvaluationBudgetStop? BudgetStop);
public sealed record EvaluationArmExecution(string Answer, long TokenUsage, int ModelCallCount, int ToolCallCount, int CitationReflectionCount);
public sealed record EvaluationArmRequest(EvaluationArm Arm, EvaluationExecutionMode Mode, EvaluationCase Case, ApprovedModelManifest? Manifest, EvaluationCallRecorder Calls);
public interface IEvaluationArmExecutor : IDisposable
{
    EvaluationArm Arm { get; }
    long MaximumTokenUsage(EvaluationCase input);
    Task<EvaluationArmExecution> ExecuteAsync(EvaluationArmRequest request, CancellationToken cancellationToken);
}

public sealed class EvaluationCallRecorder
{
    private readonly List<EvaluationModelCall> _calls = [];
    public void RecordResponse(string prompt, string response) => _calls.Add(new(prompt, response, null));
    public void RecordFailure(string prompt, string error) => _calls.Add(new(prompt, null, error));
    internal IReadOnlyList<EvaluationModelCall> Snapshot() => Array.AsReadOnly(_calls.ToArray());
}
public sealed class EvaluationInputException(string message) : Exception(message);
/// <summary>Runs the verified frozen suite in process. Injected executors are owned and disposed by this run.</summary>
public sealed class FourArmEvaluationRunner(IEnumerable<IEvaluationArmExecutor> executors, ILogger<FourArmEvaluationRunner>? logger = null)
{
    public const string FrozenV2Sha256 = "7318583ee0bdc540fdeabd85833d288843c2a1bc429b1f85c9709eac8443a049";
    private static readonly string[] Categories =
        ["baseline-complete", "missing-required-history", "ambiguous-identity", "blocked-or-unprocessed-evidence"];
    private readonly IEvaluationArmExecutor[] _executors = executors.ToArray();
    private readonly object _budgetGate = new();
    private readonly ILogger<FourArmEvaluationRunner>? _logger = logger;
    private static readonly ActivitySource ActivitySource = new("AirlineDemo.Api.Evaluation");
    public async Task<FourArmEvaluationResult> RunAsync(EvaluationRunOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            var (suiteHash, cases) = LoadFrozenSuite(options);
            var manifestB = ApprovedModelManifest.Load(options.ArmBManifestPath).RequireUsable();
            var manifestC = ApprovedModelManifest.Load(options.ArmCManifestPath).RequireUsable();
            if (_executors.Length != 4 || _executors.Select(e => e.Arm).Distinct().Count() != 4 ||
                !Enum.GetValues<EvaluationArm>().All(arm => _executors.Any(e => e.Arm == arm)))
                throw new EvaluationInputException("exactly one executor for each arm is required");
            var byArm = _executors.ToDictionary(e => e.Arm);

            var manifests = new Dictionary<EvaluationArm, ApprovedModelManifest?> { [EvaluationArm.A] = null, [EvaluationArm.B] = manifestB, [EvaluationArm.C] = manifestC, [EvaluationArm.D] = manifestB };
            var configs = manifests.ToDictionary(p => p.Key, p => p.Value is { } m
                ? new EvaluationEffectiveConfiguration(m.ManifestVersion!, m.Arm!, m.DeploymentName!, m.Model!, m.ApiVersion!, m.Region!, m.ContentFilter!)
                : null);
            var results = new List<EvaluationCaseResult>(cases.Count);
            long used = 0, reserved = 0;
            EvaluationBudgetStop? stop = null;
            foreach (var input in cases)
            {
                var dispatches = new List<EvaluationDispatch>(4);
                foreach (var arm in Enum.GetValues<EvaluationArm>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var executor = byArm[arm];
                    var maximum = executor.MaximumTokenUsage(input);
                    if (maximum < 0) throw new EvaluationInputException("executor declared a negative token reservation");
                    lock (_budgetGate)
                    {
                        if (maximum > EvaluationRunOptions.MaximumReservedTokens - used - reserved)
                        {
                            stop = new EvaluationBudgetStop(arm, input.CaseId, maximum, EvaluationRunOptions.MaximumReservedTokens);
                        }
                        else reserved += maximum;
                    }
                    if (stop is not null) break;

                    var calls = new EvaluationCallRecorder();
                    var reservationHeld = true;
                    using var activity = ActivitySource.StartActivity("evaluation.arm");
                    activity?.SetTag("evaluation.arm", arm.ToString());
                    activity?.SetTag("evaluation.case_id", input.CaseId);
                    _logger?.LogInformation("Evaluation dispatch {Arm} {CaseId}", arm, input.CaseId);
                    try
                    {
                        var execution = await executor.ExecuteAsync(
                            new EvaluationArmRequest(arm, Mode(arm), input, manifests[arm], calls), cancellationToken);
                        if (execution.TokenUsage < 0 || execution.TokenUsage > maximum)
                            throw new InvalidOperationException("executor reported usage outside its reservation");
                        if (execution.ModelCallCount != calls.Snapshot().Count)
                            throw new InvalidOperationException("executor call count does not match recorded model calls");
                        if (arm == EvaluationArm.D &&
                            (execution.ModelCallCount != 1 || execution.ToolCallCount != 0 || execution.CitationReflectionCount != 0))
                            throw new InvalidOperationException("arm D must make one call without tools or citation reflection");
                        lock (_budgetGate) { reserved -= maximum; used += execution.TokenUsage; }
                        reservationHeld = false;
                        dispatches.Add(new(arm, execution.Answer, execution.TokenUsage, calls.Snapshot(), null));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        _logger?.LogWarning("Evaluation dispatch failed for {Arm} {CaseId}: {ErrorType}",
                            arm, input.CaseId, ex.GetType().FullName ?? ex.GetType().Name);
                        dispatches.Add(new(arm, null, 0, calls.Snapshot(),
                            new EvaluationArmFailure(arm.ToString(), input.CaseId, ex.GetType().FullName ?? ex.GetType().Name)));
                    }
                    finally
                    {
                        if (reservationHeld) lock (_budgetGate) reserved -= maximum;
                    }
                }
                results.Add(new EvaluationCaseResult(input.CaseId, dispatches.AsReadOnly()));
                if (stop is not null) break;
            }
            return new FourArmEvaluationResult(suiteHash, configs, results.AsReadOnly(), used, stop);
        }
        finally
        {
            List<Exception>? disposalFailures = null;
            foreach (var executor in _executors)
            {
                try { executor.Dispose(); }
                catch (Exception ex) { (disposalFailures ??= []).Add(ex); }
            }
            if (disposalFailures is not null) throw new AggregateException("evaluation executor cleanup failed", disposalFailures);
        }
    }
    private static EvaluationExecutionMode Mode(EvaluationArm arm) => arm == EvaluationArm.A ? EvaluationExecutionMode.RulesBaseline : arm == EvaluationArm.D ? EvaluationExecutionMode.SingleCallWithoutToolsOrCitationReflection : EvaluationExecutionMode.BoundedAgent;
    private static (string Hash, List<EvaluationCase> Cases) LoadFrozenSuite(EvaluationRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(options.SuiteManifestPath));
        var root = manifest.RootElement;
        if (root.GetProperty("fixtureVersion").GetInt32() != 2 ||
            root.GetProperty("suite").GetString() != "evaluation-v2" ||
            root.GetProperty("caseCount").GetInt32() != 40)
            throw new EvaluationInputException("suite manifest is not the frozen v2 suite");
        var bytes = File.ReadAllBytes(options.CasesPath);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (root.GetProperty("casesSha256").GetString() != hash)
            throw new EvaluationInputException("suite manifest hash does not match cases.jsonl");
        var lines = new UTF8Encoding(false, true).GetString(bytes).Split('\n');
        if (lines[^1].Length == 0) lines = lines[..^1];
        if (lines.Length != 40) throw new EvaluationInputException("frozen v2 suite must contain 40 cases");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var counts = Categories.ToDictionary(c => c, _ => 0, StringComparer.Ordinal);
        var cases = new List<EvaluationCase>(40);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            var item = doc.RootElement;
            var id = item.GetProperty("caseId").GetString() ?? "";
            var category = item.GetProperty("category").GetString() ?? "";
            if (item.GetProperty("inputVersion").GetInt32() != 2 || !seen.Add(id) || !counts.ContainsKey(category))
                throw new EvaluationInputException("suite contains an invalid, duplicate, or unsupported case");
            counts[category]++;
            cases.Add(new(id, category, line, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line))).ToLowerInvariant()));
        }
        var manifestCounts = root.GetProperty("categoryCounts");
        if (Categories.Any(c => counts[c] != 10 || manifestCounts.GetProperty(c).GetInt32() != 10) ||
            manifestCounts.EnumerateObject().Count() != Categories.Length || hash != FrozenV2Sha256)
            throw new EvaluationInputException("suite is not the verified frozen v2 case mix");
        return (hash, cases);
    }
}
