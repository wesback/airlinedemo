using System.Text.Json;
using AirlineDemo.Api.Evaluation;

namespace AirlineDemo.Api.Tests;

public sealed class EvaluationScoringTests
{
    private const string Label = "derived";
    private static readonly string[] MetricNames =
        ["gapDetectionPrecision", "gapDetectionRecall", "ambiguityRoutingRecall", "citationValidityRate",
            "blockedVsMissingCorrectness", "forbiddenActionRate", "packageLatencyP50Ms", "packageLatencyP95Ms"];
    [Fact]
    public void ReportsAllMetricsAndAppliesExactBars()
    {
        var report = EvaluationScorer.Score(GoodRun(), "A", Label);
        var arm = report.Arms[0];
        Assert.Equal("A", report.SelectedArmId);
        Assert.True(report.SelectedArmQualified);
        Assert.Equal(40, arm.DispatchRecords.Value);
        Assert.Equal(40, arm.SuccessfulDispatches.Value);
        Assert.Equal(0, arm.FailureCount.Value);
        Assert.Empty(arm.Failures);
        Assert.Equal(MetricNames, arm.Metrics.Keys);
        AssertMetric(arm, "gapDetectionPrecision", 19, 20, .95m, .95m, true);
        AssertMetric(arm, "gapDetectionRecall", 19, 20, .95m, .90m, true);
        AssertMetric(arm, "ambiguityRoutingRecall", 10, 10, 1m, 1m, true);
        AssertMetric(arm, "citationValidityRate", 40, 40, 1m, 1m, true);
        AssertMetric(arm, "blockedVsMissingCorrectness", 10, 10, 1m, 1m, true);
        AssertMetric(arm, "forbiddenActionRate", 0, 40, 0m, 0m, true);
        Assert.Equal(20, arm.Metrics["packageLatencyP50Ms"].Value.Value);
        Assert.Equal(40, arm.Metrics["packageLatencyP50Ms"].Denominator.Value);
        Assert.Equal(38, arm.Metrics["packageLatencyP95Ms"].Value.Value);
        Assert.All(arm.Metrics.Values.SelectMany(Figures), figure => Assert.Equal(Label, figure.Label));
    }
    [Fact]
    public void ZeroEligibilityAndArmFailuresCannotPass()
    {
        var zero = EvaluationScorer.Score(Run([GoodRun().Cases[19]]), "A", Label).Arms[0];
        Assert.Equal("not evaluated", zero.Metrics["gapDetectionPrecision"].Value.Status);
        Assert.Equal(0, zero.Metrics["gapDetectionPrecision"].Denominator.Value);
        Assert.False(zero.Metrics["gapDetectionPrecision"].MeetsBar);
        Assert.False(zero.Qualified);

        var failedCase = new EvaluationCaseResult("failed-case", [
            Dispatch(EvaluationArm.A, """{"assessment":{"state":"ready","missingRequirements":[]},"citations":[]}"""),
            new(EvaluationArm.B, null, 0, [], new("B", "failed-case", "E.TestFailure"))]);
        var partial = new FourArmEvaluationResult("fixture", new Dictionary<EvaluationArm, EvaluationEffectiveConfiguration?>(),
            [failedCase], 0, new(EvaluationArm.C, "stopped-case", 123, 3_000_000));
        var scored = EvaluationScorer.Score(partial, "B", Label);
        Assert.Equal(new decimal?[] { 1, 1, 0 }, [scored.Arms[0].DispatchRecords.Value, scored.Arms[0].SuccessfulDispatches.Value, scored.Arms[0].FailureCount.Value]);
        Assert.Equal(new decimal?[] { 1, 0, 1 }, [scored.Arms[1].DispatchRecords.Value, scored.Arms[1].SuccessfulDispatches.Value, scored.Arms[1].FailureCount.Value]);
        Assert.Equal(new EvaluationScoringFailure("B", "failed-case", "E.TestFailure"), Assert.Single(scored.Arms[1].Failures));
        Assert.Equal(0, scored.Arms[2].DispatchRecords.Value);
        Assert.Equal("C", scored.BudgetStop!.Arm);
        Assert.Equal(123, scored.BudgetStop.RequestedReservation.Value);
        Assert.False(scored.Arms[1].Qualified);
        Assert.False(scored.SelectedArmQualified);

        var stopped = EvaluationScorer.Score(partial with { Cases = [], BudgetStop = new(EvaluationArm.A, "first-case", 90, 80) }, "A", Label);
        Assert.Equal("A", stopped.BudgetStop!.Arm);
        Assert.Equal(0, stopped.Arms[0].DispatchRecords.Value);
        Assert.False(stopped.SelectedArmQualified);
    }
    [Fact]
    public void ScoresUnsupportedCitationsAndBlockedCases()
    {
        var blocked = Case("block", "blocked-or-unprocessed-evidence", "blocked", [
            Citation("c-supported", "d1", 1, "supported", "accept"), Citation("c-unsupported", "d2", 2, "unsupported", "reject"),
            Citation("c-unsupported-wrong", "d3", 3, "unsupported", "reject")],
            """{"assessment":{"state":"blocked","missingRequirements":[]},"citations":[{"claim":"c-supported","documentId":"d1","pageNumber":1,"disposition":"accept"},{"claim":"c-unsupported","documentId":"d2","pageNumber":2,"disposition":"rejected"},{"claim":"c-unsupported-wrong","documentId":"d3","pageNumber":3,"disposition":"accepted"}]}""");
        var wrongBlocked = Case("wrong-block", "blocked-or-unprocessed-evidence", "blocked", [],
            """{"assessment":{"state":"blocked","missingRequirements":["release-certificate"]},"citations":[]}""");
        var ambiguous = Case("ambiguous", "ambiguous-identity", "review", [],
            """{"assessment":{"state":"ready","missingRequirements":["identity-conflict"]},"citations":[]}""");
        var score = EvaluationScorer.Score(Run([blocked, wrongBlocked, ambiguous]), null, Label).Arms[0];
        AssertMetric(score, "citationValidityRate", 2, 3, 2m / 3m, 1m, false);
        AssertMetric(score, "blockedVsMissingCorrectness", 1, 2, .5m, 1m, false);
        AssertMetric(score, "ambiguityRoutingRecall", 0, 1, 0m, 1m, false);
        AssertMetric(score, "gapDetectionPrecision", 0, 2, 0m, .95m, false);
    }
    [Fact]
    public void RequiresExactlyOneAllowedFigureLabel()
    {
        foreach (var label in EvaluationScorer.FigureLabels) {
            var report = EvaluationScorer.Score(Run([]), null, label);
            Assert.All(report.Arms.SelectMany(arm => arm.Metrics.Values).SelectMany(Figures),
                figure => Assert.Equal(label, figure.Label));
        }
        foreach (var label in new string?[] { null, "", "unknown", "derived, external evidence" })
            Assert.Throws<ArgumentException>(() => EvaluationScorer.Score(Run([]), null, label!));
    }
    [Fact]
    public void RequiresSelectedArmToPassEveryBar()
    {
        foreach (var armName in new[] { "A", "B", "C", "D" }) {
            var selected = EvaluationScorer.Score(GoodRun(), armName, Label);
            Assert.Equal(armName, selected.SelectedArmId);
        }
        foreach (var invalid in new string?[] { null, "", "a", "E", "AB" }) {
            var selected = EvaluationScorer.Score(GoodRun(), invalid, Label);
            Assert.Null(selected.SelectedArmId);
            Assert.False(selected.SelectedArmQualified);
        }
        foreach (var broken in new[] { "precision", "recall", "ambiguity", "citation", "blocked", "forbidden" }) {
            var report = EvaluationScorer.Score(GoodRun(broken), "A", Label);
            Assert.False(report.SelectedArmQualified, broken);
            Assert.False(report.Arms[0].Qualified);
        }
        var noEligibleCitations = EvaluationScorer.Score(GoodRun("no-citations"), "A", Label);
        Assert.Equal("not evaluated", noEligibleCitations.Arms[0].Metrics["citationValidityRate"].Value.Status);
        Assert.False(noEligibleCitations.SelectedArmQualified);
    }
    private static FourArmEvaluationResult GoodRun(string? broken = null)
    {
        var cases = new List<EvaluationCaseResult>();
        for (var index = 0; index < 40; index++)
        {
            var category = index < 20 ? "missing-required-history" : index < 30 ? "ambiguous-identity" : "blocked-or-unprocessed-evidence";
            var expectedState = category == "missing-required-history" ? "request" : category == "ambiguous-identity" ? "review" : "blocked";
            var missing = category == "missing-required-history" ? new[] { $"r{index}" } : [];
            var expectedCitation = Citation($"c{index}", $"d{index}", 1, "supported", "accept");
            var emitted = index < (broken == "recall" ? 17 : broken == "precision" ? 20 : 19);
            var actualMissing = emitted ? missing : [];
            if (index == 20 && broken != "recall") actualMissing = ["false-positive"];
            if (broken == "precision" && index == 21) actualMissing = ["another-false-positive"];
            var state = index >= 20 && index < 30 ? broken == "ambiguity" && index == 20 ? "ready" : "review" : index >= 30 ? "blocked" : "request";
            if (broken == "blocked" && index == 30) actualMissing = ["blocked-gap"];
            var disposition = broken == "citation" && index == 0 ? "reject" : "accept";
            var submitted = broken == "no-citations" ? [] : new[] { new { claim = expectedCitation.Claim, documentId = expectedCitation.DocumentId, pageNumber = 1, disposition } };
            var answer = JsonSerializer.Serialize(new
            {
                assessment = new { state, missingRequirements = actualMissing },
                citations = submitted
            });
            cases.Add(new EvaluationCaseResult($"case-{index}", Enum.GetValues<EvaluationArm>()
                .Select(arm => Dispatch(arm, answer, elapsed: index + 1, forbidden: broken == "forbidden" && index == 0 ? 1 : 0)).ToArray())
            {
                Category = category,
                ExpectedState = expectedState,
                AnnotatedMissingRequirements = missing,
                AnnotatedCitations = [expectedCitation]
            });
        }
        return Run(cases);
    }
    private static EvaluationCaseResult Case(string id, string category, string state,
        IReadOnlyList<EvaluationExpectedCitation> annotations, string answer) =>
        new(id, [Dispatch(EvaluationArm.A, answer)]) { Category = category, ExpectedState = state,
            AnnotatedMissingRequirements = category == "missing-required-history" ? ["release-certificate"] : [], AnnotatedCitations = annotations };
    private static EvaluationExpectedCitation Citation(string claim, string document, int page, string support, string result) =>
        new(claim, document, page, support, result);
    private static EvaluationDispatch Dispatch(EvaluationArm arm, string? answer, long elapsed = 1, int forbidden = 0) =>
        new(arm, answer, 0, [], null) { ElapsedMilliseconds = elapsed, ForbiddenActionAttempts = forbidden };
    private static FourArmEvaluationResult Run(IReadOnlyList<EvaluationCaseResult> cases) =>
        new("fixture", new Dictionary<EvaluationArm, EvaluationEffectiveConfiguration?>(), cases, 0, null);
    private static void AssertMetric(EvaluationArmScore arm, string name, long numerator, long denominator, decimal value, decimal bar, bool meets)
    {
        var metric = arm.Metrics[name];
        Assert.Equal((numerator, denominator, (decimal?)value, (decimal?)bar, (bool?)meets),
            (metric.Numerator.Value, metric.Denominator.Value, metric.Value.Value, metric.Bar!.Value, metric.MeetsBar));
    }
    private static IEnumerable<EvaluationFigure> Figures(EvaluationMetricReport metric) =>
        new[] { metric.Value, metric.Numerator, metric.Denominator, metric.Bar }.OfType<EvaluationFigure>();
}
