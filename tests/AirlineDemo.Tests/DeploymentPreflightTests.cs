using System.Text.Json;

namespace AirlineDemo.Tests;

public sealed class DeploymentPreflightTests
{
    [Fact]
    public void PreflightRecord_PublishesNonSecretDestinationAndHostingBaseline()
    {
        using var preflight = LoadJson("deployment", "preflight.json");
        var root = preflight.RootElement;

        var destination = root.GetProperty("destination");
        Assert.Equal("swedencentral", destination.GetProperty("region").GetString());
        Assert.Equal("airlinedemo-swc-demo", destination.GetProperty("deploymentName").GetString());
        Assert.Equal("rg-airlinedemo-swc-demo", destination.GetProperty("resourceGroupName").GetString());
        Assert.False(destination.GetProperty("subscription").GetProperty("committed").GetBoolean());
        Assert.False(destination.GetProperty("tenant").GetProperty("committed").GetBoolean());

        var application = root.GetProperty("application");
        Assert.Equal("Azure Functions v4", application.GetProperty("runtime").GetString());
        Assert.Equal("dotnet-isolated", application.GetProperty("workerModel").GetString());
        Assert.Equal("net10.0", application.GetProperty("targetFramework").GetString());
        Assert.Equal("Flex Consumption",
            application.GetProperty("hosting").GetProperty("plan").GetString());
        Assert.Equal("Azure Storage",
            application.GetProperty("durableFunctions").GetProperty("backend").GetString());
        Assert.Equal("StorageV2",
            application.GetProperty("durableFunctions").GetProperty("storageAccountKind").GetString());
        Assert.Equal("packages.lock.json",
            application.GetProperty("durableFunctions").GetProperty("packageLock").GetString());
    }

    [Fact]
    public void PreflightRecord_PublishesSqlRetentionAndModelReadinessGate()
    {
        using var preflight = LoadJson("deployment", "preflight.json");
        var root = preflight.RootElement;

        var database = root.GetProperty("database");
        Assert.Equal("Serverless General Purpose", database.GetProperty("sku").GetString());
        Assert.Equal("system-assigned managed identity",
            database.GetProperty("authentication").GetString());
        Assert.Equal("public", database.GetProperty("endpoint").GetProperty("type").GetString());
        Assert.Equal("firewall-restricted",
            database.GetProperty("endpoint").GetProperty("access").GetString());

        var retention = root.GetProperty("retention");
        Assert.Equal(90, retention.GetProperty("applicationInsightsAndLogAnalyticsDays").GetInt32());
        Assert.Equal(90, retention.GetProperty("evidenceBlobVersionsDays").GetInt32());
        Assert.Equal(90, retention.GetProperty("fixtureDataDays").GetInt32());

        var model = root.GetProperty("azureOpenAI");
        Assert.Equal(JsonValueKind.Null, model.GetProperty("model").ValueKind);
        Assert.Equal(JsonValueKind.Null, model.GetProperty("modelVersion").ValueKind);
        Assert.Equal("deployment-time confirmation required",
            model.GetProperty("modelSelection").GetString());
        Assert.Equal("Standard", model.GetProperty("deploymentType").GetString());
        Assert.Equal(30000, model.GetProperty("quotaTpm").GetInt32());

        var gate = root.GetProperty("readinessGate");
        Assert.Equal("terraform apply", gate.GetProperty("mustPassBefore").GetString());
        Assert.Equal("confirmation-required", gate.GetProperty("status").GetString());
        Assert.DoesNotContain(gate.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("confirmed").GetBoolean());
        Assert.Contains("must not be silently substituted",
            gate.GetProperty("substitutionPolicy").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("neither provisioning nor spending",
            gate.GetProperty("authorization").GetString());
    }

    [Fact]
    public void PackageLock_ContainsCompatibleWorkerAndDurableTaskPackages()
    {
        using var lockFile = LoadJson("packages.lock.json");
        var packages = lockFile.RootElement.GetProperty("packages");

        Assert.Equal("net10.0", lockFile.RootElement.GetProperty("targetFramework").GetString());
        Assert.Equal("2.52.0", packages.GetProperty("Microsoft.Azure.Functions.Worker")
            .GetProperty("version").GetString());
        Assert.Equal("2.1.0", packages.GetProperty("Microsoft.Azure.Functions.Worker.Sdk")
            .GetProperty("version").GetString());
        Assert.Equal("1.19.0",
            packages.GetProperty("Microsoft.Azure.Functions.Worker.Extensions.DurableTask")
                .GetProperty("version").GetString());
    }

    private static JsonDocument LoadJson(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate))
            {
                return JsonDocument.Parse(File.ReadAllText(candidate));
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(relativePath)}.");
    }
}
