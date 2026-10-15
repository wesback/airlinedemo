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
        Assert.Matches(@"action\s*=\s*""Allow""", ingress);
        // Empty presenter_reviewer_cidrs produces a Deny-all ingress restriction.
        Assert.Matches(@"length\(var\.presenter_reviewer_cidrs\)\s*==\s*0", ingress);
        Assert.Matches(@"action\s*=\s*""Deny""", ingress);

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

        // The provider exposes no TLS argument for Cognitive Services accounts; the model account is
        // therefore pinned to Microsoft-managed encryption, no customer key, and no local auth.
        var model = Block(main, "resource \"azurerm_cognitive_account\" \"model\"");
        Assert.DoesNotMatch(@"customer_managed_key", model);
        Assert.Matches(@"local_auth_enabled\s*=\s*false", model);

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
        Assert.Matches(@"destination_address_prefix\s*=\s*each\.value", main);
        Assert.Matches(@"for_each\s*=\s*local\.egress_rules", main);

        Assert.Matches(@"sha256", Tf("variables.tf"));
    }

    // AC: NFR-006 / NFR-010. Hosting inputs have no defaults and carry their validations.
    [Fact]
    public void NFR006_RejectsMissingHostingInputs()
    {
        var variables = Tf("variables.tf");
        string[] required =
        {
            "subscription_id", "tenant_id", "location", "resource_group_name", "vnet_address_space",
            "app_subnet_cidr", "private_endpoint_subnet_cidr", "presenter_reviewer_cidrs",
            "egress_allowed_destinations", "sql_server_name", "sql_database_name", "storage_account_name",
            "key_vault_name", "model_account_name", "model_endpoint_subdomain", "budget_amount",
            "budget_start_date", "budget_contact_emails", "terraform_state_backend", "acr_name",
            "container_apps_environment_name", "container_app_name", "image_digest", "demo_passcode_secret_uri",
        };

        foreach (var name in required)
        {
            var block = Block(variables, $"variable \"{name}\"");
            Assert.DoesNotMatch(@"(?m)^\s*default\s*=", block);
        }

        var cidrs = Block(variables, "variable \"presenter_reviewer_cidrs\"");
        Assert.Matches(@"cidrnetmask", cidrs);
        var egress = Block(variables, "variable \"egress_allowed_destinations\"");
        Assert.Matches(@"cidrnetmask", egress);
        var digest = Block(variables, "variable \"image_digest\"");
        Assert.Matches(@"\^sha256:\[0-9a-f\]\{64\}\$", digest);
    }
}
