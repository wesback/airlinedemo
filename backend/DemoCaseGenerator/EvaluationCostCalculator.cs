namespace DemoCaseGenerator;

public sealed record EvaluationCostFigure(decimal? Value, string? Status, string Label);
public sealed record EvaluationArmCostMeasurement(decimal? TotalMeasuredCostEur, decimal? ProcessedPageCount, decimal? CompletedCaseCount);
public sealed record EvaluationArmCostReport(string Arm, EvaluationCostFigure CostPerProcessedPageEur, EvaluationCostFigure CostPerCompletedCaseEur);
public sealed record EvaluationCostReport(IReadOnlyList<EvaluationArmCostReport> Arms, EvaluationCostFigure BreakEvenDaysPerTransition);

public static class EvaluationCostCalculator
{
    private const string NotMeasured = "not measured";
    private const string CaseStudyStipulation = "case-study stipulation";
    private static readonly string[] AllowedLabels =
    [
        CaseStudyStipulation, "external evidence", "scenario assumption", "architectural decision",
        "synthetic demonstration result", "unvalidated target", "derived"
    ];

    public static EvaluationCostFigure RecoveredDayValuePerIdleAircraftMonth { get; } =
        new(250_000m, null, CaseStudyStipulation);

    public static EvaluationCostFigure RecoveredDayValueEurPerDay { get; } =
        new(8_200m, null, CaseStudyStipulation);

    public static EvaluationCostReport Calculate(
        IReadOnlyDictionary<string, EvaluationArmCostMeasurement?> measurements,
        decimal? measuredPlatformCostPerTransitionEur,
        decimal? recoveredDayValueEurPerDay,
        string? measuredCostLabel)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        var costLabel = ValidateLabel(measuredCostLabel);
        if (recoveredDayValueEurPerDay < 0)
            throw new ArgumentOutOfRangeException(nameof(recoveredDayValueEurPerDay));

        var arms = new List<EvaluationArmCostReport>(4);
        foreach (var arm in new[] { "A", "B", "C", "D" })
        {
            measurements.TryGetValue(arm, out var measurement);
            arms.Add(new(
                arm,
                UnitCost(measurement?.TotalMeasuredCostEur, measurement?.ProcessedPageCount, costLabel),
                UnitCost(measurement?.TotalMeasuredCostEur, measurement?.CompletedCaseCount, costLabel)));
        }

        var breakEven = measuredPlatformCostPerTransitionEur is null || recoveredDayValueEurPerDay is null or 0
            ? NotMeasuredFigure("derived")
            : new EvaluationCostFigure(measuredPlatformCostPerTransitionEur.Value / recoveredDayValueEurPerDay.Value, null, "derived");

        return new(arms, breakEven);
    }

    private static EvaluationCostFigure UnitCost(decimal? totalMeasuredCostEur, decimal? denominator, string label)
    {
        if (denominator < 0)
            throw new ArgumentOutOfRangeException(nameof(denominator));
        return totalMeasuredCostEur is null || denominator is null or 0
            ? NotMeasuredFigure(label)
            : new EvaluationCostFigure(totalMeasuredCostEur.Value / denominator.Value, null, label);
    }

    private static EvaluationCostFigure NotMeasuredFigure(string label) => new(null, NotMeasured, label);

    private static string ValidateLabel(string? label)
    {
        if (label is null || !AllowedLabels.Contains(label, StringComparer.Ordinal))
            throw new ArgumentException("exactly one allowed figure label is required", nameof(label));
        return label;
    }
}
