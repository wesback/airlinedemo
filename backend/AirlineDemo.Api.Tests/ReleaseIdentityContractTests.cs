using System.Text.RegularExpressions;

namespace AirlineDemo.Api.Tests;

// Source-text checks over infra/terraform that Terraform's own tests cannot make: which role assignments and outputs exist at all.
// They match declaration names only (no attributes, braces or heredocs); scopes, principals, roles, federation and output values
// are asserted on evaluated values by infra/terraform/tests/release-identity.tftest.hcl.
public sealed class ReleaseIdentityContractTests
{
    private static readonly Regex RoleAssignmentName = new(@"(?m)^resource\s+""azurerm_role_assignment""\s+""(\w+)""");
    private static readonly Regex OutputName = new(@"(?m)^output\s+""(\w+)""");
    private static readonly Regex ModuleBlock = new(@"(?m)^\s*module\s+""");

    // RELEASE-ID-01 and RELEASE-ID-06: a new, renamed or second grant (including a second one for the app identity on the ACR)
    // fails until this list is changed on purpose. app_acr_pull is the app identity's only ACR grant; the Terraform test
    // registry_is_private_and_pull_scoped asserts it is AcrPull on the ACR for the app identity.
    [Fact]
    public void Role_assignments_are_held_to_a_baseline()
    {
        var files = ReadTerraformFiles();

        var names = files
            .SelectMany(f => RoleAssignmentName.Matches(f.Text).Select(m => m.Groups[1].Value))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "app_acr_pull",
                "app_evidence",
                "app_openai_user",
                "app_telemetry",
                "build_acr_push",
                "migration_sql",
                "release_acr_push",
                "release_container_app",
                "release_signing_certificate",
                "release_signing_key",
                "telemetry_reader",
                "terraform_contributor",
            },
            names);

        Assert.DoesNotContain(files, f => ModuleBlock.IsMatch(f.Text));
    }

    // RELEASE-ID-04: the set of outputs cannot grow unnoticed. The Terraform test release_outputs_contain_no_secrets asserts the
    // evaluated values of these three outputs.
    [Fact]
    public void Outputs_are_held_to_a_baseline()
    {
        var names = ReadTerraformFiles()
            .SelectMany(f => OutputName.Matches(f.Text).Select(m => m.Groups[1].Value))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "app_sql_grant_statements",
                "migration_sql_grant_statements",
                "model_endpoint",
            },
            names);
    }

    // Behaviour test for the release identity contracts: what release-identity.tf declares, by name.
    [Fact]
    public void Release_identity_contracts_are_complete()
    {
        var root = Path.Combine(FindRepoRoot(), "infra", "terraform");
        var release = File.ReadAllText(Path.Combine(root, "release-identity.tf"));

        Assert.Equal(
            new[] { "build", "release" },
            Regex.Matches(release, @"(?m)^resource\s+""azurerm_user_assigned_identity""\s+""(\w+)""").Select(m => m.Groups[1].Value).ToArray());
        Assert.Equal(
            new[] { "build", "release" },
            Regex.Matches(release, @"(?m)^resource\s+""azurerm_federated_identity_credential""\s+""(\w+)""").Select(m => m.Groups[1].Value).ToArray());
        Assert.Equal(
            new[] { "build_acr_push", "release_acr_push", "release_signing_key", "release_signing_certificate", "release_container_app" },
            RoleAssignmentName.Matches(release).Select(m => m.Groups[1].Value).ToArray());
        Assert.Empty(OutputName.Matches(release));

        // RELEASE-ID-02: exactly one federated credential per workflow identity across all Terraform files.
        Assert.Equal(
            new[] { "build", "release" },
            ReadTerraformFiles()
                .SelectMany(f => Regex.Matches(f.Text, @"(?m)^resource\s+""azurerm_federated_identity_credential""\s+""(\w+)""").Select(m => m.Groups[1].Value))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray());

        // RELEASE-ID-07: the signing key is the key Key Vault creates for the certificate; no separate key resource.
        Assert.DoesNotContain("azurerm_key_vault_key", release);

        // The existing terraform_principal_object_id grant (Contributor) must never resolve to a workflow identity, including a
        // match that differs only in letter case; the Terraform test contributor_precondition_rejects_case_only_workflow_match
        // exercises the precondition, and this keeps the test inputs distinct.
        var testFile = File.ReadAllText(Path.Combine(root, "tests", "release-identity.tftest.hcl"));
        var inputPrincipal = Regex.Match(testFile, @"(?m)^\s*terraform_principal_object_id\s*=\s*""([^""]+)""").Groups[1].Value;
        var workflowPrincipals = Regex.Matches(
                testFile,
                @"target\s*=\s*azurerm_user_assigned_identity\.(build|release)\s*values\s*=\s*\{[^}]*?principal_id\s*=\s*""([^""]+)""",
                RegexOptions.Singleline)
            .Select(m => m.Groups[2].Value)
            .ToArray();
        Assert.NotEmpty(workflowPrincipals);
        Assert.NotEmpty(inputPrincipal);
        Assert.All(workflowPrincipals, p => Assert.NotEqual(inputPrincipal, p));

        // RELEASE-ID-05: the Terraform test uses the mock provider and provisions nothing.
        Assert.Contains("mock_provider \"azurerm\"", testFile);
    }

    private static List<(string Path, string Text)> ReadTerraformFiles()
    {
        var root = Path.Combine(FindRepoRoot(), "infra", "terraform");
        return Directory.EnumerateFiles(root, "*.tf", SearchOption.AllDirectories)
            .Select(f => (Path: f, Text: File.ReadAllText(f)))
            .ToList();
    }

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
