using System.Text.Json;
using DemoCaseGenerator;

namespace AirlineDemo.Api.Tests;

public sealed class EvaluationCostCalculatorTests
{
    [Fact]
    public void CalculatesUnitCostsAndMarksMissingInputs()
    {
        var measurements = new Dictionary<string, EvaluationArmCostMeasurement?>
        {
            ["A"] = new(101m, 2m, 4m),
            ["B"] = new(203m, 3m, 7m),
            ["C"] = new(307m, 5m, 11m),
            ["D"] = new(409m, 7m, 13m)
        };
        var report = Calculate(measurements);

        Assert.Equal(new decimal?[] { 50.5m, 203m / 3m, 307m / 5m, 409m / 7m },
            report.Arms.Select(arm => arm.CostPerProcessedPageEur.Value));
        Assert.Equal(new decimal?[] { 101m / 4m, 203m / 7m, 307m / 11m, 409m / 13m },
            report.Arms.Select(arm => arm.CostPerCompletedCaseEur.Value));
        Assert.All(report.Arms.SelectMany(arm => new[] { arm.CostPerProcessedPageEur, arm.CostPerCompletedCaseEur }),
            figure => Assert.Null(figure.Status));

        var missingArm = Calculate(new Dictionary<string, EvaluationArmCostMeasurement?>());
        AssertNotMeasured(missingArm.Arms[0].CostPerProcessedPageEur);
        AssertNotMeasured(missingArm.Arms[0].CostPerCompletedCaseEur);
        var missingTotalCost = Calculate(new Dictionary<string, EvaluationArmCostMeasurement?> { ["A"] = new(null, 2m, 4m) });
        AssertNotMeasured(missingTotalCost.Arms[0].CostPerProcessedPageEur);
        AssertNotMeasured(missingTotalCost.Arms[0].CostPerCompletedCaseEur);
        var missingPageCount = Calculate(new Dictionary<string, EvaluationArmCostMeasurement?> { ["A"] = new(10m, null, 4m) });
        AssertNotMeasured(missingPageCount.Arms[0].CostPerProcessedPageEur);
        var missingCaseCount = Calculate(new Dictionary<string, EvaluationArmCostMeasurement?> { ["A"] = new(10m, 2m, null) });
        AssertNotMeasured(missingCaseCount.Arms[0].CostPerCompletedCaseEur);
        var zeroDenominator = Calculate(new Dictionary<string, EvaluationArmCostMeasurement?> { ["A"] = new(10m, 0m, 0m) });
        AssertNotMeasured(zeroDenominator.Arms[0].CostPerProcessedPageEur);
        AssertNotMeasured(zeroDenominator.Arms[0].CostPerCompletedCaseEur);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Calculate(new Dictionary<string, EvaluationArmCostMeasurement?> { ["A"] = new(10m, -1m, 1m) }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Calculate(new Dictionary<string, EvaluationArmCostMeasurement?> { ["A"] = new(10m, 1m, -1m) }));
    }

    [Fact]
    public void CalculatesBreakEvenOnlyFromAvailableInputs()
    {
        var report = EvaluationCostCalculator.Calculate(
            new Dictionary<string, EvaluationArmCostMeasurement?>(), 25_000m, 8_200m, "synthetic demonstration result");

        Assert.Equal(25_000m / 8_200m, report.BreakEvenDaysPerTransition.Value);
        Assert.Equal("derived", report.BreakEvenDaysPerTransition.Label);
        Assert.Null(report.BreakEvenDaysPerTransition.Status);
        AssertNotMeasured(EvaluationCostCalculator.Calculate(
            new Dictionary<string, EvaluationArmCostMeasurement?>(), null, 8_200m, "synthetic demonstration result")
            .BreakEvenDaysPerTransition);
        AssertNotMeasured(EvaluationCostCalculator.Calculate(
            new Dictionary<string, EvaluationArmCostMeasurement?>(), 25_000m, null, "synthetic demonstration result")
            .BreakEvenDaysPerTransition);
        AssertNotMeasured(EvaluationCostCalculator.Calculate(
            new Dictionary<string, EvaluationArmCostMeasurement?>(), 25_000m, 0m, "synthetic demonstration result")
            .BreakEvenDaysPerTransition);
    }

    [Fact]
    public void EnforcesFigureLabelsAndCaseStudyValues()
    {
        var labels = new[]
        {
            "case-study stipulation", "external evidence", "scenario assumption", "architectural decision",
            "synthetic demonstration result", "unvalidated target", "derived"
        };
        foreach (var label in labels)
        {
            var report = Calculate(new Dictionary<string, EvaluationArmCostMeasurement?> { ["A"] = new(10m, 2m, 1m) }, label);
            Assert.Equal(label, report.Arms[0].CostPerProcessedPageEur.Label);
            Assert.Equal(label, report.Arms[0].CostPerCompletedCaseEur.Label);
        }
        foreach (var label in new string?[] { null, "", "unknown", "derived, external evidence" })
            Assert.Throws<ArgumentException>(() => Calculate(new Dictionary<string, EvaluationArmCostMeasurement?>(), label));

        Assert.Equal(250_000m, EvaluationCostCalculator.RecoveredDayValuePerIdleAircraftMonth.Value);
        Assert.Equal("case-study stipulation", EvaluationCostCalculator.RecoveredDayValuePerIdleAircraftMonth.Label);
        Assert.Equal(8_200m, EvaluationCostCalculator.RecoveredDayValueEurPerDay.Value);
        Assert.Equal("case-study stipulation", EvaluationCostCalculator.RecoveredDayValueEurPerDay.Label);
        var breakEven = EvaluationCostCalculator.Calculate(new Dictionary<string, EvaluationArmCostMeasurement?>(),
            16_400m, EvaluationCostCalculator.RecoveredDayValueEurPerDay.Value, "synthetic demonstration result");
        Assert.Equal(2m, breakEven.BreakEvenDaysPerTransition.Value);
    }

    [Fact]
    public void DoesNotPresentCostsAsValidatedCustomerSavings()
    {
        var report = Calculate(new Dictionary<string, EvaluationArmCostMeasurement?>
        {
            ["A"] = new(12m, 3m, 2m)
        });
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(report));
        var root = document.RootElement;
        Assert.Equal(new[] { "Arms", "BreakEvenDaysPerTransition" },
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(new[] { "A", "B", "C", "D" },
            root.GetProperty("Arms").EnumerateArray().Select(arm => arm.GetProperty("Arm").GetString()));
        foreach (var arm in root.GetProperty("Arms").EnumerateArray())
        {
            Assert.Equal(new[] { "Arm", "CostPerProcessedPageEur", "CostPerCompletedCaseEur" },
                arm.EnumerateObject().Select(property => property.Name));
            Assert.Equal("synthetic demonstration result",
                arm.GetProperty("CostPerProcessedPageEur").GetProperty("Label").GetString());
            Assert.Equal("synthetic demonstration result",
                arm.GetProperty("CostPerCompletedCaseEur").GetProperty("Label").GetString());
        }
        Assert.Equal(new[] { "Value", "Status", "Label" },
            root.GetProperty("BreakEvenDaysPerTransition").EnumerateObject().Select(property => property.Name));
        Assert.Equal("derived", root.GetProperty("BreakEvenDaysPerTransition").GetProperty("Label").GetString());
        var missingPageCost = root.GetProperty("Arms")[1].GetProperty("CostPerProcessedPageEur");
        var missingCaseCost = root.GetProperty("Arms")[1].GetProperty("CostPerCompletedCaseEur");
        Assert.Equal("not measured", missingPageCost.GetProperty("Status").GetString());
        Assert.Equal("not measured", missingCaseCost.GetProperty("Status").GetString());
        Assert.Equal(JsonValueKind.Null, missingPageCost.GetProperty("Value").ValueKind);
        Assert.Equal(JsonValueKind.Null, missingCaseCost.GetProperty("Value").ValueKind);
        var missingBreakEven = root.GetProperty("BreakEvenDaysPerTransition");
        Assert.Equal("not measured", missingBreakEven.GetProperty("Status").GetString());
        Assert.Equal(JsonValueKind.Null, missingBreakEven.GetProperty("Value").ValueKind);
        Assert.DoesNotContain("savings", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("validated", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    private static EvaluationCostReport Calculate(
        IReadOnlyDictionary<string, EvaluationArmCostMeasurement?> measurements,
        string? label = "synthetic demonstration result") =>
        EvaluationCostCalculator.Calculate(measurements, null, null, label);

    private static void AssertNotMeasured(EvaluationCostFigure figure)
    {
        Assert.Null(figure.Value);
        Assert.Equal("not measured", figure.Status);
    }
}
