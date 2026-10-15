using System.Text.RegularExpressions;

namespace AirlineDemo.Api.Tests;

public sealed class ReleaseIdentityContractTests
{
    private static readonly Regex RoleAssignmentBlock = new(
        @"^resource\s+""azurerm_role_assignment""\s+""[^""]+""\s*\{(?<body>.*?)^\}",
        RegexOptions.Multiline | RegexOptions.Singleline);

    private static readonly Regex RoleDefinitionName = new(@"role_definition_name\s*=\s*""([^""]+)""");

    private static readonly Regex AppIdentityReference = new(@"azurerm_user_assigned_identity\.app\b");

    private static readonly Regex IndirectScope = new(@"scope\s*=\s*(local|var)\.");

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

    private static List<string> AppAcrGrantRoles(string terraform)
    {
        var roles = new List<string>();
        foreach (Match block in RoleAssignmentBlock.Matches(terraform))
        {
            var body = block.Groups["body"].Value;
            if (!AppIdentityReference.IsMatch(body)
                || (!body.Contains("azurerm_container_registry", StringComparison.Ordinal) && !IndirectScope.IsMatch(body)))
            {
                continue;
            }

            var role = RoleDefinitionName.Match(body);
            roles.Add(role.Success ? role.Groups[1].Value : string.Empty);
        }

        return roles;
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
