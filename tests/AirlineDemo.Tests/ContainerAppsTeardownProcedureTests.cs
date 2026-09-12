using System.Text.RegularExpressions;

namespace AirlineDemo.Tests;

public sealed class ContainerAppsTeardownProcedureTests
{
    [Fact]
    public void Procedure_RequiresOrderedTargetRunDrainReceiptsAndApprovedDestroy()
    {
        var procedure = LoadText(
            "deployment", "container-apps-teardown-procedure.md");
        var normalized = Normalize(procedure);

        var target = normalized.IndexOf(
            "### 1. Confirm the exact environment and run",
            StringComparison.Ordinal);
        var drain = normalized.IndexOf(
            "### 2. Stop and drain new demo work",
            StringComparison.Ordinal);
        var receipts = normalized.IndexOf(
            "### 3. Preserve evaluation and recording receipts",
            StringComparison.Ordinal);
        var plan = normalized.IndexOf(
            "### 4. Review the bounded Terraform destroy plan",
            StringComparison.Ordinal);
        var apply = normalized.IndexOf(
            "### 5. Apply the approved destroy plan",
            StringComparison.Ordinal);
        var destroyCommand = normalized.IndexOf(
            "terraform -chdir=\"$TERRAFORM_ROOT\" apply \"$DESTROY_PLAN\"",
            StringComparison.Ordinal);

        Assert.True(target >= 0);
        Assert.True(drain > target);
        Assert.True(receipts > drain);
        Assert.True(plan > receipts);
        Assert.True(apply > plan);
        Assert.True(destroyCommand > apply);
        Assert.Contains("airlinedemo-swc-demo", normalized,
            StringComparison.Ordinal);
        Assert.Contains("rg-airlinedemo-swc-demo", normalized,
            StringComparison.Ordinal);
        Assert.Contains("TARGET_RUN_ID", normalized, StringComparison.Ordinal);
        Assert.Contains("no active run", normalized, StringComparison.Ordinal);
        Assert.Contains("evaluation receipt", normalized, StringComparison.Ordinal);
        Assert.Contains("recording receipt", normalized, StringComparison.Ordinal);
        Assert.Contains("Terraform plan hash", normalized, StringComparison.Ordinal);
        Assert.Contains("authorized operator", normalized, StringComparison.Ordinal);
        Assert.Contains("missing, unreadable, or unapproved receipt blocks destruction",
            normalized, StringComparison.Ordinal);
        Assert.Contains("Do not close the teardown", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void Procedure_EnumeratesDisposableScopeAndProtectsEveryExcludedBoundary()
    {
        var procedure = LoadText(
            "deployment", "container-apps-teardown-procedure.md");
        var normalized = Normalize(procedure);

        var disposableResources = new[]
        {
            "module.demo_boundary.azurerm_resource_group.this",
            "module.demo_boundary.azurerm_storage_account.evidence",
            "module.demo_boundary.azurerm_storage_management_policy.evidence",
            "module.observability.azurerm_log_analytics_workspace.this",
            "module.observability.azurerm_application_insights.this",
            "module.container_apps.azurerm_container_registry.application",
            "module.container_apps.azurerm_container_app_environment.this",
            "module.container_apps.azurerm_container_app.this",
            "module.container_apps.azurerm_user_assigned_identity.migration",
            "module.container_apps.azurerm_role_assignment.runtime_evidence_reader",
            "module.container_apps.azurerm_role_assignment.runtime_document_intelligence_user",
            "module.container_apps.azurerm_role_assignment.runtime_azure_openai_user",
            "module.sql.azurerm_mssql_server.this",
            "module.sql.azurerm_mssql_database.this",
            "module.sql.azurerm_mssql_firewall_rule.azure_services",
            "module.document_intelligence.azurerm_cognitive_account.this",
            "module.ai.azurerm_cognitive_account.this",
            "module.ai.azurerm_cognitive_deployment.this"
        };

        Assert.Equal(18, disposableResources.Length);
        Assert.All(disposableResources, resource =>
            Assert.Contains(resource, procedure, StringComparison.Ordinal));
        Assert.Contains("only the Terraform-owned disposable", normalized,
            StringComparison.Ordinal);
        Assert.Contains("every planned address is one of the 18 disposable resources",
            normalized, StringComparison.Ordinal);

        Assert.Contains("rg-airlinedemo-state", normalized, StringComparison.Ordinal);
        Assert.Contains("stairlinedemostate", normalized, StringComparison.Ordinal);
        Assert.Contains("private `tfstate` container", normalized,
            StringComparison.Ordinal);
        Assert.Contains("Storage Blob Data Contributor", normalized,
            StringComparison.Ordinal);
        Assert.Contains("Do not run `terraform/bootstrap` destroy", normalized,
            StringComparison.Ordinal);
        Assert.Contains("subscription and tenant", normalized, StringComparison.Ordinal);
        Assert.Contains("subscription-scope or tenant-scope cleanup", normalized,
            StringComparison.Ordinal);
        Assert.Contains("Shared network and policy resources", normalized,
            StringComparison.Ordinal);
        Assert.Contains("organizational virtual network", normalized,
            StringComparison.Ordinal);
        Assert.Contains("Other users' data", normalized, StringComparison.Ordinal);
        Assert.Contains("another user, tenant, run, case", normalized,
            StringComparison.Ordinal);
        Assert.Contains("mixed with this demo", normalized, StringComparison.Ordinal);

        Assert.DoesNotMatch(
            new Regex(
                @"(?im)^\s*(?:az\s+group\s+delete|terraform\s+-chdir=.?terraform/bootstrap.*destroy)\b"),
            procedure);
    }

    [Fact]
    public void Procedure_RequiresPostTeardownVerificationAndResidualBillingEvidence()
    {
        var procedure = LoadText(
            "deployment", "container-apps-teardown-procedure.md");
        var normalized = Normalize(procedure);

        var destroy = normalized.IndexOf(
            "terraform -chdir=\"$TERRAFORM_ROOT\" apply \"$DESTROY_PLAN\"",
            StringComparison.Ordinal);
        var verification = normalized.IndexOf(
            "## Post-teardown verification and residual-cost record",
            StringComparison.Ordinal);

        Assert.True(destroy >= 0);
        Assert.True(verification > destroy);
        Assert.Contains("no remaining workload-root objects", normalized,
            StringComparison.Ordinal);
        Assert.Contains("`rg-airlinedemo-swc-demo` has no remaining disposable resources",
            normalized, StringComparison.Ordinal);
        Assert.Contains("Cost Management actuals and forecast", normalized,
            StringComparison.Ordinal);
        Assert.Contains("A successful destroy command is not proof that billing has stopped",
            normalized, StringComparison.Ordinal);
        Assert.Contains("residual-cost table", normalized, StringComparison.Ordinal);

        Assert.Contains("Retained evidence storage, blob versions", normalized,
            StringComparison.Ordinal);
        Assert.Contains("Azure SQL database backups and retained database storage",
            normalized, StringComparison.Ordinal);
        Assert.Contains("Application Insights and Log Analytics monitoring retention",
            normalized, StringComparison.Ordinal);
        Assert.Contains("Protected remote Terraform state and lock/blob storage",
            normalized, StringComparison.Ordinal);
        Assert.Contains("billing period", normalized, StringComparison.Ordinal);
        Assert.Contains("resource and meter breakdown", normalized,
            StringComparison.Ordinal);
        Assert.Contains("owner and next action", normalized, StringComparison.Ordinal);
        Assert.Contains("deletion lag", normalized, StringComparison.Ordinal);
        Assert.Contains("do not claim zero cost", normalized, StringComparison.Ordinal);
        Assert.Contains("Teardown is closed only when", normalized, StringComparison.Ordinal);
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
