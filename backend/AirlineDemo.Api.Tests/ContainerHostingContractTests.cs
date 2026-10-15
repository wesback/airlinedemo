using System.Text.RegularExpressions;
using Xunit;

namespace AirlineDemo.Api.Tests;

public class ContainerHostingContractTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "infra", "terraform", "main.tf")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string Terraform(string file) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "infra", "terraform", file));

    private static string Hosting() => Terraform("container-hosting.tf");

    private static string Variables() => Terraform("variables.tf");

    // AC: M-13 / AC-018
    [Fact]
    public void M13_HostingAndRegistryArePrivateAndDenyByDefault()
    {
        var hosting = Hosting();
        var main = Terraform("main.tf");

        Assert.Matches(@"resource\s+""azurerm_container_registry""\s+""acr""\s*\{[\s\S]*?sku\s*=\s*""Premium""", hosting);
        Assert.Matches(@"admin_enabled\s*=\s*false", hosting);
        Assert.Matches(@"public_network_access_enabled\s*=\s*false", hosting);
        Assert.Contains("subresource = \"registry\"", main);
        Assert.Contains("privatelink.azurecr.io", main);
        Assert.Matches(@"infrastructure_subnet_id\s*=\s*azurerm_subnet\.app\.id", hosting);
        Assert.Matches(@"ip_security_restriction", hosting);
        Assert.Matches(@"dynamic\s+""ip_security_restriction""", hosting);
        Assert.Matches(@"action\s*=\s*""Deny""", hosting);
        Assert.Matches(@"local\.presenter_reviewer_rules", hosting);
        Assert.Matches(@"0\.0\.0\.0/0", hosting);
        Assert.Matches(@"destination_port_range\s*=\s*""443""", Terraform("main.tf") + hosting);
        Assert.Matches(@"ingress_deny_all", Terraform("main.tf"));
        Assert.Matches(@"egress_deny_all", Terraform("main.tf"));
        Assert.Matches(@"cidrnetmask\(", Variables());
    }

    // AC: M-14 / NFR-001
    [Fact]
    public void M14_HostingUsesSecureTransportAndNoInlineSecrets()
    {
        var hosting = Hosting();
        var main = Terraform("main.tf");
        var variables = Variables();

        Assert.Matches(@"allow_insecure_connections\s*=\s*false", hosting);
        Assert.Matches(@"min_tls_version\s*=\s*""TLS1_2""", main);
        Assert.Matches(@"transparent_data_encryption_enabled\s*=\s*true|azurerm_mssql_server_transparent_data_encryption", main);
        Assert.Matches(@"infrastructure_encryption_enabled\s*=\s*true", main);
        Assert.Matches(@"Encrypt=True", hosting);
        Assert.Matches(@"TrustServerCertificate=False", hosting);
        Assert.Matches(@"key_vault_secret_id\s*=\s*var\.demo_passcode_secret_uri", hosting);
        Assert.DoesNotMatch(@"(?i)(access_key|primary_access_key|password)\s*=\s*""[^""]+""", hosting);
        Assert.DoesNotMatch(@"(?i)Password=", hosting);
        Assert.DoesNotMatch(@"(?i)AccountKey=", hosting);
        Assert.DoesNotMatch(@"(?i)default\s*=\s*""[^""]*(password|secret|token)", variables);
        Assert.Matches(@"Authentication=Active Directory Managed Identity", hosting);
        Assert.Matches(@"ConnectionStrings__CaseState", hosting);
    }

    // AC: M-18 / AC-018
    [Fact]
    public void M18_HostingRequiresPrivateDependenciesAndDigest()
    {
        var hosting = Hosting();
        var main = Terraform("main.tf");
        var variables = Variables();

        Assert.Matches(@"identity_ids\s*=\s*\[\s*azurerm_user_assigned_identity\.app\.id\s*\]", hosting);
        Assert.Matches(@"identity\s*=\s*azurerm_user_assigned_identity\.app\.id", hosting);
        Assert.Matches(@"image_digest", hosting);
        Assert.Matches(@"@\$\{var\.image_digest\}|/airline-api@", hosting);
        Assert.Matches(@"public_network_access_enabled\s*=\s*false", main);
        Assert.Matches(@"privatelink\.database\.windows\.net|\bsql\b", main);
        Assert.Matches(@"privatelink\.blob\.core\.windows\.net|\bblob\b", main);
        Assert.Matches(@"local_auth_enabled\s*=\s*false", main);
        Assert.Matches(@"\^sha256:\[0-9a-f\]\{64\}\$", variables);
        Assert.DoesNotMatch(@"image\s*=\s*""[^""]*:latest""", hosting);
    }

    // AC: NFR-006 / NFR-010
    [Fact]
    public void NFR006_RejectsMissingHostingInputs()
    {
        var variables = Variables();
        var required = new[]
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
            var block = Regex.Match(variables, $@"variable\s+""{name}""\s*\{{(?<body>[\s\S]*?)\n\}}");
            Assert.True(block.Success, $"variable {name} is not declared.");
            Assert.DoesNotMatch(@"^\s*default\s*=", block.Groups["body"].Value);
        }

        Assert.Matches(@"cidrnetmask\(", variables);
        Assert.Matches(@"\^sha256:\[0-9a-f\]\{64\}\$", variables);
        Assert.DoesNotMatch(@"variable\s+""sql_database_name""\s*\{[^}]*default", variables);
    }
}
