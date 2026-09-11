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
