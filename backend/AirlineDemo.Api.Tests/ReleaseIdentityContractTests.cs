using System.Text.RegularExpressions;

namespace AirlineDemo.Api.Tests;

public sealed class ReleaseIdentityContractTests
{
    private static readonly Regex RoleAssignmentHeader = new(
        @"^resource\s+""azurerm_role_assignment""\s+""(?<name>[^""]+)""\s*\{",
        RegexOptions.Multiline);

    private static readonly Regex ModuleBlock = new(@"^\s*module\s+""[^""]+""\s*\{", RegexOptions.Multiline);

    // A direct reference: azurerm_<type>.<name>.<attribute>, with nothing else in the expression.
    private static readonly Regex DirectResourceReference = new(@"^azurerm_[a-z0-9_]+\.[A-Za-z0-9_\-]+\.[a-z_]+$");

    private static readonly Regex RoleDefinitionName = new(@"role_definition_name\s*=\s*""([^""]+)""");

    private static readonly Regex AppIdentityReference = new(@"azurerm_user_assigned_identity\.app\b");

    private static readonly Regex PrincipalAttribute = new(@"^\s*principal_id\s*=\s*(?<expr>.+)$", RegexOptions.Multiline);

    private static readonly Regex ScopeAttribute = new(@"^\s*scope\s*=\s*(?<expr>.+)$", RegexOptions.Multiline);

    private static readonly Regex ContainerRegistryReference = new(@"\bazurerm_container_registry\b");

    private static readonly Regex IndirectReference = new(@"\b(local|var|module)\.(\w+)");

    private static readonly Regex ResourceOrDataReference = new(@"\bazurerm_\w+\.|\bdata\.");

    private static readonly Regex LiteralProvider = new(@"/providers/(?<ns>[A-Za-z0-9.]+)/", RegexOptions.IgnoreCase);

    private static readonly Regex InterpolatedVariable = new(@"\$\{var\.\w+\}");

    // A resource-group scope has no /providers/ segment, so it cannot be the registry (names never contain '/').
    private static readonly Regex ResourceGroupOnlyScope = new(@"^\s*""/subscriptions/[^""/]*/resourceGroups/[^""/]*""\s*$");

    private static readonly Regex HeredocStart = new(@"<<-?(?<marker>[A-Za-z_][A-Za-z0-9_]*)[ \t]*\r?\n");

    private const int MaxIndirectionDepth = 5;

    // Pre-existing role assignments that resolve through variables or locals. Any other indirect grant fails.
    private static readonly HashSet<(string File, string Block)> ExistingIndirectGrants = new()
    {
        ("main.tf", "terraform_contributor"),
        ("identity-and-telemetry.tf", "telemetry_reader"),
    };

    // RELEASE-ID-01: exactly one Terraform grant gives the app identity a role on the ACR, and it is AcrPull.
    [Fact]
    public void App_identity_has_exactly_one_acr_role_assignment()
    {
        var root = Path.Combine(FindRepoRoot(), "infra", "terraform");
        var terraform = string.Join("\n", Directory.EnumerateFiles(root, "*.tf", SearchOption.AllDirectories).Select(f => File.ReadAllText(f)));

        Assert.Equal(new[] { "AcrPull" }, AppAcrGrantRoles(terraform));
    }

    // RELEASE-ID-06: role assignments resolve by direct references only, so no module, variable, local, data
    // source or expression can hide a grant from the file-level checks.
    [Fact]
    public void Role_assignments_use_only_direct_resource_references()
    {
        var files = ReadTerraformFiles();
        var violations = new List<string>();

        foreach (var (path, text) in files)
        {
            var fileName = Path.GetFileName(path);
            if (ModuleBlock.IsMatch(text))
            {
                violations.Add($"{fileName}: module blocks are not allowed in infra/terraform.");
            }

            foreach (var (blockName, body) in RoleAssignmentBlocks(text))
            {
                var relativePath = Path.GetRelativePath(Path.Combine(FindRepoRoot(), "infra", "terraform"), path).Replace('\\', '/');
                if (ExistingIndirectGrants.Contains((relativePath, blockName)))
                {
                    continue;
                }

                foreach (var attribute in new[] { "principal_id", "scope" })
                {
                    var match = Regex.Match(body, $@"^\s*{attribute}\s*=\s*(?<expr>.+?)\s*$", RegexOptions.Multiline);
                    var expr = match.Success ? match.Groups["expr"].Value : string.Empty;
                    if (!DirectResourceReference.IsMatch(expr))
                    {
                        violations.Add(
                            $"{fileName}: azurerm_role_assignment \"{blockName}\" sets {attribute} to '{expr}', which is not a direct azurerm_<type>.<name>.<attribute> reference.");
                    }
                }
            }
        }

        Assert.Empty(violations);

        var terraform = string.Join("\n", files.Select(f => f.Text));
        Assert.Equal(new[] { "AcrPull" }, AppAcrGrantRoles(terraform));
    }

    // Behaviour test for the release identity contracts: identities, federation, grants and outputs in release-identity.tf.
    [Fact]
    public void Release_identity_contracts_are_complete()
    {
        var root = Path.Combine(FindRepoRoot(), "infra", "terraform");
        var release = File.ReadAllText(Path.Combine(root, "release-identity.tf"));
        var allTerraform = string.Join("\n", ReadTerraformFiles().Select(f => f.Text));

        Assert.Equal(
            new[] { "build", "release" },
            Regex.Matches(release, @"^resource\s+""azurerm_user_assigned_identity""\s+""(?<n>[^""]+)""", RegexOptions.Multiline)
                .Select(m => m.Groups["n"].Value).ToArray());

        Assert.Equal(
            new[] { "wesback/airlinedemo" },
            Regex.Matches(release, @"github_repository\s*=\s*""([^""]+)""").Select(m => m.Groups[1].Value).ToArray());
        Assert.Equal(
            new[] { "api://AzureADTokenExchange" },
            Regex.Matches(release, @"github_audience\s*=\s*""([^""]+)""").Select(m => m.Groups[1].Value).Distinct().ToArray());
        Assert.Equal(
            new[] { "repo:${local.github_repository}:ref:refs/heads/main", "repo:${local.github_repository}:environment:production-release" },
            Regex.Matches(release, @"subject\s*=\s*""(repo:[^""]+)""").Select(m => m.Groups[1].Value).ToArray());
        Assert.Equal(
            new[] { "[local.github_audience]", "[local.github_audience]" },
            Regex.Matches(release, @"audience\s*=\s*(\[[^\]]+\])").Select(m => m.Groups[1].Value).ToArray());

        Assert.Equal(
            new (string Name, string Role)[]
            {
                ("build_acr_push", "AcrPush"),
                ("release_acr_push", "AcrPush"),
                ("release_signing_key", "Key Vault Crypto User"),
                ("release_signing_certificate", "Key Vault Certificate User"),
                ("release_container_app", "Container Apps Contributor"),
            },
            RoleAssignmentBlocks(release)
                .Select(b => (Name: b.Name, Role: RoleDefinitionName.Match(b.Body).Groups[1].Value))
                .ToArray());

        Assert.Equal(new[] { "AcrPull" }, AppAcrGrantRoles(allTerraform));

        Assert.Equal(
            new[] { "build_identity_client_id", "release_identity_client_id" },
            Regex.Matches(release, @"^output\s+""([^""]+)""", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToArray());
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
    public void Grant_through_module_and_variable_principals_is_counted()
    {
        const string terraform = """
            resource "azurerm_role_assignment" "worker_acr_push" {
              scope                = azurerm_container_registry.acr.id
              role_definition_name = "AcrPush"
              principal_id         = module.worker.principal_id
            }

            resource "azurerm_role_assignment" "reader_acr_pull" {
              scope                = azurerm_container_registry.acr.id
              role_definition_name = "AcrPull"
              principal_id         = var.reader_principal_id
            }
            """;

        Assert.Equal(new[] { "AcrPush", "AcrPull" }, AppAcrGrantRoles(terraform));
    }

    [Fact]
    public void Module_and_variable_principals_on_non_acr_scope_are_not_counted()
    {
        const string terraform = """
            resource "azurerm_role_assignment" "worker_kv" {
              scope                = azurerm_key_vault.kv.id
              role_definition_name = "Key Vault Secrets User"
              principal_id         = module.worker.principal_id
            }

            resource "azurerm_role_assignment" "reader_kv" {
              scope                = azurerm_key_vault.kv.id
              role_definition_name = "Key Vault Secrets User"
              principal_id         = var.reader_principal_id
            }
            """;

        Assert.Empty(AppAcrGrantRoles(terraform));
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

    [Fact]
    public void App_grant_to_unrelated_identity_is_not_counted()
    {
        const string terraform = """
            resource "azurerm_role_assignment" "other_acr_push" {
              scope                = azurerm_container_registry.acr.id
              role_definition_name = "AcrPush"
              principal_id         = azurerm_user_assigned_identity.other.principal_id
            }

            resource "azurerm_role_assignment" "other_storage_acr_push" {
              scope                = azurerm_container_registry.acr.id
              role_definition_name = "AcrPush"
              principal_id         = azurerm_storage_account.store.identity[0].principal_id
            }
            """;

        Assert.Empty(AppAcrGrantRoles(terraform));
    }

    [Fact]
    public void Grant_through_local_build_principal_is_not_counted()
    {
        const string terraform = """
            locals {
              build_principal = azurerm_user_assigned_identity.build.principal_id
            }

            resource "azurerm_role_assignment" "build_via_local" {
              scope                = azurerm_container_registry.acr.id
              role_definition_name = "AcrPush"
              principal_id         = local.build_principal
            }
            """;

        Assert.Empty(AppAcrGrantRoles(terraform));
    }

    [Fact]
    public void App_grant_after_nested_block_is_counted()
    {
        const string terraform = """
            resource "azurerm_role_assignment" "app_acr_push_nested" {
              role_definition_name = "AcrPush"
              condition {
                version = "2.0"
              }
              principal_id = azurerm_user_assigned_identity.app.principal_id
              scope        = azurerm_container_registry.acr.id
            }
            """;

        Assert.Equal(new[] { "AcrPush" }, AppAcrGrantRoles(terraform));
    }

    [Fact]
    public void Brace_inside_heredoc_does_not_end_role_assignment_block()
    {
        const string terraform = """
            resource "azurerm_role_assignment" "app_acr_push_heredoc" {
              description = <<-EOT
                closing brace } and "unbalanced quote
              EOT
              role_definition_name = "AcrPush"
              principal_id         = azurerm_user_assigned_identity.app.principal_id
              scope                = azurerm_container_registry.acr.id
            }
            """;

        Assert.Equal(new[] { "AcrPush" }, AppAcrGrantRoles(terraform));
    }

    [Fact]
    public void Brace_inside_string_does_not_end_role_assignment_block()
    {
        const string terraform = """
            resource "azurerm_role_assignment" "app_acr_push_described" {
              description          = "grants push; see docs {v2}} and # not a comment"
              role_definition_name = "AcrPush"
              principal_id         = azurerm_user_assigned_identity.app.principal_id
              scope                = azurerm_container_registry.acr.id
            }
            """;

        Assert.Equal(new[] { "AcrPush" }, AppAcrGrantRoles(terraform));
    }

    [Fact]
    public void Brace_inside_line_comment_does_not_end_role_assignment_block()
    {
        const string terraform = """
            resource "azurerm_role_assignment" "app_acr_push_line_comment" {
              # closing brace } before the attributes
              role_definition_name = "AcrPush"
              principal_id         = azurerm_user_assigned_identity.app.principal_id
              scope                = azurerm_container_registry.acr.id
            }
            """;

        Assert.Equal(new[] { "AcrPush" }, AppAcrGrantRoles(terraform));
    }

    [Fact]
    public void Brace_inside_block_comment_does_not_end_role_assignment_block()
    {
        const string terraform = """
            resource "azurerm_role_assignment" "app_acr_push_block_comment" {
              /* closing brace } inside a block comment */
              role_definition_name = "AcrPush"
              principal_id         = azurerm_user_assigned_identity.app.principal_id
              scope                = azurerm_container_registry.acr.id
            }
            """;

        Assert.Equal(new[] { "AcrPush" }, AppAcrGrantRoles(terraform));
    }

    private static List<string> AppAcrGrantRoles(string terraform)
    {
        var roles = new List<string>();
        foreach (var (_, body) in RoleAssignmentBlocks(terraform))
        {
            if (!MayGrantToApp(terraform, body) || !MayScopeToAcr(terraform, body))
            {
                continue;
            }

            var role = RoleDefinitionName.Match(body);
            roles.Add(role.Success ? role.Groups[1].Value : string.Empty);
        }

        return roles;
    }

    // Reads each azurerm_role_assignment body up to its closing brace, so nested blocks cannot end it early.
    private static IEnumerable<(string Name, string Body)> RoleAssignmentBlocks(string text)
    {
        foreach (Match header in RoleAssignmentHeader.Matches(text))
        {
            var start = header.Index + header.Length;
            yield return (header.Groups["name"].Value, text[start..FindBlockEnd(text, start)]);
        }
    }

    // Returns the index of the '}' that closes a block whose body starts at `start`.
    // Braces inside quoted strings and comments are skipped. An unclosed block runs to the end of the file,
    // which keeps its attributes under the checks.
    private static int FindBlockEnd(string text, int start)
    {
        var depth = 1;
        var i = start;
        while (i < text.Length)
        {
            if (text[i] == '"')
            {
                i = SkipQuoted(text, i);
                continue;
            }

            if (text[i] == '<' && TryReadHeredoc(text, i) is { } heredocEnd)
            {
                i = heredocEnd;
                continue;
            }

            if (text[i] == '#' || text.AsSpan(i).StartsWith("//"))
            {
                var newline = text.IndexOf('\n', i);
                i = newline < 0 ? text.Length : newline;
                continue;
            }

            if (text.AsSpan(i).StartsWith("/*"))
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 2;
                continue;
            }

            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}' && --depth == 0)
            {
                return i;
            }

            i++;
        }

        return text.Length;
    }

    // Returns the index just past a heredoc (<<MARKER or <<-MARKER) that starts at `open`, or null when none starts there.
    // The body ends at the first line that holds only the marker; an unterminated heredoc runs to the end of the file.
    private static int? TryReadHeredoc(string text, int open)
    {
        var header = HeredocStart.Match(text, open);
        if (!header.Success || header.Index != open)
        {
            return null;
        }

        var marker = header.Groups["marker"].Value;
        var lineStart = header.Index + header.Length;
        while (lineStart < text.Length)
        {
            var lineEnd = text.IndexOf('\n', lineStart);
            var end = lineEnd < 0 ? text.Length : lineEnd;
            if (text[lineStart..end].Trim() == marker)
            {
                return end;
            }

            lineStart = end + 1;
        }

        return text.Length;
    }

    // Returns the index just past the closing quote of the string starting at `open`, honouring backslash escapes.
    private static int SkipQuoted(string text, int open)
    {
        var i = open + 1;
        while (i < text.Length && text[i] != '\n')
        {
            if (text[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (text[i] == '"')
            {
                return i + 1;
            }

            i++;
        }

        return Math.Min(i, text.Length);
    }

    // Counts a grant only when its principal is shown to reference the app identity, directly or through locals.
    private static bool MayGrantToApp(string terraform, string roleAssignmentBody)
    {
        var principal = PrincipalAttribute.Match(roleAssignmentBody);
        return principal.Success && MayResolveToApp(terraform, principal.Groups["expr"].Value, MaxIndirectionDepth);
    }

    private static bool MayResolveToApp(string terraform, string expression, int depth)
    {
        if (AppIdentityReference.IsMatch(expression))
        {
            return true;
        }

        if (depth == 0)
        {
            return false;
        }

        foreach (Match reference in IndirectReference.Matches(expression))
        {
            // Variables and module outputs are supplied outside these files, so they could be the app identity.
            if (reference.Groups[1].Value != "local")
            {
                return true;
            }

            var definition = DefinitionText(terraform, reference.Groups[2].Value);
            if (definition is not null && MayResolveToApp(terraform, definition, depth - 1))
            {
                return true;
            }
        }

        return false;
    }

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
