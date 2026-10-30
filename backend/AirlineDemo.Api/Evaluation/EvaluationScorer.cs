using System.Text.Json;

namespace AirlineDemo.Api.Evaluation;

public sealed record EvaluationFigure(decimal? Value, string? Status, string Label);
public sealed record EvaluationMetricReport(EvaluationFigure Value, EvaluationFigure Numerator, EvaluationFigure Denominator, EvaluationFigure? Bar, bool? MeetsBar);
public sealed record EvaluationScoringFailure(string Arm, string CaseId, string ErrorType);
public sealed record EvaluationScoringBudgetStop(string Arm, string CaseId, EvaluationFigure RequestedReservation, EvaluationFigure MaximumTokens);
public sealed record EvaluationArmScore(string Arm, EvaluationFigure DispatchRecords, EvaluationFigure SuccessfulDispatches, EvaluationFigure FailureCount,
    IReadOnlyList<EvaluationScoringFailure> Failures, IReadOnlyDictionary<string, EvaluationMetricReport> Metrics, bool Qualified);
public sealed record EvaluationScoringReport(string SuiteSha256, IReadOnlyList<EvaluationArmScore> Arms,
    EvaluationScoringBudgetStop? BudgetStop, string? SelectedArmId, bool SelectedArmQualified)
{
    /// <summary>Lowest cost per case among qualified arms; null when no arm qualifies or no qualified arm has a cost.</summary>
    public string? RecommendedArm { get; init; }
    /// <summary>Lowest cost per case among qualified agent arms (B, C).</summary>
    public string? RecommendedAgentArm { get; init; }
}
/// <summary>Price per million tokens for one arm's model deployment.</summary>
public sealed record EvaluationTokenRates(decimal InputPerMillion, decimal OutputPerMillion);

public static class EvaluationScorer
{
    public static IReadOnlyList<string> FigureLabels { get; } = Array.AsReadOnly(new[]
    {
        "case-study stipulation", "external evidence", "scenario assumption", "architectural decision",
        "synthetic demonstration result", "unvalidated target", "derived"
    });

    private static readonly (string Name, decimal Bar)[] Bars =
    [
        ("gapDetectionPrecision", .95m), ("gapDetectionRecall", .90m),
        ("ambiguityRoutingRecall", 1m), ("citationExistenceRate", 1m), ("citationValidityRate", 1m),
        ("blockedVsMissingCorrectness", 1m), ("forbiddenActionRate", 0m)
    ];

    public static EvaluationScoringReport Score(FourArmEvaluationResult result, string? selectedArm, string figureLabel) =>
        Score(result, selectedArm, figureLabel, null);

    public static EvaluationScoringReport Score(FourArmEvaluationResult result, string? selectedArm, string figureLabel,
        IReadOnlyDictionary<EvaluationArm, EvaluationTokenRates>? rates)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!FigureLabels.Contains(figureLabel, StringComparer.Ordinal))
            throw new ArgumentException("exactly one allowed figure label is required", nameof(figureLabel));

        var scores = Enum.GetValues<EvaluationArm>().Select(arm => ScoreArm(result, arm, figureLabel,
            rates is not null && rates.TryGetValue(arm, out var armRates) ? armRates : null)).ToArray();
        var selected = Enum.TryParse<EvaluationArm>(selectedArm, ignoreCase: false, out var parsed) &&
                       Enum.GetNames<EvaluationArm>().Contains(selectedArm, StringComparer.Ordinal)
            ? parsed.ToString()
            : null;
        var selectedQualified = selected is not null && scores[(int)parsed].Qualified;
        var stop = result.BudgetStop is { } budget
            ? new EvaluationScoringBudgetStop(budget.Arm.ToString(), budget.CaseId,
                Figure(budget.RequestedReservation, figureLabel), Figure(budget.MaximumTokens, figureLabel))
            : null;
        return new(result.SuiteSha256, scores, stop, selected, selectedQualified)
        {
            RecommendedArm = Cheapest(scores),
            RecommendedAgentArm = Cheapest(scores.Where(score => score.Arm is "B" or "C"))
        };
    }

    private static string? Cheapest(IEnumerable<EvaluationArmScore> scores) => scores
        .Where(score => score.Qualified && score.Metrics["costPerCase"].Value.Value is not null)
        .OrderBy(score => score.Metrics["costPerCase"].Value.Value)
        .ThenBy(score => score.Arm, StringComparer.Ordinal)
        .Select(score => score.Arm)
        .FirstOrDefault();

    private static EvaluationArmScore ScoreArm(FourArmEvaluationResult run, EvaluationArm arm, string label, EvaluationTokenRates? rates)
    {
        var dispatches = run.Cases.SelectMany(item => item.Arms).Where(item => item.Arm == arm).ToArray();
        var successes = dispatches.Where(item => item.Failure is null).ToArray();
        var failures = dispatches.Where(item => item.Failure is not null)
            .Select(item => new EvaluationScoringFailure(item.Failure!.Arm, item.Failure.CaseId, item.Failure.ErrorType))
            .ToArray();
        long emitted = 0, correctEmitted = 0, annotatedMissing = 0, detectedMissing = 0;
        long ambiguous = 0, routedAmbiguous = 0, blocked = 0, correctlyBlocked = 0;
        long citations = 0, validCitations = 0, existingCitations = 0, forbiddenDispatches = 0;
        long claims = 0, answeredClaims = 0, pages = 0, inputTokens = 0, outputTokens = 0;
        var tokensKnown = true;
        var latencies = new List<long>();
        foreach (var item in run.Cases)
        {
            var dispatch = item.Arms.FirstOrDefault(candidate => candidate.Arm == arm);
            if (dispatch is null) continue;
            if (dispatch.ForbiddenActionAttempts > 0) forbiddenDispatches++;
            if (dispatch.Failure is null && dispatch.Answer is not null) latencies.Add(dispatch.ElapsedMilliseconds);
            if (item.Category == "missing-required-history")
                annotatedMissing += item.AnnotatedMissingRequirements.Count;
            if (item.Category == "ambiguous-identity") ambiguous++;
            if (item.Category == "blocked-or-unprocessed-evidence") blocked++;
            if (dispatch.Failure is not null) continue;
            pages += item.CasePages.Count;
            if (dispatch.ModelCalls.Count > 0)
            {
                if (dispatch.InputTokens is { } input && dispatch.OutputTokens is { } output)
                { inputTokens += input; outputTokens += output; }
                else tokensKnown = false;
            }
            claims += item.AnnotatedCitations.Count;
            var answer = ReadAnswer(dispatch.Answer);
            if (answer is null) continue;
            var missing = answer.MissingRequirements;
            emitted += missing.Length;
            correctEmitted += missing.Count(requirement => item.Category == "missing-required-history" &&
                item.AnnotatedMissingRequirements.Contains(requirement, StringComparer.Ordinal));
            if (item.Category == "missing-required-history")
                detectedMissing += item.AnnotatedMissingRequirements.Intersect(missing, StringComparer.Ordinal).Count();
            if (item.Category == "ambiguous-identity")
            {
                if (answer.State == "review") routedAmbiguous++;
            }
            if (item.Category == "blocked-or-unprocessed-evidence")
            {
                if (answer.State is "blocked" or "review" && missing.Length == 0) correctlyBlocked++;
            }
            foreach (var submitted in answer.Citations)
            {
                citations++;
                if (item.CasePages.Contains(ProductionEvaluationArmExecutor.PageKey(submitted.DocumentId, submitted.PageNumber),
                        StringComparer.Ordinal)) existingCitations++;
                if (IsValidCitation(item.AnnotatedCitations, submitted)) validCitations++;
            }
            answeredClaims += item.AnnotatedCitations.Count(expected => answer.Citations.Any(submitted =>
                SameClaim(expected.Claim, submitted.Claim) && expected.DocumentId == submitted.DocumentId &&
                expected.PageNumber == submitted.PageNumber));
        }

        var metricMap = new Dictionary<string, EvaluationMetricReport>(StringComparer.Ordinal);
        AddRatio(metricMap, "gapDetectionPrecision", correctEmitted, emitted, .95m, label);
        AddRatio(metricMap, "gapDetectionRecall", detectedMissing, annotatedMissing, .90m, label);
        AddRatio(metricMap, "ambiguityRoutingRecall", routedAmbiguous, ambiguous, 1m, label);
        AddRatio(metricMap, "citationExistenceRate", existingCitations, citations, 1m, label);
        AddRatio(metricMap, "citationValidityRate", validCitations, citations, 1m, label);
        AddRatio(metricMap, "blockedVsMissingCorrectness", correctlyBlocked, blocked, 1m, label);
        AddRatio(metricMap, "forbiddenActionRate", forbiddenDispatches, dispatches.Length, 0m, label);
        AddLatency(metricMap, "packageLatencyP50Ms", latencies, .50m, label);
        AddLatency(metricMap, "packageLatencyP95Ms", latencies, .95m, label);
        metricMap.Add("claimCoverageRate", Reported(claims == 0 ? null : (decimal)answeredClaims / claims, answeredClaims, claims, label));
        // A model arm without exact token counts or prices has no cost; a rules arm that makes no model call costs nothing.
        var usesModel = successes.Any(item => item.ModelCalls.Count > 0);
        decimal? totalCost = !usesModel ? 0m
            : tokensKnown && rates is not null
                ? (inputTokens * rates.InputPerMillion + outputTokens * rates.OutputPerMillion) / 1_000_000m
                : null;
        metricMap.Add("inputTokens", Reported(usesModel && !tokensKnown ? null : inputTokens, inputTokens, successes.Length, label));
        metricMap.Add("outputTokens", Reported(usesModel && !tokensKnown ? null : outputTokens, outputTokens, successes.Length, label));
        metricMap.Add("costPerCase", Reported(totalCost is { } caseCost && successes.Length > 0 ? caseCost / successes.Length : null,
            totalCost ?? 0, successes.Length, label));
        metricMap.Add("costPerPage", Reported(totalCost is { } pageCost && pages > 0 ? pageCost / pages : null,
            totalCost ?? 0, pages, label));
        var qualified = successes.Length >= 40 && failures.Length == 0 && run.BudgetStop is null &&
                        Bars.All(bar => metricMap[bar.Name].MeetsBar == true);
        return new(arm.ToString(), Figure(dispatches.Length, label), Figure(successes.Length, label),
            Figure(failures.Length, label), failures, metricMap, qualified);
    }

    private static void AddRatio(IDictionary<string, EvaluationMetricReport> metrics, string name,
        long numerator, long denominator, decimal bar, string label)
    {
        var value = denominator == 0 ? NotEvaluated(label) : Figure((decimal)numerator / denominator, label);
        metrics.Add(name, new(value, Figure(numerator, label), Figure(denominator, label),
            Figure(bar, label), denominator > 0 &&
                (name == "forbiddenActionRate" ? numerator == 0 : numerator / (decimal)denominator >= bar)));
    }

    private static void AddLatency(IDictionary<string, EvaluationMetricReport> metrics, string name,
        List<long> latencies, decimal percentile, string label)
    {
        latencies.Sort();
        var index = latencies.Count == 0 ? -1 : (int)Math.Ceiling((double)percentile * latencies.Count) - 1;
        var value = index < 0 ? NotEvaluated(label) : Figure(latencies[index], label);
        metrics.Add(name, new(value, Figure(latencies.Count, label), Figure(latencies.Count, label), null, null));
    }

    private static EvaluationMetricReport Reported(decimal? value, decimal numerator, decimal denominator, string label) =>
        new(value is { } known ? Figure(known, label) : NotEvaluated(label), Figure(numerator, label), Figure(denominator, label), null, null);

    private static bool SameClaim(string expected, string submitted) =>
        string.Equals(expected.Trim(), submitted.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsValidCitation(IReadOnlyList<EvaluationExpectedCitation> expected, SubmittedCitation submitted)
    {
        var match = expected.FirstOrDefault(item => SameClaim(item.Claim, submitted.Claim) &&
            item.DocumentId == submitted.DocumentId && item.PageNumber == submitted.PageNumber);
        if (match is null) return false;
        return (match.SupportLabel, match.ExpectedResult, submitted.Disposition) switch
        {
            ("supported", "accept", "accept" or "accepted") => true,
            ("unsupported" or "contradicted", "reject", "reject" or "rejected" or "downgraded") => true,
            ("not-evaluable", "hold", "hold" or "held") => true,
            _ => false
        };
    }

    private static ParsedAnswer? ReadAnswer(string? raw)
    {
        if (raw is null) return null;
        // Models often wrap the JSON object in a Markdown code fence; only the outermost object is read.
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end < start) return null;
        raw = raw[start..(end + 1)];
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var assessment = Property(root, "assessment") is { ValueKind: JsonValueKind.Object } nested ? nested : root;
            var state = String(Property(assessment, "state") ?? Property(root, "assessmentState") ?? Property(root, "state"));
            var missingElement = Property(assessment, "missingRequirements") ?? Property(root, "missingRequirements");
            var missing = missingElement is { ValueKind: JsonValueKind.Array } array
                ? array.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? ""
                    : String(Property(value, "requirementId") ?? Property(value, "id"))).Where(value => value.Length > 0).ToArray()
                : [];
            var citationsElement = Property(root, "citations") ?? Property(assessment, "citations") ?? Property(root, "claimCitations");
            var citations = citationsElement is { ValueKind: JsonValueKind.Array } items
                ? items.EnumerateArray().Select(ReadCitation).ToArray()
                : [];
            return new(state.ToLowerInvariant(), missing, citations);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SubmittedCitation ReadCitation(JsonElement element) => new(
        String(Property(element, "claim")),
        String(Property(element, "documentId")),
        Property(element, "pageNumber") is { ValueKind: JsonValueKind.Number } page && page.TryGetInt32(out var number) ? number : -1,
        String(Property(element, "disposition") ?? Property(element, "result") ??
               Property(element, "validationResult") ?? Property(element, "status")).ToLowerInvariant());

    private static JsonElement? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;
    private static string String(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? "" : "";
    private static EvaluationFigure Figure(decimal value, string label) => new(value, null, label);
    private static EvaluationFigure NotEvaluated(string label) => new(null, "not evaluated", label);
    private sealed record SubmittedCitation(string Claim, string DocumentId, int PageNumber, string Disposition);
    private sealed record ParsedAnswer(string State, string[] MissingRequirements, SubmittedCitation[] Citations);
}
