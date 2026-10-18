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
        var wrongVarianceLabel = evidence with
        {
            Observations = [observation with { VarianceEvidenceLabel = "external evidence" }]
        };
        Assert.Throws<ArgumentException>(() => wrongVarianceLabel.Validate());
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

    private static readonly string[] Scopes = ["A", "B", "C", "D", "transition", "deployment"];
    private static readonly string[] Categories =
        ["cost-per-page", "cost-per-case", "transition-cost", "active", "idle", "residual"];
    private static readonly string[] PlannedLabels =
    [
        "case-study stipulation", "external evidence", "scenario assumption", "architectural decision",
        "synthetic demonstration result", "unvalidated target", "derived"
    ];

    private static void AssertContract(DeploymentCostObservation observation, bool accepted)
    {
        var evidence = MeasuredEvidence() with { Observations = [observation] };
        if (accepted)
            evidence.Validate();
        else
            Assert.ThrowsAny<ArgumentException>(evidence.Validate);
    }

    private static DeploymentCostObservation Observation(
        bool planned, bool observed, bool variance, string label = "scenario assumption") =>
        MeasuredEvidence().Observations[0] with
        {
            Scope = "A",
            Category = "active",
            PlannedAmount = planned ? DeploymentCostAmount.Measured(100m) : DeploymentCostAmount.NotMeasured,
            ObservedAmount = observed ? DeploymentCostAmount.Measured(125m) : DeploymentCostAmount.NotMeasured,
            VarianceAmount = variance ? DeploymentCostAmount.Measured(25m) : DeploymentCostAmount.NotMeasured,
            PlannedEvidenceReference = planned ? "test:planned" : "not measured",
            ObservedEvidenceReference = observed ? "test:observed" : "not measured",
            PlannedEvidenceLabel = planned ? label : "not measured",
            ObservedEvidenceLabel = observed ? "external evidence" : "not measured",
            VarianceEvidenceLabel = variance ? "derived" : "not measured",
            Currency = planned || observed || variance ? "EUR" : "not measured"
        };

    [Fact]
    public void AcceptsOnlyListedScopesCategoriesAndFields()
    {
        var valid = Observation(true, true, true);

        foreach (var scope in Scopes)
            AssertContract(valid with { Scope = scope }, true);
        foreach (var scope in new[] { "E", "a", "Transition", "global", "" })
            AssertContract(valid with { Scope = scope }, false);

        foreach (var category in Categories)
            AssertContract(valid with { Category = category }, true);
        foreach (var category in new[] { "compute", "Active", "cost-per-token", "" })
            AssertContract(valid with { Category = category }, false);

        foreach (var label in PlannedLabels)
            AssertContract(Observation(true, true, true, label), true);
        AssertContract(Observation(true, true, true, "invented label"), false);
        AssertContract(Observation(true, true, true, "not measured"), false);
        AssertContract(Observation(false, true, false) with { PlannedEvidenceLabel = "not measured" }, true);
        AssertContract(Observation(false, true, false) with { PlannedEvidenceLabel = "derived" }, false);

        foreach (var currency in new[] { "EUR", "USD", "GBP" })
            AssertContract(valid with { Currency = currency }, true);
        foreach (var currency in new[] { "eur", "Eur", "EU", "EURO", "E1R", "€", "", "not measured" })
            AssertContract(valid with { Currency = currency }, false);

        AssertContract(valid with { PeriodStartUtc = "2026-01-01T00:00:00Z", PeriodEndUtc = "2026-01-01T00:00:01Z" }, true);
        foreach (var start in new[] { "2026-01-01T00:00:00+02:00", "2026-01-01T00:00:00", "2026-01-01" })
            AssertContract(valid with { PeriodStartUtc = start }, false);
        AssertContract(valid with { PeriodEndUtc = "2026-01-02T00:00:00+00:00" }, false);
        AssertContract(valid with { PeriodEndUtc = valid.PeriodStartUtc }, false);
        AssertContract(valid with { PeriodEndUtc = "2025-12-31T23:59:59Z" }, false);

        // planned, observed, variance measured flags: variance needs both inputs measured.
        foreach (var (planned, observed, variance, accepted) in new[]
                 {
                     (true, true, true, true), (true, false, false, true), (false, true, false, true),
                     (false, false, false, true), (true, true, false, false), (true, false, true, false),
                     (false, true, true, false), (false, false, true, false)
                 })
            AssertContract(Observation(planned, observed, variance), accepted);

        // Currency is "not measured" if and only if all three amounts are not measured.
        AssertContract(Observation(false, false, false), true);
        AssertContract(Observation(false, false, false) with { Currency = "EUR" }, false);
        AssertContract(Observation(true, false, false) with { Currency = "not measured" }, false);

        // Labels and references per amount.
        AssertContract(valid with { ObservedEvidenceLabel = "synthetic demonstration result" }, false);
        AssertContract(valid with { VarianceEvidenceLabel = "external evidence" }, false);
        AssertContract(valid with { VarianceEvidenceLabel = "not measured" }, false);
        AssertContract(valid with { PlannedEvidenceReference = "" }, false);
        AssertContract(valid with { ObservedEvidenceReference = " " }, false);
        AssertContract(Observation(false, true, false) with { PlannedEvidenceReference = "test:planned" }, false);
        AssertContract(Observation(true, false, false) with { ObservedEvidenceLabel = "external evidence" }, false);
        AssertContract(Observation(true, true, false), false);
    }

    private static DeploymentCostEvidence MeasuredEvidence() => new(
        1, new string('a', 40), "synthetic-test-deployment",
        [
            new DeploymentCostObservation(
                "A", "active", DeploymentCostAmount.Measured(100m), DeploymentCostAmount.Measured(125m),
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
