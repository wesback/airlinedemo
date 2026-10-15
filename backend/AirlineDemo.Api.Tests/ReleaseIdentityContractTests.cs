using System.Text.RegularExpressions;

namespace AirlineDemo.Api.Tests;

public sealed class ReleaseIdentityContractTests
{
    private static readonly Regex RoleAssignmentBlock = new(
        @"^resource\s+""azurerm_role_assignment""\s+""[^""]+""\s*\{(?<body>.*?)^\}",
        RegexOptions.Multiline | RegexOptions.Singleline);

    private static readonly Regex RoleDefinitionName = new(@"role_definition_name\s*=\s*""([^""]+)""");

    private static readonly Regex AppIdentityReference = new(@"azurerm_user_assigned_identity\.app\b");

    // RELEASE-ID-01: exactly one Terraform grant gives the app identity a role on the ACR, and it is AcrPull.
    [Fact]
    public void App_identity_has_exactly_one_acr_role_assignment()
    {
        var root = Path.Combine(FindRepoRoot(), "infra", "terraform");
        var appAcrGrantRoles = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.tf", SearchOption.AllDirectories))
        {
            foreach (Match block in RoleAssignmentBlock.Matches(File.ReadAllText(file)))
            {
                var body = block.Groups["body"].Value;
                if (!AppIdentityReference.IsMatch(body) || !body.Contains("azurerm_container_registry", StringComparison.Ordinal))
                {
                    continue;
                }

                var role = RoleDefinitionName.Match(body);
                appAcrGrantRoles.Add(role.Success ? role.Groups[1].Value : string.Empty);
            }
        }

        Assert.Equal(new[] { "AcrPull" }, appAcrGrantRoles);
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
