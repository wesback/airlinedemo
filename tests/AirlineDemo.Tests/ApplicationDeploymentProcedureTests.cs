using System.Text.RegularExpressions;

namespace AirlineDemo.Tests;

public sealed class ApplicationDeploymentProcedureTests
{
    [Fact]
    public void Procedure_DefinesReproducibleBuildTagPushAndRevisionDeployment()
    {
        var procedure = LoadText("deployment", "application-deployment-procedure.md");

        Assert.Contains("Dockerfile", procedure, StringComparison.Ordinal);
        Assert.Contains("dotnet/sdk:10.0", LoadText("Dockerfile"), StringComparison.Ordinal);
        Assert.Contains("mcr.microsoft.com/dotnet/aspnet:10.0", LoadText("Dockerfile"),
            StringComparison.Ordinal);
        Assert.Contains("SOURCE_REVISION", procedure, StringComparison.Ordinal);
        Assert.Contains("IMAGE_TAG", procedure, StringComparison.Ordinal);
        Assert.Contains("docker build", procedure, StringComparison.Ordinal);
        Assert.Contains("output -raw container_app_registry_id", procedure,
            StringComparison.Ordinal);
        Assert.Contains("az acr login", procedure, StringComparison.Ordinal);
        Assert.Contains("docker push", procedure, StringComparison.Ordinal);
        Assert.Contains("az containerapp update", procedure, StringComparison.Ordinal);
        Assert.Contains("--resource-group", procedure, StringComparison.Ordinal);
        Assert.Contains("--revision-suffix", procedure, StringComparison.Ordinal);
        Assert.Contains("container_app_id", procedure, StringComparison.Ordinal);
        Assert.Contains("container_image=$IMAGE_REF", procedure, StringComparison.Ordinal);
        Assert.Contains("az account set --subscription", procedure,
            StringComparison.Ordinal);
        Assert.DoesNotContain(":latest", procedure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Procedure_RequiresAllApprovalCheckpointsBeforeApplyOrDeployment()
    {
        var procedure = LoadText("deployment", "application-deployment-procedure.md");
        var normalized = Normalize(procedure);

        var preflight = normalized.IndexOf("### 1. Preflight checkpoint",
            StringComparison.Ordinal);
        var cost = normalized.IndexOf("### 2. Cost checkpoint", StringComparison.Ordinal);
        var destination = normalized.IndexOf("### 3. Destination checkpoint",
            StringComparison.Ordinal);
        var plan = normalized.IndexOf("### 4. Terraform-plan checkpoint",
            StringComparison.Ordinal);
        var approval = normalized.IndexOf("### 5. Operator approval checkpoint",
            StringComparison.Ordinal);
        var apply = normalized.IndexOf("terraform -chdir=\"$TERRAFORM_ROOT\" apply",
            StringComparison.Ordinal);
        var revision = normalized.IndexOf("az containerapp update", StringComparison.Ordinal);

        Assert.True(preflight >= 0 && cost > preflight);
        Assert.True(destination > cost && plan > destination);
        Assert.True(approval > plan && apply > approval && revision > approval);
        Assert.Contains("Do not run an infrastructure apply, image push, or revision deployment",
            normalized, StringComparison.Ordinal);
        Assert.Contains("planning-only", normalized, StringComparison.Ordinal);
        Assert.Contains("not evidence that the command has run or succeeded", normalized,
            StringComparison.Ordinal);
        Assert.Contains("USD 500", normalized, StringComparison.Ordinal);
        Assert.Contains("residual charges", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("complete Terraform plan", normalized, StringComparison.Ordinal);
        Assert.Contains("authorized operator", normalized, StringComparison.Ordinal);
        Assert.Contains("does not claim", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void Procedure_SeparatesDeploymentRuntimeAndMigrationIdentities()
    {
        var procedure = LoadText("deployment", "application-deployment-procedure.md");
        var normalized = Normalize(procedure);

        Assert.Contains("separately supplies the Azure subscription and tenant",
            normalized, StringComparison.Ordinal);
        Assert.Contains("deployment-principal object ID", normalized,
            StringComparison.Ordinal);
        Assert.Contains("container_app_runtime_principal_id", normalized,
            StringComparison.Ordinal);
        Assert.Contains("migration_identity_principal_id", normalized,
            StringComparison.Ordinal);
        Assert.Contains("DEPLOYMENT_PRINCIPAL_OBJECT_ID", normalized,
            StringComparison.Ordinal);
        Assert.Contains("test \"$DEPLOYMENT_PRINCIPAL_OBJECT_ID\" != \"$RUNTIME_PRINCIPAL_ID\"",
            normalized, StringComparison.Ordinal);
        Assert.Contains("test \"$DEPLOYMENT_PRINCIPAL_OBJECT_ID\" != \"$MIGRATION_PRINCIPAL_ID\"",
            normalized, StringComparison.Ordinal);
        Assert.Contains("test \"$RUNTIME_PRINCIPAL_ID\" != \"$MIGRATION_PRINCIPAL_ID\"",
            normalized, StringComparison.Ordinal);
        Assert.Contains("not the Container Apps system-assigned runtime identity",
            normalized, StringComparison.Ordinal);
        Assert.Contains("not the Terraform-managed", normalized, StringComparison.Ordinal);
        Assert.Contains("Container App ID", normalized, StringComparison.Ordinal);
        Assert.Contains("container_app_id", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void Procedure_ContainsNoCredentialsOrSeparatedDataArtifacts()
    {
        var procedure = LoadText("deployment", "application-deployment-procedure.md");
        var normalized = Normalize(procedure);

        var forbiddenCredentialPatterns = new[]
        {
            @"(?i)accountkey\s*=",
            @"(?i)defaultendpointsprotocol\s*=",
            @"(?i)connectionstrings?\s*=",
            @"(?i)(client[_-]?secret|storage[_-]?key|sas[_-]?token)\s*=",
            @"(?i)(password|secret)\s*=\s*[""'`]",
            @"(?i)bearer\s+[a-z0-9._-]{20,}"
        };

        Assert.DoesNotMatch(
            new Regex(string.Join("|", forbiddenCredentialPatterns)),
            procedure);
        Assert.DoesNotContain("generator/answer-key", procedure,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evaluator-data", procedure,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("staged-mock-partner-response", procedure,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mock-partner-responses/", procedure,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not written in this procedure", normalized,
            StringComparison.Ordinal);
        Assert.Contains("not part of Terraform or this image", normalized,
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

    private static string Normalize(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim();
}
