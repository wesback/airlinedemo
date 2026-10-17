using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Evaluation;
using DemoCaseGenerator;

namespace AirlineDemo.Api.Tests;

public sealed class EvaluationJsonExporterTests
{
    private static readonly string[] Labels =
    [
        "case-study stipulation", "external evidence", "scenario assumption", "architectural decision",
        "synthetic demonstration result", "unvalidated target", "derived"
    ];
    private static readonly string[] MetricNames =
    [
        "gapDetectionPrecision", "gapDetectionRecall", "ambiguityRoutingRecall", "citationValidityRate",
        "blockedVsMissingCorrectness", "forbiddenActionRate", "packageLatencyP50Ms", "packageLatencyP95Ms"
    ];

    [Fact]
    public void WritesExactVersionedReportShape()
    {
        var bytes = Export(Inputs());
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        AssertProperties(root, "schemaVersion", "suiteVersion", "suiteSha256", "incomplete", "stopReason",
            "selectedArmId", "selectedArmQualified", "arms", "costFigures");
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2, root.GetProperty("suiteVersion").GetInt32());
        Assert.Equal(new[] { "A", "B", "C", "D" },
            root.GetProperty("arms").EnumerateArray().Select(arm => arm.GetProperty("armId").GetString()));
        Assert.All(root.GetProperty("arms").EnumerateArray(), arm =>
        {
            AssertProperties(arm, "armId", "deployment", "mode", "completedCases", "metrics", "passed");
            Assert.All(arm.GetProperty("metrics").EnumerateArray(), metric =>
                AssertProperties(metric, "name", "value", "eligibleCount", "unit", "evidenceLabel"));
        });
        Assert.All(root.GetProperty("costFigures").EnumerateArray(), figure =>
            AssertProperties(figure, "name", "value", "unit", "evidenceLabel"));
        Assert.Equal(9, root.GetProperty("costFigures").GetArrayLength());
        var firstMetric = root.GetProperty("arms")[0].GetProperty("metrics")[0];
        Assert.Equal(JsonValueKind.Null, firstMetric.GetProperty("value").ValueKind);
        Assert.Equal(1, firstMetric.GetProperty("eligibleCount").GetInt32());
        Assert.Null(root.GetProperty("arms")[0].GetProperty("deployment").GetString());
        Assert.Null(root.GetProperty("arms")[0].GetProperty("mode").GetString());
        Assert.Equal("demo-b", root.GetProperty("arms")[1].GetProperty("deployment").GetString());
        Assert.Equal("BoundedAgent", root.GetProperty("arms")[1].GetProperty("mode").GetString());
        EvaluationJsonExporter.ValidateJson(bytes);

        var inputs = Inputs();
        foreach (var version in new[] { 0, 2 })
        {
            var stream = new MemoryStream();
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                EvaluationJsonExporter.Write(stream, inputs.Run, inputs.Scoring, inputs.Costs, version));
            Assert.Empty(stream.ToArray());
        }
        foreach (var level in new[] { "root", "arm", "metric", "cost" })
        {
            var changed = JsonNode.Parse(Encoding.UTF8.GetString(bytes))!.AsObject();
            AddUnknownProperty(changed, level);
            Assert.Throws<JsonException>(() => EvaluationJsonExporter.ValidateJson(Encoding.UTF8.GetBytes(changed.ToJsonString())));
        }
        foreach (var version in new[] { 0, 2 })
        {
            var unsupported = JsonNode.Parse(Encoding.UTF8.GetString(bytes))!.AsObject();
            unsupported["schemaVersion"] = version;
            Assert.Throws<JsonException>(() => EvaluationJsonExporter.ValidateJson(Encoding.UTF8.GetBytes(unsupported.ToJsonString())));
        }
    }

    [Fact]
    public void SerializesInStableOrder()
    {
        var input = Inputs(reverse: true);
        var first = Export(input);
        var second = Export(input);
        Assert.Equal(first, second);
        using var json = JsonDocument.Parse(first);
        Assert.Equal(MetricNames.Order(StringComparer.Ordinal),
            json.RootElement.GetProperty("arms")[0].GetProperty("metrics").EnumerateArray()
                .Select(metric => metric.GetProperty("name").GetString()));
        var costNames = json.RootElement.GetProperty("costFigures").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString()).ToArray();
        Assert.Equal(costNames.Order(StringComparer.Ordinal), costNames);
    }

    [Fact]
    public void PreservesIncompleteResultsWithoutPassing()
    {
        var input = Inputs(qualified: true, incomplete: true);
        using var json = JsonDocument.Parse(Export(input));
        var root = json.RootElement;
        Assert.True(root.GetProperty("incomplete").GetBoolean());
        Assert.Equal("token-budget-exhausted", root.GetProperty("stopReason").GetString());
        Assert.False(root.GetProperty("selectedArmQualified").GetBoolean());
        Assert.All(root.GetProperty("arms").EnumerateArray(), arm => Assert.False(arm.GetProperty("passed").GetBoolean()));
        Assert.Equal(new[] { 2, 1, 0, 0 },
            root.GetProperty("arms").EnumerateArray().Select(arm => arm.GetProperty("completedCases").GetInt32()));
        Assert.All(root.GetProperty("arms").EnumerateArray(), arm =>
            Assert.Equal(JsonValueKind.Null, arm.GetProperty("metrics")[0].GetProperty("value").ValueKind));
        var complete = Inputs();
        using var completeJson = JsonDocument.Parse(Export(complete));
        Assert.False(completeJson.RootElement.GetProperty("incomplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, completeJson.RootElement.GetProperty("stopReason").ValueKind);
    }

    [Fact]
    public void RejectsMissingOrUnknownEvidenceLabels()
    {
        foreach (var label in Labels)
            Assert.NotEmpty(Export(Inputs(label: label)));
        foreach (var label in new string?[] { null, "", "unknown", "derived, external evidence" })
        {
            var output = new MemoryStream();
            var input = Inputs(label: label);
            Assert.Throws<ArgumentException>(() =>
                EvaluationJsonExporter.Write(output, input.Run, input.Scoring, input.Costs));
            Assert.Equal(0, output.Length);
        }
    }

    [Fact]
    public void RefusesContentOutsideReportSchema()
    {
        var invalidInputs = new[]
        {
            Inputs(metricName: "prompt-canary"),
            Inputs(caseId: "customer-id-canary"),
            Inputs(deploymentName: "credential-canary!"),
            Inputs(costArm: "request-payload-canary"),
            Inputs(incomplete: true, stopCaseId: "source-document-canary")
        };
        foreach (var input in invalidInputs)
        {
            var output = new MemoryStream();
            Assert.Throws<ArgumentException>(() =>
                EvaluationJsonExporter.Write(output, input.Run, input.Scoring, input.Costs));
            Assert.Equal(0, output.Length);
        }
        var clean = Inputs();
        var cleanBytes = Export(clean);
        var text = Encoding.UTF8.GetString(cleanBytes);
        foreach (var canary in new[] { "case-input-canary", "source-document-canary", "prompt-canary",
                     "completion-canary", "request-payload-canary", "credential-canary", "customer-id-canary" })
            Assert.DoesNotContain(canary, text, StringComparison.Ordinal);
    }

    private static void AssertProperties(JsonElement element, params string[] names) =>
        Assert.Equal(names, element.EnumerateObject().Select(property => property.Name));

    private static void AddUnknownProperty(JsonObject root, string level)
    {
        var target = level switch
        {
            "root" => root,
            "arm" => root["arms"]![0]!.AsObject(),
            "metric" => root["arms"]![0]!["metrics"]![0]!.AsObject(),
            _ => root["costFigures"]![0]!.AsObject()
        };
        target["unknown"] = true;
    }

    private static byte[] Export((FourArmEvaluationResult Run, EvaluationScoringReport Scoring, EvaluationCostReport Costs) input)
    {
        using var output = new MemoryStream();
        EvaluationJsonExporter.Write(output, input.Run, input.Scoring, input.Costs);
        return output.ToArray();
    }

    private static (FourArmEvaluationResult Run, EvaluationScoringReport Scoring, EvaluationCostReport Costs) Inputs(
        bool reverse = false, bool qualified = false, bool incomplete = false, string? label = "derived",
        string? metricName = null, string caseId = "EVAL-V2-01", string deploymentName = "demo-b",
        string costArm = "A", string stopCaseId = "EVAL-V2-02")
    {
        var configurations = new Dictionary<EvaluationArm, EvaluationEffectiveConfiguration?>
        {
            [EvaluationArm.A] = null,
            [EvaluationArm.B] = new("v1", "B", deploymentName, "model-b", "2025-01", "west", "standard"),
            [EvaluationArm.C] = new("v1", "C", "demo-c", "model-c", "2025-01", "west", "standard"),
            [EvaluationArm.D] = new("v1", "D", "demo-b", "model-b", "2025-01", "west", "standard")
        };
        EvaluationCaseResult Case(params EvaluationArm[] arms) =>
            new(caseId, arms.Select(arm => new EvaluationDispatch(arm, "case-input-canary", 1,
                [new("source-document-canary prompt-canary", "completion-canary", "request-payload-canary")], null)).ToArray());
        var cases = incomplete
            ? new[] { Case(EvaluationArm.A, EvaluationArm.B), Case(EvaluationArm.A) }
            : new[] { Case(EvaluationArm.A, EvaluationArm.B, EvaluationArm.C, EvaluationArm.D) };
        var run = new FourArmEvaluationResult(new string('a', 64), configurations, cases, 4,
            incomplete ? new(EvaluationArm.C, stopCaseId, 10, 20) : null);
        var names = reverse ? MetricNames.Reverse().ToArray() : MetricNames;
        var scores = Enum.GetValues<EvaluationArm>().Select(arm => new EvaluationArmScore(arm.ToString(),
            new(1, null, label!), new(1, null, label!), new(0, null, label!), [],
            names.ToDictionary(name => name == MetricNames[0] && metricName is not null ? metricName : name,
                name => new EvaluationMetricReport(new(null, "not evaluated", label!), new(0, null, label!),
                    new(1, null, label!), null, false)), qualified)).ToArray();
        var scoring = new EvaluationScoringReport(run.SuiteSha256, scores,
            incomplete ? new("C", stopCaseId, new(10, null, label!), new(20, null, label!)) : null, "A", qualified);
        var costs = new EvaluationCostReport(
            Enum.GetValues<EvaluationArm>().Reverse().Select(arm => new EvaluationArmCostReport(arm == EvaluationArm.A ? costArm : arm.ToString(),
                new(1m, null, label!), new(2m, null, label!))).ToArray(),
            new(3m, null, "derived"));
        return (run, scoring, costs);
    }
}
