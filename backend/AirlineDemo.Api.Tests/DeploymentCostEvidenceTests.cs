using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Evaluation;

namespace AirlineDemo.Api.Tests;

public sealed class DeploymentCostEvidenceTests
{
    private static readonly string Root = FindRepoRoot();
    private static readonly string EvidencePath = Path.Combine(Root, "docs", "evidence", "aircraft-return-readiness-costs.json");
    private static readonly string[] Scopes = ["A", "B", "C", "D", "transition", "deployment"];
    private static readonly string[] Categories =
        ["cost-per-page", "cost-per-case", "transition-cost", "active", "idle", "residual"];
    private static readonly string[] PlannedLabels =
    [
        "case-study stipulation", "external evidence", "scenario assumption", "architectural decision",
        "synthetic demonstration result", "unvalidated target", "derived"
    ];
    private static readonly string[] ObservationFields =
    [
        "scope", "category", "plannedAmount", "observedAmount", "varianceAmount", "currency",
        "periodStartUtc", "periodEndUtc", "plannedEvidenceReference", "observedEvidenceReference",
        "plannedEvidenceLabel", "observedEvidenceLabel", "varianceEvidenceLabel"
    ];

    [Fact]
    public void RequiresExactVersionedRecordShape()
    {
        var checkedIn = File.ReadAllBytes(EvidencePath);
        DeploymentCostEvidence.ValidateJson(checkedIn);
        var valid = Record(Observation());
        AssertValid(valid);
        var valid64CharacterRevision = JsonNode.Parse(valid)!.AsObject();
        valid64CharacterRevision["repositoryRevision"] = new string('b', 64);
        AssertValid(valid64CharacterRevision.ToJsonString());

        foreach (var field in new[] { "schemaVersion", "repositoryRevision", "deploymentId", "observations" })
        {
            var missing = JsonNode.Parse(valid)!.AsObject();
            missing.Remove(field);
            AssertInvalid(missing.ToJsonString());
        }

        foreach (var mutation in new Action<JsonObject>[]
        {
            root => root["unknown"] = true,
            root => root["schemaVersion"] = "1",
            root => root["repositoryRevision"] = 42,
            root => root["deploymentId"] = 7,
            root => root["observations"] = new JsonObject()
        })
        {
            AssertInvalid(MutateRoot(valid, mutation));
        }

        foreach (var revision in new[]
        {
            new string('A', 40), new string('g', 40), new string('a', 39), new string('a', 41)
        })
        {
            var invalidRevision = JsonNode.Parse(valid)!.AsObject();
            invalidRevision["repositoryRevision"] = revision;
            AssertInvalid(invalidRevision.ToJsonString());
        }
        var emptyDeploymentId = JsonNode.Parse(valid)!.AsObject();
        emptyDeploymentId["deploymentId"] = "";
        AssertInvalid(emptyDeploymentId.ToJsonString());

        var unsupportedVersion = JsonNode.Parse(valid)!.AsObject();
        unsupportedVersion["schemaVersion"] = 2;
        AssertInvalid(unsupportedVersion.ToJsonString());
        AssertInvalid(valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1.0", StringComparison.Ordinal));

        foreach (var field in ObservationFields)
        {
            var missing = JsonNode.Parse(valid)!.AsObject();
            missing["observations"]![0]!.AsObject().Remove(field);
            AssertInvalid(missing.ToJsonString());

            var incorrectlyTyped = JsonNode.Parse(valid)!.AsObject();
            incorrectlyTyped["observations"]![0]![field] = true;
            AssertInvalid(incorrectlyTyped.ToJsonString());
        }

        var unknownObservationField = JsonNode.Parse(valid)!.AsObject();
        unknownObservationField["observations"]![0]!["unknown"] = true;
        AssertInvalid(unknownObservationField.ToJsonString());
        foreach (var amountField in new[] { "plannedAmount", "observedAmount", "varianceAmount" })
        {
            var incorrectlySpelledUnmeasured = JsonNode.Parse(valid)!.AsObject();
            incorrectlySpelledUnmeasured["observations"]![0]![amountField] = "Not measured";
            AssertInvalid(incorrectlySpelledUnmeasured.ToJsonString());
        }
    }

    [Fact]
    public void AcceptsOnlyListedScopesCategoriesAndFields()
    {
        foreach (var scope in Scopes)
        {
            var item = Observation();
            item["scope"] = scope;
            AssertValid(Record(item));
        }
        RejectField("scope", "E");

        foreach (var category in Categories)
        {
            var item = Observation();
            item["category"] = category;
            AssertValid(Record(item));
        }
        RejectField("category", "unlisted");

        foreach (var label in PlannedLabels)
        {
            var item = Measured(planned: 100m, observed: null, variance: null);
            item["plannedEvidenceLabel"] = label;
            AssertValid(Record(item));
        }
        RejectField("plannedEvidenceLabel", "unlisted");

        foreach (var currency in new[] { "EU", "eur", "ZZZ" })
        {
            var item = Measured(planned: 100m, observed: null, variance: null);
            item["currency"] = currency;
            AssertInvalid(Record(item));
        }

        foreach (var timestamp in new[] { "2026-01-01T00:00:00+01:00", "2026-01-01T00:00:00" })
        {
            var item = Observation();
            item["periodStartUtc"] = timestamp;
            AssertInvalid(Record(item));
        }

        var equalPeriod = Observation();
        equalPeriod["periodEndUtc"] = "2026-01-01T00:00:00Z";
        AssertInvalid(Record(equalPeriod));
        var reversedPeriod = Observation();
        reversedPeriod["periodEndUtc"] = "2025-12-31T23:59:59Z";
        AssertInvalid(Record(reversedPeriod));

        foreach (var (field, number, referenceField) in new[]
        {
            ("plannedAmount", 100m, "plannedEvidenceReference"),
            ("observedAmount", 100m, "observedEvidenceReference")
        })
        {
            var missingEvidence = Measured(planned: null, observed: null, variance: null);
            missingEvidence[field] = number;
            AssertInvalid(Record(missingEvidence));

            var missingReference = Measured(planned: 100m, observed: null, variance: null);
            if (field == "observedAmount")
                missingReference = Measured(planned: null, observed: 100m, variance: null);
            missingReference[referenceField] = "not measured";
            AssertInvalid(Record(missingReference));
        }

        var observedWrongLabel = Measured(planned: null, observed: 100m, variance: null);
        observedWrongLabel["observedEvidenceLabel"] = "derived";
        AssertInvalid(Record(observedWrongLabel));
        var varianceWrongLabel = Measured(planned: 100m, observed: 125m, variance: 25m);
        varianceWrongLabel["varianceEvidenceLabel"] = "external evidence";
        AssertInvalid(Record(varianceWrongLabel));

        var observedOnly = Measured(planned: null, observed: 125m, variance: null);
        AssertValid(Record(observedOnly));
        var plannedOnly = Measured(planned: 100m, observed: null, variance: null);
        AssertValid(Record(plannedOnly));
        var bothMeasured = Measured(planned: 100m, observed: 125m, variance: 25m);
        AssertValid(Record(bothMeasured));

        var unmeasuredCurrency = Measured(planned: 100m, observed: null, variance: null);
        unmeasuredCurrency["currency"] = "not measured";
        AssertInvalid(Record(unmeasuredCurrency));
        var measuredCurrencyWithoutAmounts = Observation();
        measuredCurrencyWithoutAmounts["currency"] = "EUR";
        AssertInvalid(Record(measuredCurrencyWithoutAmounts));

        var unmeasuredReference = Observation();
        unmeasuredReference["plannedEvidenceReference"] = "";
        AssertInvalid(Record(unmeasuredReference));
    }

    [Fact]
    public void CalculatesExactVarianceForMatchingBasis()
    {
        Assert.Equal(25m, DeploymentCostEvidence.CalculateVariance(100m, 125m));
        Assert.Equal(-25m, DeploymentCostEvidence.CalculateVariance(100m, 75m));
        AssertValid(Record(Measured(planned: 100m, observed: 125m, variance: 25m)));
        AssertValid(Record(Measured(planned: 100m, observed: 75m, variance: -25m)));

        var incorrectVariance = Measured(planned: 100m, observed: 125m, variance: 24m);
        AssertInvalid(Record(incorrectVariance));

        var missingPlanned = Measured(planned: null, observed: 125m, variance: 0m);
        AssertInvalid(Record(missingPlanned));
        var missingObserved = Measured(planned: 100m, observed: null, variance: 0m);
        AssertInvalid(Record(missingObserved));
    }

    [Fact]
    public void DoesNotInventMissingCostEvidence()
    {
        var checkedIn = JsonNode.Parse(File.ReadAllText(EvidencePath))!.AsObject();
        Assert.Empty(checkedIn["observations"]!.AsArray());

        var unmeasuredRecord = Record(Observation());
        AssertValid(unmeasuredRecord);
        foreach (var (field, substitute) in new[]
        {
            ("plannedAmount", 0m), ("observedAmount", 250000m), ("varianceAmount", 8200m)
        })
        {
            var invented = JsonNode.Parse(unmeasuredRecord)!.AsObject();
            invented["observations"]![0]![field] = substitute;
            AssertInvalid(invented.ToJsonString());
        }
    }

    private static JsonObject Observation() => new()
    {
        ["scope"] = "deployment",
        ["category"] = "active",
        ["plannedAmount"] = "not measured",
        ["observedAmount"] = "not measured",
        ["varianceAmount"] = "not measured",
        ["currency"] = "not measured",
        ["periodStartUtc"] = "2026-01-01T00:00:00Z",
        ["periodEndUtc"] = "2026-02-01T00:00:00Z",
        ["plannedEvidenceReference"] = "not measured",
        ["observedEvidenceReference"] = "not measured",
        ["plannedEvidenceLabel"] = "not measured",
        ["observedEvidenceLabel"] = "not measured",
        ["varianceEvidenceLabel"] = "not measured"
    };

    private static JsonObject Measured(decimal? planned, decimal? observed, decimal? variance)
    {
        var item = Observation();
        item["plannedAmount"] = planned is null ? "not measured" : JsonValue.Create(planned);
        item["observedAmount"] = observed is null ? "not measured" : JsonValue.Create(observed);
        item["varianceAmount"] = variance is null ? "not measured" : JsonValue.Create(variance);
        item["currency"] = planned is null && observed is null && variance is null ? "not measured" : "EUR";
        item["plannedEvidenceReference"] = planned is null ? "not measured" : "planned-source";
        item["observedEvidenceReference"] = observed is null ? "not measured" : "observed-source";
        item["plannedEvidenceLabel"] = planned is null ? "not measured" : "derived";
        item["observedEvidenceLabel"] = observed is null ? "not measured" : "external evidence";
        item["varianceEvidenceLabel"] = variance is null ? "not measured" : "derived";
        return item;
    }

    private static string Record(JsonObject observation) => new JsonObject
    {
        ["schemaVersion"] = 1,
        ["repositoryRevision"] = new string('a', 40),
        ["deploymentId"] = "test-deployment",
        ["observations"] = new JsonArray(observation)
    }.ToJsonString();

    private static string MutateRoot(string json, Action<JsonObject> mutation)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        mutation(root);
        return root.ToJsonString();
    }

    private static void RejectField(string field, string value)
    {
        var item = Observation();
        item[field] = value;
        AssertInvalid(Record(item));
    }

    private static void AssertValid(string json) => DeploymentCostEvidence.ValidateJson(System.Text.Encoding.UTF8.GetBytes(json));

    private static void AssertInvalid(string json) =>
        Assert.Throws<JsonException>(() => DeploymentCostEvidence.ValidateJson(System.Text.Encoding.UTF8.GetBytes(json)));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".github", "workflows", "build.yml")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
