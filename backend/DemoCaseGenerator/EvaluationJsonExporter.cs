using System.Text.Json;
using System.Text.RegularExpressions;
using AirlineDemo.Api.Evaluation;

namespace DemoCaseGenerator;

public static class EvaluationJsonExporter
{
    public const int SchemaVersion = 1;
    public const int FrozenSuiteVersion = 2;
    private static readonly string[] ArmIds = ["A", "B", "C", "D"];
    private static readonly string[] SafeDeploymentNames = ["demo-a", "demo-b", "demo-c", "demo-d"];
    private static readonly string[] CaseCategories =
        ["baseline-complete", "missing-required-history", "ambiguous-identity", "blocked-or-unprocessed-evidence"];
    private static readonly string[] ExpectedStates = ["request", "review", "blocked"];
    private static readonly IReadOnlyDictionary<string, string> MetricUnits =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gapDetectionPrecision"] = "ratio",
            ["gapDetectionRecall"] = "ratio",
            ["ambiguityRoutingRecall"] = "ratio",
            ["citationValidityRate"] = "ratio",
            ["blockedVsMissingCorrectness"] = "ratio",
            ["forbiddenActionRate"] = "ratio",
            ["packageLatencyP50Ms"] = "ms",
            ["packageLatencyP95Ms"] = "ms"
        };
    private static readonly IReadOnlyList<string> EvidenceLabels = EvaluationScorer.FigureLabels;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void Write(Stream output, FourArmEvaluationResult run, EvaluationScoringReport scoring,
        EvaluationCostReport costs, int schemaVersion = SchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(scoring);
        ArgumentNullException.ThrowIfNull(costs);
        if (!output.CanWrite) throw new ArgumentException("output stream must be writable", nameof(output));
        if (schemaVersion != SchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "only schema version 1 is supported");
        if (!StringComparer.Ordinal.Equals(run.SuiteSha256, scoring.SuiteSha256) ||
            !Regex.IsMatch(run.SuiteSha256, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("runner and scorer must identify the same frozen suite");
        if (run.BudgetStop is { } stop
            ? scoring.BudgetStop is not { } scoredStop || scoredStop.Arm != stop.Arm.ToString() || scoredStop.CaseId != stop.CaseId
            : scoring.BudgetStop is not null)
            throw new ArgumentException("runner and scorer must agree on the budget stop");
        ValidateCaseIds(run, scoring);
        ValidateNoModelContent(run);

        var scores = scoring.Arms.ToDictionary(score => ParseArm(score.Arm));
        if (scores.Count != ArmIds.Length) throw new ArgumentException("exactly one score for each arm is required", nameof(scoring));
        if (scoring.SelectedArmId is not null && !ArmIds.Contains(scoring.SelectedArmId, StringComparer.Ordinal))
            throw new ArgumentException("selected arm must be A, B, C, D, or null", nameof(scoring));
        var costArms = costs.Arms.ToDictionary(cost => ParseArm(cost.Arm));
        if (costArms.Count != ArmIds.Length) throw new ArgumentException("exactly one cost report for each arm is required", nameof(costs));

        var incomplete = run.BudgetStop is not null;
        var arms = ArmIds.Select(id =>
        {
            var arm = Enum.Parse<EvaluationArm>(id);
            run.EffectiveConfigurations.TryGetValue(arm, out var config);
            var score = scores[arm];
            if (score.Metrics.Count != MetricUnits.Count)
                throw new ArgumentException("each arm must contain every scorer metric", nameof(scoring));
            var metrics = score.Metrics.OrderBy(metric => metric.Key, StringComparer.Ordinal)
                .Select(metric =>
                {
                    if (!MetricUnits.TryGetValue(metric.Key, out var unit))
                        throw new ArgumentException($"unsupported evaluation metric: {metric.Key}", nameof(scoring));
                    var figure = metric.Value.Value;
                    ValidateLabel(figure.Label);
                    return new MetricDto(metric.Key, figure.Value, metric.Value.Denominator.Value, unit, figure.Label);
                }).ToArray();
            return new ArmDto(id, Deployment(config), config is null ? null : ExecutionMode(arm),
                run.Cases.Sum(item => item.Arms.Count(dispatch => dispatch.Arm == arm && dispatch.Failure is null)),
                metrics, !incomplete && score.Qualified);
        }).ToArray();
        var figures = new List<CostFigureDto>(9);
        foreach (var id in ArmIds)
        {
            var cost = costArms[Enum.Parse<EvaluationArm>(id)];
            AddCost(figures, $"{id}.costPerCompletedCaseEur", cost.CostPerCompletedCaseEur, "EUR/case");
            AddCost(figures, $"{id}.costPerProcessedPageEur", cost.CostPerProcessedPageEur, "EUR/page");
        }
        AddCost(figures, "breakEvenDaysPerTransition", costs.BreakEvenDaysPerTransition, "days/transition");
        var document = new ReportDto(SchemaVersion, FrozenSuiteVersion, run.SuiteSha256, incomplete,
            incomplete ? "token-budget-exhausted" : null, scoring.SelectedArmId,
            !incomplete && scoring.SelectedArmId is not null && scoring.SelectedArmQualified, arms,
            figures.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        ValidateJson(bytes);
        output.Write(bytes);
    }

    public static void ValidateJson(ReadOnlySpan<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json.ToArray());
        var root = document.RootElement;
        RequireProperties(root, "schemaVersion", "suiteVersion", "suiteSha256", "incomplete", "stopReason",
            "selectedArmId", "selectedArmQualified", "arms", "costFigures");
        if (root.GetProperty("schemaVersion").GetInt32() != SchemaVersion)
            throw new JsonException("only schema version 1 is supported");
        foreach (var arm in root.GetProperty("arms").EnumerateArray())
        {
            RequireProperties(arm, "armId", "deployment", "mode", "completedCases", "metrics", "passed");
            foreach (var metric in arm.GetProperty("metrics").EnumerateArray())
                RequireProperties(metric, "name", "value", "eligibleCount", "unit", "evidenceLabel");
        }
        foreach (var cost in root.GetProperty("costFigures").EnumerateArray())
            RequireProperties(cost, "name", "value", "unit", "evidenceLabel");
    }

    private static string? Deployment(EvaluationEffectiveConfiguration? config)
    {
        if (config is null) return null;
        var name = config.DeploymentName;
        if (!SafeDeploymentNames.Contains(name, StringComparer.Ordinal))
            throw new ArgumentException("deployment name is not an approved non-identifying alias");
        return name;
    }

    private static void ValidateNoModelContent(FourArmEvaluationResult run)
    {
        if (run.Cases.Any(item => item.Arms.Any(dispatch =>
                !string.IsNullOrEmpty(dispatch.Answer) ||
                dispatch.ModelCalls.Any(call =>
                    !string.IsNullOrEmpty(call.Prompt) ||
                    !string.IsNullOrEmpty(call.Response) ||
                    !string.IsNullOrEmpty(call.Error))))
        )
            throw new ArgumentException("evaluation results containing model content cannot be exported");
    }

    private static void ValidateCaseIds(FourArmEvaluationResult run, EvaluationScoringReport scoring)
    {
        static bool IsFrozenCase(string id) => Regex.IsMatch(id, "^EVAL-V2-(?:0[1-9]|[1-3][0-9]|40)$", RegexOptions.CultureInvariant);
        if (run.Cases.Any(item => !IsFrozenCase(item.CaseId)) ||
            run.BudgetStop is { } stop && !IsFrozenCase(stop.CaseId) ||
            scoring.Arms.SelectMany(arm => arm.Failures).Any(failure => !IsFrozenCase(failure.CaseId)) ||
            scoring.BudgetStop is { } scoredStop && !IsFrozenCase(scoredStop.CaseId))
            throw new ArgumentException("evaluation results must contain only frozen suite case identifiers");
        if (run.Cases.Any(item =>
                !CaseCategories.Contains(item.Category, StringComparer.Ordinal) ||
                !ExpectedStates.Contains(item.ExpectedState, StringComparer.Ordinal)))
            throw new ArgumentException("evaluation results must contain only frozen case metadata");
    }

    private static string? ExecutionMode(EvaluationArm arm) => arm switch
    {
        EvaluationArm.A => nameof(EvaluationExecutionMode.RulesBaseline),
        EvaluationArm.B or EvaluationArm.C => nameof(EvaluationExecutionMode.BoundedAgent),
        EvaluationArm.D => nameof(EvaluationExecutionMode.SingleCallWithoutToolsOrCitationReflection),
        _ => throw new ArgumentOutOfRangeException(nameof(arm))
    };

    private static EvaluationArm ParseArm(string id)
    {
        if (!Enum.TryParse<EvaluationArm>(id, ignoreCase: false, out var arm) ||
            !StringComparer.Ordinal.Equals(arm.ToString(), id))
            throw new ArgumentException("arm must be A, B, C, or D");
        return arm;
    }

    private static void AddCost(List<CostFigureDto> figures, string name, EvaluationCostFigure figure, string unit)
    {
        ValidateLabel(figure.Label);
        figures.Add(new(name, figure.Value, unit, figure.Label));
    }

    private static void ValidateLabel(string? label)
    {
        if (label is null || !EvidenceLabels.Contains(label, StringComparer.Ordinal))
            throw new ArgumentException("exactly one allowed figure label is required");
    }

    private static void RequireProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            element.EnumerateObject().Count() != expected.Length ||
            expected.Any(name => !element.TryGetProperty(name, out _)))
            throw new JsonException("object does not match the evaluation report schema");
    }

    private sealed record ReportDto(int SchemaVersion, int SuiteVersion, string SuiteSha256, bool Incomplete, string? StopReason,
        string? SelectedArmId, bool SelectedArmQualified, IReadOnlyList<ArmDto> Arms, IReadOnlyList<CostFigureDto> CostFigures);
    private sealed record ArmDto(string ArmId, string? Deployment, string? Mode, int CompletedCases, IReadOnlyList<MetricDto> Metrics, bool Passed);
    private sealed record MetricDto(string Name, decimal? Value, decimal? EligibleCount, string Unit, string EvidenceLabel);
    private sealed record CostFigureDto(string Name, decimal? Value, string Unit, string EvidenceLabel);
}
