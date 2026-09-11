using System.Text.RegularExpressions;

namespace AirlineDemo.Tests;

public sealed class TerraformConfigurationTests
{
    [Fact]
    public void TerraformRoot_PinsTerraformAndAzureProviderWithCheckedInLock()
    {
        var versions = LoadText("terraform", "versions.tf");
        var lockFile = LoadText("terraform", ".terraform.lock.hcl");

        Assert.Contains("required_version = \"= 1.9.8\"", versions, StringComparison.Ordinal);
        Assert.Contains("source  = \"hashicorp/azurerm\"", versions, StringComparison.Ordinal);
        Assert.Contains("version = \"= 4.62.0\"", versions, StringComparison.Ordinal);
        Assert.Contains("registry.terraform.io/hashicorp/azurerm", lockFile,
            StringComparison.Ordinal);
        Assert.Contains("version     = \"4.62.0\"", lockFile, StringComparison.Ordinal);
        Assert.Contains("h1:", lockFile, StringComparison.Ordinal);
    }

    [Fact]
    public void TerraformVariables_ValidateApprovedTypedSettingsAndExplicitDefaultRegion()
    {
        var variables = LoadText("terraform", "variables.tf");
        var example = LoadText("terraform", "examples", "demo.tfvars");

        Assert.Contains("variable \"deployment_name\"", variables, StringComparison.Ordinal);
        Assert.Contains("variable \"resource_group_name\"", variables, StringComparison.Ordinal);
        Assert.Contains("variable \"region\"", variables, StringComparison.Ordinal);
        Assert.Contains("type        = string", variables, StringComparison.Ordinal);
        Assert.Contains("type        = number", variables, StringComparison.Ordinal);
        Assert.Contains("validation", variables, StringComparison.Ordinal);
        Assert.Contains("default     = \"swedencentral\"", variables, StringComparison.Ordinal);
        Assert.Contains("region", example, StringComparison.Ordinal);
        Assert.Contains("= \"swedencentral\"", example, StringComparison.Ordinal);
        Assert.Contains("Azure Functions v4", example, StringComparison.Ordinal);
        Assert.Contains("dotnet-isolated", example, StringComparison.Ordinal);
        Assert.Contains("StorageV2", example, StringComparison.Ordinal);
        Assert.Contains("Serverless General Purpose", example, StringComparison.Ordinal);
        Assert.Contains("system-assigned managed identity", example, StringComparison.Ordinal);
        Assert.Contains("firewall-restricted", example, StringComparison.Ordinal);
        Assert.Contains("= 90", example, StringComparison.Ordinal);
        Assert.Contains("= 30000", example, StringComparison.Ordinal);
    }

    [Fact]
    public void TerraformExample_IsCredentialFreeAndPublishesApplicationOutputs()
    {
        var example = LoadText("terraform", "examples", "demo.tfvars");
        var outputs = LoadText("terraform", "outputs.tf");

        Assert.Contains("airlinedemo-swc-demo", example, StringComparison.Ordinal);
        Assert.Contains("rg-airlinedemo-swc-demo", example, StringComparison.Ordinal);
        Assert.DoesNotContain("subscription", example, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", example, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("output \"deployment_name\"", outputs, StringComparison.Ordinal);
        Assert.Contains("output \"resource_group_name\"", outputs, StringComparison.Ordinal);
        Assert.Contains("output \"region\"", outputs, StringComparison.Ordinal);
        Assert.Contains("output \"application_deployment\"", outputs, StringComparison.Ordinal);
        Assert.Contains("No resource endpoints or credentials", outputs,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TerraformModules_CreateTaggedDemoBoundaryWithSeparatedStorage()
    {
        var root = LoadText("terraform", "main.tf");
        var boundary = LoadText("terraform", "modules", "demo-boundary", "main.tf");
        var boundaryOutputs = LoadText("terraform", "modules", "demo-boundary", "outputs.tf");

        Assert.Contains("module \"demo_boundary\"", root, StringComparison.Ordinal);
        Assert.Contains("resource \"azurerm_resource_group\" \"this\"", boundary,
            StringComparison.Ordinal);
        Assert.Contains("tags     = var.tags", boundary, StringComparison.Ordinal);
        Assert.Contains("deployment  = var.deployment_name", root, StringComparison.Ordinal);
        Assert.Contains("owner       = var.owner", root, StringComparison.Ordinal);
        Assert.Contains("cost_center = var.cost_center", root, StringComparison.Ordinal);
        Assert.Contains("resource \"azurerm_storage_account\" \"runtime_host\"", boundary,
            StringComparison.Ordinal);
        Assert.Contains("resource \"azurerm_storage_account\" \"evidence\"", boundary,
            StringComparison.Ordinal);
        Assert.Contains("runtime_host_storage_account_id", boundaryOutputs,
            StringComparison.Ordinal);
        Assert.Contains("evidence_storage_account_id", boundaryOutputs,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TerraformEvidenceStorage_IsPrivateVersionedAndRetainedWithoutAHostPolicy()
    {
        var boundary = LoadText("terraform", "modules", "demo-boundary", "main.tf");
        var boundaryVariables = LoadText(
            "terraform", "modules", "demo-boundary", "variables.tf");

        Assert.Contains("public_network_access_enabled   = false", boundary,
            StringComparison.Ordinal);
        Assert.Contains("shared_access_key_enabled       = false", boundary,
            StringComparison.Ordinal);
        Assert.Contains("versioning_enabled = true", boundary, StringComparison.Ordinal);
        Assert.Contains("resource \"azurerm_storage_management_policy\" \"evidence\"",
            boundary, StringComparison.Ordinal);
        Assert.Contains("delete_after_days_since_creation = var.evidence_retention_days",
            boundary, StringComparison.Ordinal);
        Assert.Contains("evidence_retention_days == 90", boundaryVariables,
            StringComparison.Ordinal);

        var runtimeStart = boundary.IndexOf(
            "resource \"azurerm_storage_account\" \"runtime_host\"", StringComparison.Ordinal);
        var evidenceStart = boundary.IndexOf(
            "resource \"azurerm_storage_account\" \"evidence\"", StringComparison.Ordinal);
        Assert.True(runtimeStart >= 0 && evidenceStart > runtimeStart);
        var runtimeBlock = boundary[runtimeStart..evidenceStart];
        Assert.DoesNotContain("blob_properties", runtimeBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("azurerm_storage_management_policy", runtimeBlock,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TerraformObservability_CreatesNinetyDayWorkspaceAndApplicationInsights()
    {
        var root = LoadText("terraform", "main.tf");
        var observability = LoadText("terraform", "modules", "observability", "main.tf");
        var outputs = LoadText("terraform", "outputs.tf");

        Assert.Contains("module \"observability\"", root, StringComparison.Ordinal);
        Assert.Contains("resource \"azurerm_log_analytics_workspace\" \"this\"",
            observability, StringComparison.Ordinal);
        Assert.Contains("resource \"azurerm_application_insights\" \"this\"",
            observability, StringComparison.Ordinal);
        Assert.Contains("retention_in_days   = var.retention_days", observability,
            StringComparison.Ordinal);
        Assert.Contains("retention_days == 90", LoadText(
            "terraform", "modules", "observability", "variables.tf"), StringComparison.Ordinal);
        Assert.Contains("output \"application_insights_id\"", outputs,
            StringComparison.Ordinal);
        Assert.Contains("output \"log_analytics_workspace_id\"", outputs,
            StringComparison.Ordinal);
        Assert.DoesNotContain("connection_string", outputs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("instrumentation_key", outputs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TerraformConfiguration_UsesOnlyApprovedBoundedResources()
    {
        var terraformDirectory = new DirectoryInfo(FindRepositoryRoot())
            .EnumerateFiles("*.tf", SearchOption.AllDirectories);
        var configuration = string.Join(
            Environment.NewLine,
            terraformDirectory.Select(file => File.ReadAllText(file.FullName)));

        Assert.Contains("source = \"./modules/demo-boundary\"", configuration,
            StringComparison.Ordinal);
        Assert.Contains("source = \"./modules/observability\"", configuration,
            StringComparison.Ordinal);

        var resourceTypes = Regex.Matches(
                configuration,
                @"resource\s+""(?<type>[^""]+)""\s+""[^""]+""")
            .Select(match => match.Groups["type"].Value)
            .ToArray();
        var approvedResourceTypes = new HashSet<string>(
            [
                "azurerm_application_insights",
                "azurerm_log_analytics_workspace",
                "azurerm_resource_group",
                "azurerm_storage_account",
                "azurerm_storage_management_policy"
            ],
            StringComparer.Ordinal);

        Assert.Equal(6, resourceTypes.Length);
        Assert.Empty(resourceTypes.Except(approvedResourceTypes, StringComparer.Ordinal));
        Assert.Equal(
            approvedResourceTypes.Count,
            resourceTypes.Distinct(StringComparer.Ordinal).Count());

        var moduleNames = Regex.Matches(
                configuration,
                @"module\s+""(?<name>[^""]+)""\s*\{")
            .Select(match => match.Groups["name"].Value)
            .ToArray();

        Assert.Equal(
            ["demo_boundary", "observability"],
            moduleNames.Order(StringComparer.Ordinal).ToArray());

        var declarationHeaders = string.Join(
            Environment.NewLine,
            Regex.Matches(
                    configuration,
                    @"(?:resource|module)\s+""[^""]+""(?:\s+""[^""]+"")?")
                .Select(match => match.Value));
        foreach (var prohibitedInfrastructure in new[]
                 {
                     "azurerm_fabric",
                     "azurerm_search_service",
                     "azurerm_kubernetes_cluster",
                     "azurerm_servicebus",
                     "azurerm_api_management",
                     "azurerm_container_registry",
                     "azurerm_consumption_reservation",
                     "azurerm_capacity_reservation",
                     "foundry_agent_service",
                     "microsoft_agent_framework"
                 })
        {
            Assert.DoesNotContain(prohibitedInfrastructure, declarationHeaders,
                StringComparison.OrdinalIgnoreCase);
        }
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AirlineDemo.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
