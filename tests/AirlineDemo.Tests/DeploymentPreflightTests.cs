using System.Text.Json;

namespace AirlineDemo.Tests;

public sealed class DeploymentPreflightTests
{
    [Fact]
    public void PreflightRecord_PublishesNonSecretContainerAppsAndRegistryBaseline()
    {
        using var preflight = LoadJson("deployment", "preflight.json");
        var root = preflight.RootElement;

        var destination = root.GetProperty("destination");
        Assert.Equal("swedencentral", destination.GetProperty("region").GetString());
        Assert.Equal("airlinedemo-swc-demo", destination.GetProperty("deploymentName").GetString());
        Assert.Equal("rg-airlinedemo-swc-demo", destination.GetProperty("resourceGroupName").GetString());
        Assert.False(destination.GetProperty("subscription").GetProperty("committed").GetBoolean());
        Assert.False(destination.GetProperty("tenant").GetProperty("committed").GetBoolean());

        Assert.Equal("planning-only", root.GetProperty("status").GetString());
        var authorization = root.GetProperty("authorization");
        Assert.False(authorization.GetProperty("provisioningAuthorized").GetBoolean());
        Assert.False(authorization.GetProperty("spendingAuthorized").GetBoolean());

        var application = root.GetProperty("application");
        Assert.Equal("ASP.NET Core", application.GetProperty("runtime").GetString());
        Assert.Equal("net10.0", application.GetProperty("targetFramework").GetString());
        var image = application.GetProperty("containerImage");
        Assert.Equal("mcr.microsoft.com/dotnet/aspnet:10.0", image.GetProperty("baseImage").GetString());
        Assert.Equal("acrairlinedemoswcdemo.azurecr.io/airlinedemo:net10.0",
            image.GetProperty("applicationImage").GetString());

        var hosting = application.GetProperty("hosting");
        Assert.Equal("Azure Container Apps", hosting.GetProperty("service").GetString());
        Assert.Equal("Consumption", hosting.GetProperty("plan").GetString());
        Assert.True(hosting.GetProperty("scaleToZero").GetBoolean());
        Assert.Equal(0, hosting.GetProperty("minReplicas").GetInt32());
        Assert.Equal(1, hosting.GetProperty("maxReplicas").GetInt32());

        var registry = application.GetProperty("containerRegistry");
        Assert.Equal("Azure Container Registry", registry.GetProperty("service").GetString());
        Assert.Equal("Basic", registry.GetProperty("sku").GetString());
        Assert.False(application.TryGetProperty("workerModel", out _));
        Assert.False(application.TryGetProperty("durableFunctions", out _));
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
    public void PackageLock_ContainsTheContainerImageHostingContract()
    {
        using var lockFile = LoadJson("packages.lock.json");
        var packages = lockFile.RootElement.GetProperty("packages");

        Assert.Equal("net10.0", lockFile.RootElement.GetProperty("targetFramework").GetString());
        Assert.Equal(JsonValueKind.Object, packages.ValueKind);
        Assert.Empty(packages.EnumerateObject());
        Assert.Equal("ASP.NET Core .NET 10 container image",
            lockFile.RootElement.GetProperty("runtime").GetString());
        Assert.Equal("mcr.microsoft.com/dotnet/aspnet:10.0",
            lockFile.RootElement.GetProperty("containerImage").GetString());
        var compatibility = lockFile.RootElement.GetProperty("compatibility");
        Assert.Equal("Azure Container Apps", compatibility.GetProperty("hostingService").GetString());
        Assert.Equal("Consumption", compatibility.GetProperty("hostingPlan").GetString());
        Assert.True(compatibility.GetProperty("scaleToZero").GetBoolean());
        Assert.Equal("Azure Container Registry",
            compatibility.GetProperty("containerRegistry").GetString());
        Assert.Equal("Basic", compatibility.GetProperty("containerRegistrySku").GetString());
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
