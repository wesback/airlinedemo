using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AirlineDemo.Tests;

public sealed class PresentationArtifactsTests
{
    private static readonly JsonSchema RecordingReceiptSchema = JsonSchema.FromText(
        LoadText("contracts", "1.0", "recording-receipt.schema.json"));

    private static readonly string[] RequiredReceiptFields =
    [
        "seed",
        "fixtureVersion",
        "applicationVersion",
        "captureDate",
        "syntheticDataLabel",
        "mockIntegrationLabel",
        "observedDurationSeconds",
        "fallbackUsed"
    ];

    [Fact]
    public void RecordingReceipt_ContainsEveryRequiredFieldAndValidValues()
    {
        var receipt = LoadJson("deployment", "recording-receipt.example.json");

        var result = RecordingReceiptSchema.Evaluate(receipt.RootElement);

        Assert.True(result.IsValid, result.ToString());
        foreach (var field in RequiredReceiptFields)
        {
            Assert.True(
                receipt.RootElement.TryGetProperty(field, out var value),
                $"Recording receipt is missing '{field}'.");
            Assert.NotEqual(JsonValueKind.Null, value.ValueKind);
            if (value.ValueKind == JsonValueKind.String)
            {
                Assert.False(string.IsNullOrWhiteSpace(value.GetString()));
            }
        }

        Assert.NotEqual(0, receipt.RootElement.GetProperty("seed").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(
            receipt.RootElement.GetProperty("captureDate").GetString()));
        Assert.True(receipt.RootElement.GetProperty("observedDurationSeconds").GetInt32() > 0);
        Assert.True(receipt.RootElement.GetProperty("fallbackUsed").ValueKind is
            JsonValueKind.True or JsonValueKind.False);
    }

    [Fact]
    public void RecordingReceipt_RejectsEachMissingRequiredField()
    {
        var completeReceipt = JsonNode.Parse(
            LoadText("deployment", "recording-receipt.example.json"))!.AsObject();

        foreach (var field in RequiredReceiptFields)
        {
            var missingFieldReceipt = completeReceipt.DeepClone().AsObject();
            missingFieldReceipt.Remove(field);

            using var document = JsonDocument.Parse(missingFieldReceipt.ToJsonString());
            var result = RecordingReceiptSchema.Evaluate(document.RootElement);

            Assert.False(result.IsValid, $"Receipt without '{field}' was accepted.");
        }
    }

    [Fact]
    public void PresenterScript_DefinesFourLabeledWalkthroughStages()
    {
        var script = LoadText("deployment", "presenter-script.md");

        foreach (var stage in new[]
        {
            "## Stage 1 - Case setup",
            "## Stage 2 - Missing-history mock request",
            "## Stage 3 - Ambiguous-identity internal review",
            "## Stage 4 - Version-bound history"
        })
        {
            Assert.Contains(stage, script, StringComparison.Ordinal);
        }

        for (var stageNumber = 1; stageNumber <= 4; stageNumber++)
        {
            var stageStart = script.IndexOf(
                $"## Stage {stageNumber} -", StringComparison.Ordinal);
            var nextStage = script.IndexOf(
                "## Stage ", stageStart + 1, StringComparison.Ordinal);
            var stage = script[stageStart..(nextStage < 0 ? script.Length : nextStage)];

            Assert.Contains("`SYNTHETIC DATA`", stage, StringComparison.Ordinal);
            Assert.Contains("`MOCK INTEGRATION`", stage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RehearsalProcedure_DefinesFallbackOutcomesAndClaimsBoundary()
    {
        var procedure = LoadText(
            "deployment", "rehearsal-and-recording-procedure.md");

        Assert.Contains("15 seconds", procedure, StringComparison.Ordinal);
        Assert.Contains("recording replaces live execution", procedure,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("STATIC EVIDENCE/CONTROL VIEW", procedure,
            StringComparison.Ordinal);
        Assert.Contains("five consecutive", procedure, StringComparison.OrdinalIgnoreCase);
        for (var outcome = 1; outcome <= 5; outcome++)
        {
            var rowStart = procedure.IndexOf($"| {outcome} |", StringComparison.Ordinal);
            Assert.True(rowStart >= 0, $"Missing completed outcome row {outcome}.");
            var rowEnd = procedure.IndexOf('\n', rowStart);
            var row = procedure[rowStart..(rowEnd < 0 ? procedure.Length : rowEnd)];
            var columns = row.Split('|', StringSplitOptions.TrimEntries)[1..^1];
            Assert.Equal(7, columns.Length);
            Assert.Matches(
                @"^\d{4}-\d{2}-\d{2}$",
                columns[1]);
            Assert.False(string.IsNullOrWhiteSpace(columns[2]));
            Assert.False(string.IsNullOrWhiteSpace(columns[3]));
            Assert.True(
                int.TryParse(columns[4], out var durationSeconds) &&
                durationSeconds > 0,
                $"Outcome {outcome} must contain a positive observed duration.");
            Assert.True(
                bool.TryParse(columns[5], out _),
                $"Outcome {outcome} must contain a boolean fallback-use value.");
            Assert.False(string.IsNullOrWhiteSpace(columns[6]));
        }
        Assert.Contains("durationSeconds", procedure, StringComparison.Ordinal);
        Assert.Contains("fallbackUsed", procedure, StringComparison.Ordinal);
        Assert.Contains("current live execution", procedure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("production accuracy", procedure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("regulatory compliance", procedure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("real partner response", procedure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("actual backup recording", procedure, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonDocument LoadJson(params string[] relativePath) =>
        JsonDocument.Parse(LoadText(relativePath));

    private static string LoadText(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {Path.Join(relativePath)}.");
    }
}
