using System.Text.Json;

namespace AirlineDemo.Tests;

public sealed class DeploymentCostModelTests
{
    [Fact]
    public void CostModel_PublishesSwedenCentralSourcesAssumptionsAndBoundedTotal()
    {
        using var model = LoadJson("deployment", "cost-model.json");
        var root = model.RootElement;

        Assert.Equal("swedencentral", root.GetProperty("region").GetString());
        Assert.Equal("2026-09-11", root.GetProperty("asOf").GetString());
        Assert.Equal("planning-only", root.GetProperty("status").GetString());

        var authorization = root.GetProperty("authorization");
        Assert.False(authorization.GetProperty("provisioningAuthorized").GetBoolean());
        Assert.False(authorization.GetProperty("spendingAuthorized").GetBoolean());
        Assert.False(authorization.GetProperty("deploymentApplied").GetBoolean());

        var assumptions = root.GetProperty("usageAssumptions");
        Assert.Equal(12, assumptions.GetProperty("rehearsalsPerMonth").GetInt32());
        Assert.Equal(48, assumptions.GetProperty("pagesPerPackage").GetInt32());
        Assert.Equal(1, assumptions.GetProperty("maxDocumentRetries").GetInt32());
        Assert.Equal(1152, assumptions.GetProperty("worstCaseDocumentIntelligencePages").GetInt32());
        Assert.Equal(90, assumptions.GetProperty("retentionDays")
            .GetProperty("evidenceBlobVersions").GetInt32());
        Assert.Equal(90, assumptions.GetProperty("retentionDays")
            .GetProperty("applicationInsightsAndLogAnalytics").GetInt32());
        Assert.Equal(30000, assumptions.GetProperty("model")
            .GetProperty("quotaTokensPerMinute").GetInt32());

        var requiredLineItems = new[]
        {
            "functions-hosting",
            "durable-backend",
            "azure-sql",
            "document-intelligence",
            "azure-openai",
            "evidence-storage",
            "monitoring",
            "networking",
            "remote-terraform-state"
        };
        var lineItems = root.GetProperty("lineItems").EnumerateArray().ToArray();
        Assert.Equal(requiredLineItems.Length, lineItems.Length);
        using var snapshot = LoadJson("deployment",
            "pricing-sweden-central-2026-09-11.json");
        Assert.Equal("2026-09-11T19:36:34Z",
            snapshot.RootElement.GetProperty("capturedAt").GetString());
        Assert.Equal("swedencentral", snapshot.RootElement.GetProperty("region").GetString());
        var meters = snapshot.RootElement.GetProperty("meters")
            .EnumerateArray()
            .ToDictionary(meter => meter.GetProperty("id").GetString()!);
        Assert.All(lineItems, item =>
        {
            Assert.StartsWith("https://azure.microsoft.com/",
                item.GetProperty("sourceUrl").GetString()!,
                StringComparison.Ordinal);
            Assert.True(item.GetProperty("subtotalUsd").GetDecimal() >= 0);
            var evidence = item.GetProperty("pricingEvidence").EnumerateArray()
                .Select(reference => reference.GetString()!)
                .ToArray();
            Assert.NotEmpty(evidence);
            Assert.All(evidence, reference => Assert.True(meters.ContainsKey(reference)));
        });
        Assert.Equal(requiredLineItems,
            lineItems.Select(item => item.GetProperty("id").GetString()).ToArray());

        var totals = root.GetProperty("totals");
        var calculatedSubtotal = lineItems.Sum(item => item.GetProperty("subtotalUsd").GetDecimal());
        Assert.Equal(totals.GetProperty("monthlyEstimateUsd").GetDecimal(), calculatedSubtotal);
        Assert.Equal(
            totals.GetProperty("monthlyEstimateUsd").GetDecimal()
                + totals.GetProperty("contingencyUsd").GetDecimal(),
            totals.GetProperty("totalWithContingencyUsd").GetDecimal());
        Assert.True(totals.GetProperty("totalWithContingencyUsd").GetDecimal()
            < totals.GetProperty("planningCeilingUsd").GetDecimal());
        Assert.True(totals.GetProperty("withinPlanningCeiling").GetBoolean());
    }

    [Fact]
    public void BudgetProcedure_DefinesNotificationsActionsResidualsAndOwnership()
    {
        var procedure = LoadText("deployment", "budget-response-procedure.md");

        Assert.Contains("USD 250 (50%)", procedure, StringComparison.Ordinal);
        Assert.Contains("USD 400 (80%)", procedure, StringComparison.Ordinal);
        Assert.Contains("USD 450 (90%)", procedure, StringComparison.Ordinal);
        Assert.Contains("USD 500 (100%)", procedure, StringComparison.Ordinal);
        Assert.Contains("alerts are notifications only", procedure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stops new rehearsals", procedure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stops the Function App", procedure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("remote Terraform state backend", procedure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Retained evidence blobs and document versions", procedure,
            StringComparison.Ordinal);
        Assert.Contains("Azure SQL data files and backup storage", procedure,
            StringComparison.Ordinal);
        Assert.Contains("Application Insights and Log Analytics", procedure,
            StringComparison.Ordinal);
        Assert.Contains("disposable demo scope", procedure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("externally owned backend", procedure,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not configure Azure Cost Management budgets or alerts", procedure,
            StringComparison.OrdinalIgnoreCase);
    }

    private static JsonDocument LoadJson(params string[] relativePath)
    {
        return JsonDocument.Parse(LoadText(relativePath));
    }

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

        throw new FileNotFoundException($"Could not find {Path.Combine(relativePath)}.");
    }
}
