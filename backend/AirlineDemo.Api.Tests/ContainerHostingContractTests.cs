using System.Text.RegularExpressions;

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

    private static string Tf(string file) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "infra", "terraform", file));

    private static string AllTf() =>
        string.Concat(Directory.GetFiles(Path.Combine(RepoRoot(), "infra", "terraform"), "*.tf").Select(File.ReadAllText));

    private static string VariableBlock(string name)
    {
        var match = Regex.Match(Tf("variables.tf"), $"^variable \"{name}\" \\{{(.*?)^\\}}", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(match.Success, $"variable {name} must be declared.");
        return match.Groups[1].Value;
    }

    // AC: M-13
    [Fact]
    public void M13_HostingAndRegistryArePrivateAndDenyByDefault()
    {
        var hosting = Tf("container-hosting.tf");
        var main = Tf("main.tf");

        Assert.Matches(@"resource ""azurerm_container_registry"" ""acr""[\s\S]*?sku\s*=\s*""Premium""", hosting);
        Assert.Matches(@"admin_enabled\s*=\s*false", hosting);
        Assert.Matches(@"public_network_access_enabled\s*=\s*false", hosting);
        Assert.Matches(@"acr\s*=\s*\{\s*resource_id\s*=\s*azurerm_container_registry\.acr\.id,\s*subresource\s*=\s*""registry"",\s*zone\s*=\s*""acr""", main);
        Assert.Matches(@"acr\s*=\s*""privatelink\.azurecr\.io""", main);
        Assert.Matches(@"infrastructure_subnet_id\s*=\s*azurerm_subnet\.app\.id", hosting);

        // Ingress: one allow rule per configured CIDR; priority 4096 deny for everything else.
        Assert.Matches(@"for_each\s*=\s*local\.presenter_reviewer_rules[\s\S]*?access\s*=\s*""Allow""", main);
        Assert.Matches(@"priority\s*=\s*4096[\s\S]*?direction\s*=\s*""Inbound""[\s\S]*?access\s*=\s*""Deny""", main);
        // Egress: allow rules only for configured destinations and the private-endpoint subnet; deny the rest.
        Assert.Matches(@"for_each\s*=\s*local\.egress_rules[\s\S]*?access\s*=\s*""Allow""", main);
        Assert.Matches(@"priority\s*=\s*4096[\s\S]*?direction\s*=\s*""Outbound""[\s\S]*?access\s*=\s*""Deny""", main);
        Assert.Matches(@"presenter_reviewer_rules\s*=\s*\{\s*for i, c in var\.presenter_reviewer_cidrs", main);
        Assert.Matches(@"egress_rules\s*=\s*\{\s*for i, c in var\.egress_allowed_destinations", main);
    }

    // AC: M-14
    [Fact]
    public void M14_HostingUsesSecureTransportAndNoInlineSecrets()
    {
        var hosting = Tf("container-hosting.tf");
        var main = Tf("main.tf");
        var identity = Tf("identity-and-telemetry.tf");

        Assert.Matches(@"allow_insecure_connections\s*=\s*false", hosting);
        Assert.Matches(@"external_enabled\s*=\s*true", hosting);
        Assert.Matches(@"min_tls_version\s*=\s*""TLS1_2""", main);
        Assert.Matches(@"minimum_tls_version\s*=\s*""1\.2""", main);
        Assert.Matches(@"resource ""azurerm_mssql_server_transparent_data_encryption"" ""sql""", main);
        Assert.Matches(@"infrastructure_encryption_enabled\s*=\s*true", main);
        Assert.Matches(@"key_vault_secret_id\s*=\s*var\.demo_passcode_secret_uri", hosting);
        Assert.Matches(@"ConnectionStrings__CaseState", hosting);
        Assert.Matches(@"Authentication=Active Directory Managed Identity", hosting);
        Assert.Matches(@"Encrypt=True", hosting);
        Assert.Matches(@"TrustServerCertificate=False", hosting);

        // No access key, password, token, or secret value in variables, tags, or app env values.
        var forbidden = new Regex(@"(?i)(password|access_key|primary_access_key|secret_value|access_token|sas_token)\s*=\s*""[^""]+""");
        Assert.DoesNotMatch(forbidden, hosting);
        Assert.DoesNotMatch(forbidden, identity);
        Assert.DoesNotMatch(new Regex(@"(?i)\bPassword=|AccountKey=|SharedAccessSignature="), hosting);
    }

    // AC: M-18
    [Fact]
    public void M18_HostingRequiresPrivateDependenciesAndDigest()
    {
        var hosting = Tf("container-hosting.tf");
        var main = Tf("main.tf");

        Assert.Matches(@"identity_ids\s*=\s*\[azurerm_user_assigned_identity\.app\.id\]", hosting);
        Assert.Matches(@"registry\s*\{[\s\S]*?identity\s*=\s*azurerm_user_assigned_identity\.app\.id", hosting);
        Assert.Matches(@"image\s*=\s*""\$\{azurerm_container_registry\.acr\.login_server\}/airline-api@\$\{var\.image_digest\}""", hosting);

        // Private references for SQL, Blob, ACR and model traffic; public access disabled.
        Assert.Matches(@"sql\s*=\s*\{\s*resource_id\s*=\s*azurerm_mssql_server\.sql\.id", main);
        Assert.Matches(@"blob\s*=\s*\{\s*resource_id\s*=\s*azurerm_storage_account\.blob\.id", main);
        Assert.Matches(@"model\s*=\s*\{\s*resource_id\s*=\s*azurerm_cognitive_account\.model\.id", main);
        Assert.Matches(@"public_network_access_enabled\s*=\s*false", main);

        // Digest must be immutable (sha256 digest) and tag-only references must be rejected by validation.
        var digest = VariableBlock("image_digest");
        Assert.Matches(@"\^sha256:\[0-9a-f\]\{64\}\$", digest);
        Assert.DoesNotMatch(@"image\s*=\s*""[^""]*:[^""@]*""", hosting);
        Assert.DoesNotMatch(@"\bdefault\s*=", digest);
    }

    // AC: NFR-006 / NFR-010
    [Fact]
    public void NFR006_RejectsMissingHostingInputs()
    {
        string[] required =
        [
            "subscription_id", "tenant_id", "location", "resource_group_name", "vnet_address_space",
            "app_subnet_cidr", "private_endpoint_subnet_cidr", "presenter_reviewer_cidrs",
            "egress_allowed_destinations", "sql_server_name", "sql_database_name", "storage_account_name",
            "key_vault_name", "model_account_name", "model_endpoint_subdomain", "budget_amount",
            "budget_start_date", "budget_contact_emails", "terraform_state_backend", "acr_name",
            "container_apps_environment_name", "container_app_name", "image_digest", "demo_passcode_secret_uri",
        ];

        foreach (var name in required)
        {
            Assert.DoesNotMatch(@"(?m)^\s*default\s*=", VariableBlock(name));
        }

        var cidrs = Tf("variables.tf");
        Assert.Matches(@"cidrnetmask\(\s*(each\.value|v|c)\s*\)", cidrs);
        Assert.Matches(@"presenter_reviewer_cidrs[\s\S]*?cidrnetmask", cidrs);
        Assert.Matches(@"egress_allowed_destinations[\s\S]*?cidrnetmask", cidrs);
        Assert.Matches(@"\^sha256:\[0-9a-f\]\{64\}\$", VariableBlock("image_digest"));
    }
}
