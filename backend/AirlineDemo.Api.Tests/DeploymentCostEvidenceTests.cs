using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Evaluation;

namespace AirlineDemo.Api.Tests;

public sealed class DeploymentCostEvidenceTests
{
    [Fact]
    public void RequiresExactVersionedRecordShape()
    {
        var json = MeasuredEvidence().ToJson();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(new[] { "schemaVersion", "repositoryRevision", "deploymentId", "observations" },
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(JsonValueKind.Number, root.GetProperty("schemaVersion").ValueKind);
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Matches("^[0-9a-f]{40}$", root.GetProperty("repositoryRevision").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("deploymentId").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("observations").ValueKind);

        var observation = root.GetProperty("observations")[0];
        Assert.Equal(new[]
        {
            "scope", "category", "plannedAmount", "observedAmount", "varianceAmount", "currency",
            "periodStartUtc", "periodEndUtc", "plannedEvidenceReference", "observedEvidenceReference",
            "plannedEvidenceLabel", "observedEvidenceLabel", "varianceEvidenceLabel"
        }, observation.EnumerateObject().Select(property => property.Name));
        Assert.Equal(JsonValueKind.Number, observation.GetProperty("plannedAmount").ValueKind);
        Assert.Equal(JsonValueKind.Number, observation.GetProperty("observedAmount").ValueKind);
        Assert.Equal(JsonValueKind.Number, observation.GetProperty("varianceAmount").ValueKind);
        Assert.All(observation.EnumerateObject().Where(property =>
                property.Name is not "plannedAmount" and not "observedAmount" and not "varianceAmount"),
            property => Assert.Equal(JsonValueKind.String, property.Value.ValueKind));
        Assert.NotNull(DeploymentCostEvidence.Parse(json));

        var checkedIn = DeploymentCostEvidence.Parse(File.ReadAllText(Path.Combine(
            FindRepoRoot(), "docs", "evidence", "aircraft-return-readiness-costs.json")));
        Assert.Equal("not measured", checkedIn.DeploymentId);
        Assert.All(checkedIn.Observations, item =>
        {
            Assert.Equal(DeploymentCostAmount.NotMeasured, item.PlannedAmount);
            Assert.Equal(DeploymentCostAmount.NotMeasured, item.ObservedAmount);
            Assert.Equal(DeploymentCostAmount.NotMeasured, item.VarianceAmount);
        });

        var missingRootField = JsonNode.Parse(json)!;
        missingRootField.AsObject().Remove("deploymentId");
        Assert.Throws<JsonException>(() => DeploymentCostEvidence.Parse(missingRootField.ToJsonString()));
        var unknownRootField = JsonNode.Parse(json)!;
        unknownRootField["unexpected"] = "value";
        Assert.Throws<JsonException>(() => DeploymentCostEvidence.Parse(unknownRootField.ToJsonString()));
        var wrongAmountType = JsonNode.Parse(json)!;
        wrongAmountType["observations"]![0]!["plannedAmount"] = true;
        Assert.Throws<JsonException>(() => DeploymentCostEvidence.Parse(wrongAmountType.ToJsonString()));
        var wrongStringType = JsonNode.Parse(json)!;
        wrongStringType["observations"]![0]!["scope"] = 1;
        Assert.Throws<JsonException>(() => DeploymentCostEvidence.Parse(wrongStringType.ToJsonString()));
        var missingField = JsonNode.Parse(json)!;
        missingField["observations"]![0]!.AsObject().Remove("category");
        Assert.Throws<JsonException>(() => DeploymentCostEvidence.Parse(missingField.ToJsonString()));
        var unknownField = JsonNode.Parse(json)!;
        unknownField["observations"]![0]!["unexpected"] = "value";
        Assert.Throws<JsonException>(() => DeploymentCostEvidence.Parse(unknownField.ToJsonString()));
        var unsupportedVersion = json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => DeploymentCostEvidence.Parse(unsupportedVersion));
        var longRevision = MeasuredEvidence() with { RepositoryRevision = new string('b', 64) };
        Assert.NotNull(DeploymentCostEvidence.Parse(longRevision.ToJson()));
        var invalidRevision = MeasuredEvidence() with { RepositoryRevision = new string('A', 40) };
        Assert.Throws<ArgumentException>(() => invalidRevision.Validate());
    }

    [Fact]
    public void ValidatesEvidenceLabelsAndAmounts()
    {
        var evidence = MeasuredEvidence();
        var observation = evidence.Observations[0];
        var plannedLabelNotMeasured = evidence with
        {
            Observations = [observation with { PlannedEvidenceLabel = "not measured" }]
        };
        Assert.Throws<ArgumentException>(() => plannedLabelNotMeasured.Validate());
        var wrongObservedLabel = evidence with
        {
            Observations = [observation with { ObservedEvidenceLabel = "synthetic demonstration result" }]
        };
        Assert.Throws<ArgumentException>(() => wrongObservedLabel.Validate());
        var missingPlannedReference = evidence with
        {
            Observations = [observation with { PlannedEvidenceReference = "" }]
        };
        Assert.Throws<ArgumentException>(() => missingPlannedReference.Validate());
        var unmeasuredWithLabel = UnmeasuredEvidence() with
        {
            Observations = [UnmeasuredEvidence().Observations[0] with { PlannedEvidenceLabel = "derived" }]
        };
        Assert.Throws<ArgumentException>(() => unmeasuredWithLabel.Validate());
    }

    [Fact]
    public void CalculatesExactVarianceForMatchingBasis()
    {
        Assert.Equal(25m, DeploymentCostEvidence.CalculateVariance(
            DeploymentCostAmount.Measured(100m), DeploymentCostAmount.Measured(125m)).Value);
        Assert.Equal(-25m, DeploymentCostEvidence.CalculateVariance(
            DeploymentCostAmount.Measured(100m), DeploymentCostAmount.Measured(75m)).Value);
        Assert.Equal(DeploymentCostAmount.NotMeasured, DeploymentCostEvidence.CalculateVariance(
            DeploymentCostAmount.NotMeasured, DeploymentCostAmount.Measured(125m)));
        var wrongVariance = MeasuredEvidence() with
        {
            Observations = [MeasuredEvidence().Observations[0] with { VarianceAmount = DeploymentCostAmount.Measured(24m) }]
        };
        Assert.Throws<ArgumentException>(() => wrongVariance.Validate());
    }

    [Fact]
    public void DoesNotInventMissingCostEvidence()
    {
        var evidence = UnmeasuredEvidence();
        var observation = evidence.Observations[0];
        Assert.All(new[]
        {
            evidence with { Observations = [observation with { PlannedAmount = DeploymentCostAmount.Measured(0m) }] },
            evidence with { Observations = [observation with { ObservedAmount = DeploymentCostAmount.Measured(0m) }] },
            evidence with { Observations = [observation with { VarianceAmount = DeploymentCostAmount.Measured(0m) }] }
        }, candidate => Assert.Throws<ArgumentException>(() => candidate.Validate()));
        Assert.Contains("\"plannedAmount\":\"not measured\"", evidence.ToJson());
        Assert.Contains("\"observedAmount\":\"not measured\"", evidence.ToJson());
        Assert.Contains("\"varianceAmount\":\"not measured\"", evidence.ToJson());
    }

    private static DeploymentCostEvidence MeasuredEvidence() => new(
        1, new string('a', 40), "synthetic-test-deployment",
        [
            new DeploymentCostObservation(
                "test scope", "compute", DeploymentCostAmount.Measured(100m), DeploymentCostAmount.Measured(125m),
                DeploymentCostAmount.Measured(25m), "EUR", "2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z",
                "test:planned", "test:observed", "scenario assumption", "external evidence", "derived")
        ]);

    private static DeploymentCostEvidence UnmeasuredEvidence() => new(
        1, new string('a', 40), "not measured",
        [
            new DeploymentCostObservation(
                "not measured", "not measured", DeploymentCostAmount.NotMeasured, DeploymentCostAmount.NotMeasured,
                DeploymentCostAmount.NotMeasured, "not measured", "not measured", "not measured",
                "not measured", "not measured", "not measured", "not measured", "not measured")
        ]);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AirlineDemo.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
