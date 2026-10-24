using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

// Read-only contract checks over the repository Terraform fixtures. No Azure credentials are needed.
public class ContainerHostingContractTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "infra", "terraform")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    static string Tf(string file) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "infra", "terraform", file));

    // Returns the text of the first block that starts with `header`, including its braces.
    static string Block(string text, string header)
    {
        var start = text.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"missing block: {header}");
        var depth = 0;
        for (var i = text.IndexOf('{', start); i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(start, i - start + 1);
                }
            }
        }
        throw new InvalidOperationException($"unterminated block: {header}");
    }

    // AC: M-13 / AC-018. ACR is Premium, private, with a registry private endpoint on privatelink.azurecr.io;
    // the Container Apps environment uses the app subnet; ingress and egress deny by default.
    [Fact]
    public void M13_HostingAndRegistryArePrivateAndDenyByDefault()
    {
        var hosting = Tf("container-hosting.tf");
        var main = Tf("main.tf");

        var acr = Block(hosting, "resource \"azurerm_container_registry\" \"acr\"");
        Assert.Matches(@"sku\s*=\s*""Premium""", acr);
        Assert.Matches(@"admin_enabled\s*=\s*false", acr);
        Assert.Matches(@"public_network_access_enabled\s*=\s*false", acr);

        Assert.Matches(@"acr\s*=\s*""privatelink\.azurecr\.io""", main);
        Assert.Matches(@"subresource\s*=\s*""registry""", main);
        Assert.Matches(@"resource\s+""azurerm_private_dns_zone_virtual_network_link""", main);

        var env = Block(hosting, "resource \"azurerm_container_app_environment\" \"demo\"");
        Assert.Matches(@"infrastructure_subnet_id\s*=\s*azurerm_subnet\.app\.id", env);

        var ingress = Block(hosting, "ingress {");
        Assert.Matches(@"allow_insecure_connections\s*=\s*false", ingress);
        Assert.Matches(@"for_each\s*=\s*local\.presenter_reviewer_rules", ingress);
        Assert.Matches(@"presenter_reviewer_rules\s*=\s*\{[^}]*var\.presenter_reviewer_cidrs", main);
        // Exactly one Allow and one Deny restriction, so no additional broad ingress rule can be added.
        Assert.Single(Regex.Matches(ingress, @"action\s*=\s*""Allow"""));
        Assert.Single(Regex.Matches(ingress, @"action\s*=\s*""Deny"""));
        // The Allow restriction is bound to the configured CIDR for its own entry.
        Assert.Matches(@"name\s*=\s*""presenter-reviewer-\$\{ip_security_restriction\.key\}""[\s\S]*?action\s*=\s*""Allow""[\s\S]*?ip_address_range\s*=\s*ip_security_restriction\.value", ingress);
        // Empty presenter_reviewer_cidrs produces only a Deny-all restriction on 0.0.0.0/0 and no Allow entries.
        Assert.Matches(@"length\(var\.presenter_reviewer_cidrs\)\s*==\s*0\s*\?\s*toset\(\[""0\.0\.0\.0/0""\]\)\s*:\s*toset\(\[\]\)", ingress);
        Assert.Matches(@"name\s*=\s*""deny-all-empty-allowlist""[\s\S]*?action\s*=\s*""Deny""[\s\S]*?ip_address_range\s*=\s*ip_security_restriction\.value", ingress);

        var ingressDeny = Block(main, "resource \"azurerm_network_security_rule\" \"ingress_deny_all\"");
        Assert.Matches(@"access\s*=\s*""Deny""", ingressDeny);
        var egressAllow = Block(main, "resource \"azurerm_network_security_rule\" \"egress_allow\"");
        Assert.Matches(@"for_each\s*=\s*local\.egress_rules", egressAllow);
        Assert.Matches(@"access\s*=\s*""Allow""", egressAllow);
        var egressDeny = Block(main, "resource \"azurerm_network_security_rule\" \"egress_deny_all\"");
        Assert.Matches(@"access\s*=\s*""Deny""", egressDeny);

        // Empty-list and non-allowlisted-egress deny cases are proven by the mocked-plan tests.
        var networkTests = Tf("tests/network-boundaries.tftest.hcl");
        Assert.Matches(@"run\s+""[^""]*empty[^""]*""", networkTests);
        Assert.Matches(@"run\s+""[^""]*(egress|allowlist)[^""]*""", networkTests);
    }

    // AC: M-14 / NFR-001. Secure transport and encryption settings; the passcode is only a Key Vault secret reference.
    [Fact]
    public void M14_HostingUsesSecureTransportAndNoInlineSecrets()
    {
        var hosting = Tf("container-hosting.tf");
        var main = Tf("main.tf");

        var sql = Block(main, "resource \"azurerm_mssql_server\" \"sql\"");
        Assert.Matches(@"minimum_tls_version\s*=\s*""1\.2""", sql);
        Assert.Matches(@"resource\s+""azurerm_mssql_server_transparent_data_encryption""\s+""sql""", main);

        var storage = Block(main, "resource \"azurerm_storage_account\" \"blob\"");
        Assert.Matches(@"min_tls_version\s*=\s*""TLS1_2""", storage);
        Assert.Matches(@"infrastructure_encryption_enabled\s*=\s*true", storage);
        // Microsoft-managed encryption: no customer-managed key block on the Storage account.
        Assert.DoesNotMatch(@"customer_managed_key", storage);

        // The azurerm provider exposes no TLS-version argument for Cognitive Services accounts, so the TLS 1.2
        // floor is the platform default and is verified by review, not by this test. This check only ensures
        // the configuration sets no TLS override that could lower it. The model account is pinned to
        // Microsoft-managed encryption (no customer-managed key), no local auth, and public access disabled.
        var model = Block(main, "resource \"azurerm_cognitive_account\" \"model\"");
        Assert.DoesNotMatch(@"(?i)tls", model);
        Assert.DoesNotMatch(@"customer_managed_key", model);
        Assert.Matches(@"local_auth_enabled\s*=\s*false", model);
        Assert.Matches(@"public_network_access_enabled\s*=\s*false", model);

        Assert.Matches(@"secret\s*\{[^}]*key_vault_secret_id\s*=\s*var\.demo_passcode_secret_uri", hosting);
        Assert.Matches(@"secret_name\s*=\s*""demo-passcode""", hosting);
        Assert.DoesNotMatch(@"(?i)value\s*=\s*""[^""]*passcode", hosting);

        var connection = Block(hosting, "env {\n        name  = \"ConnectionStrings__CaseState\"");
        Assert.Matches(@"Authentication=Active Directory Managed Identity", connection);
        Assert.Matches(@"Encrypt=True", connection);
        Assert.Matches(@"TrustServerCertificate=False", connection);
        Assert.DoesNotMatch(@"(?i)password|accountkey|sharedaccesssignature|access_?token", connection);

        var all = hosting + main + Tf("variables.tf");
        Assert.DoesNotMatch(@"(?i)(access_key|primary_access_key|primary_key)\s*=\s*""", all);
        Assert.DoesNotMatch(@"(?i)password\s*=\s*""", all);
    }

    // AC: M-18 / AC-018. Identity wiring, private dependencies, public access disabled, digest-pinned image.
    [Fact]
    public void M18_HostingRequiresPrivateDependenciesAndDigest()
    {
        var hosting = Tf("container-hosting.tf");
        var main = Tf("main.tf");

        Assert.Matches(@"identity_ids\s*=\s*\[azurerm_user_assigned_identity\.app\.id\]", hosting);
        Assert.Matches(@"registry\s*\{[^}]*identity\s*=\s*azurerm_user_assigned_identity\.app\.id", hosting);
        Assert.Matches(@"principal_id\s*=\s*azurerm_user_assigned_identity\.app\.principal_id", hosting);
        Assert.Matches(@"role_definition_name\s*=\s*""AcrPull""", hosting);
        Assert.Matches(@"AZURE_CLIENT_ID", hosting);

        Assert.Matches(@"public_network_access_enabled\s*=\s*false", Block(hosting, "resource \"azurerm_container_registry\" \"acr\""));
        Assert.Matches(@"public_network_access_enabled\s*=\s*false", Block(main, "resource \"azurerm_mssql_server\" \"sql\""));
        Assert.Matches(@"public_network_access_enabled\s*=\s*false", Block(main, "resource \"azurerm_storage_account\" \"blob\""));
        Assert.Matches(@"public_network_access_enabled\s*=\s*false", Block(main, "resource \"azurerm_cognitive_account\" \"model\""));

        Assert.Matches(@"image\s*=\s*""\$\{azurerm_container_registry\.acr\.login_server\}/airline-api@\$\{var\.image_digest\}""", hosting);
        Assert.DoesNotMatch(@"airline-api:", hosting);

        // Model traffic is limited to the approved egress destinations; everything else is denied.
        Assert.Matches(@"egress_rules\s*=\s*\{[^}]*var\.egress_allowed_destinations", main);
        var egressAllowRule = Block(main, "resource \"azurerm_network_security_rule\" \"egress_allow\"");
        Assert.Matches(@"for_each\s*=\s*local\.egress_rules", egressAllowRule);
        Assert.Matches(@"destination_address_prefix\s*=\s*each\.value", egressAllowRule);
        // Every Allow rule must target only an approved destination: the private-endpoint subnet, the app subnet,
        // the internal API Management subnet, one configured egress destination, or one entry of the pinned
        // Container Apps platform list. Service tags such as Internet, VirtualNetwork or AzureCloud are rejected.
        foreach (Match rule in Regex.Matches(main, @"resource\s+""azurerm_network_security_rule""\s+""\w+""\s*\{"))
        {
            var ruleBlock = Block(main, rule.Value);
            if (Regex.IsMatch(ruleBlock, @"access\s*=\s*""Allow"""))
            {
                Assert.DoesNotMatch(@"destination_address_prefixes\s*=", ruleBlock);
                Assert.Matches(@"(?m)^\s*destination_address_prefix\s*=\s*(local\.app_subnet_cidr|local\.private_endpoint_subnet_cidr|local\.gateway_subnet_cidr|each\.value|each\.value\.destination)\s*$", ruleBlock);
                // each.value is only a safe destination when the rule iterates the configured egress allowlist.
                if (Regex.IsMatch(ruleBlock, @"(?m)destination_address_prefix\s*=\s*each\.value\s*$"))
                {
                    Assert.Matches(@"(?m)^\s*for_each\s*=\s*local\.egress_rules\s*$", ruleBlock);
                }
                // each.value.destination is only safe when the rule iterates the pinned platform list below.
                if (Regex.IsMatch(ruleBlock, @"destination_address_prefix\s*=\s*each\.value\.destination"))
                {
                    Assert.Matches(@"(?m)^\s*for_each\s*=\s*local\.container_apps_platform_egress\s*$", ruleBlock);
                }
            }
        }

        // The platform list is exactly what a workload-profiles environment with an NSG needs, and nothing broader.
        var platform = Block(main, "container_apps_platform_egress = {");
        Assert.Equal(
            new[] { "168.63.129.16", "AzureActiveDirectory", "AzureFrontDoor.FirstParty", "AzureMonitor", "MicrosoftContainerRegistry" },
            Regex.Matches(platform, @"destination\s*=\s*""([^""]+)""").Select(m => m.Groups[1].Value).OrderBy(d => d, StringComparer.Ordinal).ToArray());
        Assert.Matches(@"(?m)^\s*dns\s*=\s*\{[^}]*ports\s*=\s*\[""53""\]", platform);
        Assert.Equal(4, Regex.Matches(platform, @"protocol\s*=\s*""Tcp"",\s*ports\s*=\s*\[""443""\]").Count);

        Assert.Matches(@"sha256", Tf("variables.tf"));
    }

    // AC: NFR-006 / NFR-010. Hosting inputs have no defaults and carry their validations.
    [Fact]
    public void NFR006_RejectsMissingHostingInputs()
    {
        var variables = Tf("variables.tf");
        // Org-specific inputs stay required. Names, ranges and the egress list derive from name_prefix or default (names.tf).
        string[] required =
        {
            "subscription_id", "tenant_id", "location", "presenter_reviewer_cidrs", "budget_amount",
            "budget_start_date", "budget_contact_emails", "terraform_state_backend",
        };
        string[] derived =
        {
            "resource_group_name", "app_subnet_cidr", "private_endpoint_subnet_cidr", "sql_server_name",
            "storage_account_name", "key_vault_name", "model_account_name", "model_endpoint_subdomain", "acr_name",
            "container_apps_environment_name", "container_app_name",
        };

        foreach (var name in required)
        {
            var block = Block(variables, $"variable \"{name}\"");
            Assert.DoesNotMatch(@"(?m)^\s*default\s*=", block);
        }

        // A derived name has a null default, so an unset name falls back to name_prefix and is never an empty string.
        foreach (var name in derived)
        {
            Assert.Matches(@"(?m)^\s*default\s*=\s*null\s*$", Block(variables, $"variable \"{name}\""));
            Assert.Matches($@"coalesce\(var\.{name},", Tf("names.tf"));
        }
        Assert.Matches(@"\^\[a-z\]\[a-z0-9\]\{2,9\}\$", Block(variables, "variable \"name_prefix\""));

        var cidrs = Block(variables, "variable \"presenter_reviewer_cidrs\"");
        Assert.Matches(@"cidrnetmask", cidrs);
        var egress = Block(variables, "variable \"egress_allowed_destinations\"");
        Assert.Matches(@"cidrnetmask", egress);
        var digest = Block(variables, "variable \"image_digest\"");
        Assert.Matches(@"\^sha256:\[0-9a-f\]\{64\}\$", digest);

        // Two-phase deploy: the application inputs are null on the platform-only run and must be given together.
        var passcode = Block(variables, "variable \"demo_passcode_secret_uri\"");
        Assert.Matches(@"(?m)^\s*default\s*=\s*null\s*$", digest);
        Assert.Matches(@"(?m)^\s*default\s*=\s*null\s*$", passcode);
        Assert.Matches(@"\(var\.demo_passcode_secret_uri\s*==\s*null\)\s*==\s*\(var\.image_digest\s*==\s*null\)", passcode);
    }
}
