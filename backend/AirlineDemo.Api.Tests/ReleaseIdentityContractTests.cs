using System.Text.RegularExpressions;

namespace AirlineDemo.Api.Tests;

public sealed class ReleaseIdentityContractTests
{
    private static readonly Regex RoleAssignmentBlock = new(
        @"^resource\s+""azurerm_role_assignment""\s+""[^""]+""\s*\{(?<body>.*?)^\}",
        RegexOptions.Multiline | RegexOptions.Singleline);

    private static readonly Regex RoleDefinitionName = new(@"role_definition_name\s*=\s*""([^""]+)""");

    private static readonly Regex AppIdentityReference = new(@"azurerm_user_assigned_identity\.app\b");

    private static readonly Regex BuildOrReleasePrincipal = new(
        @"(?m)^\s*principal_id\s*=\s*azurerm_user_assigned_identity\.(build|release)\.principal_id\s*$");

    private static readonly Regex ScopeAttribute = new(@"^\s*scope\s*=\s*(?<expr>.+)$", RegexOptions.Multiline);

    private static readonly Regex ContainerRegistryReference = new(@"\bazurerm_container_registry\b");

    private static readonly Regex IndirectReference = new(@"\b(local|var|module)\.(\w+)");

    private static readonly Regex ResourceOrDataReference = new(@"\bazurerm_\w+\.|\bdata\.");

    private static readonly Regex LiteralProvider = new(@"/providers/(?<ns>[A-Za-z0-9.]+)/", RegexOptions.IgnoreCase);

    private static readonly Regex InterpolatedVariable = new(@"\$\{var\.\w+\}");

    // A resource-group scope has no /providers/ segment, so it cannot be the registry (names never contain '/').
    private static readonly Regex ResourceGroupOnlyScope = new(@"^\s*""/subscriptions/[^""/]*/resourceGroups/[^""/]*""\s*$");

    private const int MaxIndirectionDepth = 5;

    // RELEASE-ID-01: exactly one Terraform grant gives the app identity a role on the ACR, and it is AcrPull.
    [Fact]
    public void App_identity_has_exactly_one_acr_role_assignment()
    {
        var root = Path.Combine(FindRepoRoot(), "infra", "terraform");
        var terraform = string.Join("\n", Directory.EnumerateFiles(root, "*.tf", SearchOption.AllDirectories).Select(f => File.ReadAllText(f)));

        Assert.Equal(new[] { "AcrPull" }, AppAcrGrantRoles(terraform));
    }

    [Fact]
    public void App_grant_with_indirect_acr_scope_is_counted()
    {
        const string terraform = """
            resource "azurerm_role_assignment" "app_acr_pull" {
              scope                = azurerm_container_registry.acr.id
              role_definition_name = "AcrPull"
              principal_id         = azurerm_user_assigned_identity.app.principal_id
            }

            resource "azurerm_role_assignment" "app_acr_push_via_local" {
              scope                = local.acr_scope
              role_definition_name = "AcrPush"
              principal_id         = azurerm_user_assigned_identity.app.principal_id
            }
            """;

        Assert.Equal(new[] { "AcrPull", "AcrPush" }, AppAcrGrantRoles(terraform));
    }

    [Fact]
    public void App_grant_with_function_wrapped_acr_scope_is_counted()
    {
        const string terraform = """
            locals {
              acr_scope = azurerm_container_registry.acr.id
            }

            resource "azurerm_role_assignment" "app_acr_push_via_function" {
              scope                = try(local.acr_scope, null)
              role_definition_name = "AcrPush"
              principal_id         = azurerm_user_assigned_identity.app.principal_id
            }
            """;

        Assert.Equal(new[] { "AcrPush" }, AppAcrGrantRoles(terraform));
    }

    [Fact]
    public void App_grant_on_non_acr_resource_is_not_counted()
    {
        const string terraform = """
            locals {
              kv_scope = azurerm_key_vault.kv.id
            }

            resource "azurerm_role_assignment" "app_kv_read" {
              scope                = local.kv_scope
              role_definition_name = "Key Vault Secrets User"
              principal_id         = azurerm_user_assigned_identity.app.principal_id
            }
            """;

        Assert.Empty(AppAcrGrantRoles(terraform));
    }

    [Fact]
    public void App_grant_on_literal_non_acr_resource_id_is_not_counted()
    {
        const string terraform = """
            resource "azurerm_role_assignment" "app_kv_read" {
              scope                = "/subscriptions/0000/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/kv"
              role_definition_name = "Key Vault Secrets User"
              principal_id         = azurerm_user_assigned_identity.app.principal_id
            }

            resource "azurerm_role_assignment" "app_acr_literal" {
              scope                = "/subscriptions/0000/resourceGroups/rg/providers/Microsoft.ContainerRegistry/registries/acr"
              role_definition_name = "AcrPush"
              principal_id         = azurerm_user_assigned_identity.app.principal_id
            }
            """;

        Assert.Equal(new[] { "AcrPush" }, AppAcrGrantRoles(terraform));
    }

    [Fact]
    public void App_grant_through_module_output_principal_is_counted()
    {
        const string terraform = """
            resource "azurerm_role_assignment" "worker_acr_push" {
              scope                = azurerm_container_registry.acr.id
              role_definition_name = "AcrPush"
              principal_id         = module.worker.principal_id
            }
            """;

        Assert.Equal(new[] { "AcrPush" }, AppAcrGrantRoles(terraform));
    }

    [Fact]
    public void App_grant_through_local_principal_is_counted()
    {
        const string terraform = """
            locals {
              worker_principal = azurerm_user_assigned_identity.app.principal_id
            }

            resource "azurerm_role_assignment" "worker_acr_pull" {
              scope                = azurerm_container_registry.acr.id
              role_definition_name = "AcrPull"
              principal_id         = local.worker_principal
            }
            """;

        Assert.Equal(new[] { "AcrPull" }, AppAcrGrantRoles(terraform));
    }

    [Fact]
    public void App_grant_on_resource_group_scope_is_not_counted()
    {
        const string terraform = """
            locals {
              rg_scope = "/subscriptions/${var.subscription_id}/resourceGroups/${var.resource_group_name}"
            }

            resource "azurerm_role_assignment" "app_rg_contributor" {
              scope                = local.rg_scope
              role_definition_name = "Contributor"
              principal_id         = azurerm_user_assigned_identity.app.principal_id
            }
            """;

        Assert.Empty(AppAcrGrantRoles(terraform));
    }

    [Fact]
    public void Build_and_release_acr_grants_are_not_counted_as_app_grants()
    {
        const string terraform = """
            resource "azurerm_role_assignment" "build_acr_push" {
              scope                = azurerm_container_registry.acr.id
              role_definition_name = "AcrPush"
              principal_id         = azurerm_user_assigned_identity.build.principal_id
            }

            resource "azurerm_role_assignment" "release_acr_push" {
              scope                = azurerm_container_registry.acr.id
              role_definition_name = "AcrPush"
              principal_id         = azurerm_user_assigned_identity.release.principal_id
            }
            """;

        Assert.Empty(AppAcrGrantRoles(terraform));
    }

    private static List<string> AppAcrGrantRoles(string terraform)
    {
        var roles = new List<string>();
        foreach (Match block in RoleAssignmentBlock.Matches(terraform))
        {
            var body = block.Groups["body"].Value;
            if (!MayGrantToApp(body) || !MayScopeToAcr(terraform, body))
            {
                continue;
            }

            var role = RoleDefinitionName.Match(body);
            roles.Add(role.Success ? role.Groups[1].Value : string.Empty);
        }

        return roles;
    }

    // Fails closed: a principal that is not literally the build or release identity (a module output, local, variable
    // or the app identity itself) may be the app identity, so the grant is counted.
    private static bool MayGrantToApp(string roleAssignmentBody) =>
        AppIdentityReference.IsMatch(roleAssignmentBody) || !BuildOrReleasePrincipal.IsMatch(roleAssignmentBody);

    // Fails closed: a scope that cannot be shown to avoid the ACR is treated as an ACR grant.
    private static bool MayScopeToAcr(string terraform, string roleAssignmentBody)
    {
        var scope = ScopeAttribute.Match(roleAssignmentBody);
        return !scope.Success || MayReferenceAcr(terraform, scope.Groups["expr"].Value, MaxIndirectionDepth);
    }

    private static bool MayReferenceAcr(string terraform, string expression, int depth)
    {
        if (ContainerRegistryReference.IsMatch(expression))
        {
            return true;
        }

        if (ResourceGroupOnlyScope.IsMatch(InterpolatedVariable.Replace(expression, "")))
        {
            return false;
        }

        var references = IndirectReference.Matches(expression);
        if (references.Count == 0)
        {
            var providers = LiteralProvider.Matches(expression);
            if (providers.Count > 0)
            {
                return providers.Any(p => p.Groups["ns"].Value.Equals("Microsoft.ContainerRegistry", StringComparison.OrdinalIgnoreCase));
            }

            return !ResourceOrDataReference.IsMatch(expression);
        }

        if (depth == 0)
        {
            return true;
        }

        foreach (Match reference in references)
        {
            var definition = DefinitionText(terraform, reference.Groups[2].Value);
            if (definition is null || MayReferenceAcr(terraform, definition, depth - 1))
            {
                return true;
            }
        }

        return false;
    }

    // Returns the right-hand side of `name = ...`, including indented continuation lines.
    private static string? DefinitionText(string terraform, string name)
    {
        var match = Regex.Match(
            terraform,
            $@"^(?<indent>[ \t]*){Regex.Escape(name)}[ \t]*=(?<def>.*)$",
            RegexOptions.Multiline);
        if (!match.Success)
        {
            return null;
        }

        var indent = match.Groups["indent"].Length;
        var lines = new List<string> { match.Groups["def"].Value };
        foreach (var line in terraform[(match.Index + match.Length)..].Split('\n').Skip(1))
        {
            if (line.Trim().Length == 0 || line.TakeWhile(c => c == ' ' || c == '\t').Count() <= indent)
            {
                break;
            }

            lines.Add(line);
        }

        return string.Join("\n", lines);
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
