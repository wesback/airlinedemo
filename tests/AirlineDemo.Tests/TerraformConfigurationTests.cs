using System.Text.Json;
using System.Text.RegularExpressions;

namespace AirlineDemo.Tests;

public sealed class TerraformConfigurationTests
{
    [Fact]
    public void TerraformRoot_PinsTerraformAndAzureProviderWithCheckedInLock()
    {
        var versions = LoadText("terraform", "versions.tf");
        var lockFile = LoadText("terraform", ".terraform.lock.hcl");

        Assert.Contains("required_version = \"= 1.9.8\"", versions, StringComparison.Ordinal);
        Assert.Contains("source  = \"hashicorp/azurerm\"", versions, StringComparison.Ordinal);
        Assert.Contains("version = \"= 4.62.0\"", versions, StringComparison.Ordinal);
        Assert.Contains("registry.terraform.io/hashicorp/azurerm", lockFile,
            StringComparison.Ordinal);
        Assert.Contains("version     = \"4.62.0\"", lockFile, StringComparison.Ordinal);
        Assert.Contains("h1:", lockFile, StringComparison.Ordinal);
    }

    [Fact]
    public void TerraformVariables_ValidateApprovedTypedSettingsAndExplicitDefaultRegion()
    {
        var variables = LoadText("terraform", "variables.tf");
        var example = LoadText("terraform", "examples", "demo.tfvars");

        Assert.Contains("variable \"deployment_name\"", variables, StringComparison.Ordinal);
        Assert.Contains("variable \"resource_group_name\"", variables, StringComparison.Ordinal);
        Assert.Contains("variable \"region\"", variables, StringComparison.Ordinal);
        Assert.Contains("type        = string", variables, StringComparison.Ordinal);
        Assert.Contains("type        = number", variables, StringComparison.Ordinal);
        Assert.Contains("validation", variables, StringComparison.Ordinal);
        Assert.Contains("default     = \"swedencentral\"", variables, StringComparison.Ordinal);
        Assert.Contains("region", example, StringComparison.Ordinal);
        Assert.Contains("= \"swedencentral\"", example, StringComparison.Ordinal);
        Assert.Contains("container_image", example, StringComparison.Ordinal);
        Assert.Contains("container_port", example, StringComparison.Ordinal);
        Assert.Contains("container_allowed_source_ranges", example, StringComparison.Ordinal);
        Assert.Contains("container_min_replicas", example, StringComparison.Ordinal);
        Assert.Contains("container_max_replicas", example, StringComparison.Ordinal);
        Assert.Contains("Serverless General Purpose", example, StringComparison.Ordinal);
        Assert.Contains("system-assigned managed identity", example, StringComparison.Ordinal);
        Assert.Contains("firewall-restricted", example, StringComparison.Ordinal);
        Assert.Contains("= 90", example, StringComparison.Ordinal);
        Assert.Contains("= 30000", example, StringComparison.Ordinal);
    }

    [Fact]
    public void TerraformExample_IsCredentialFreeAndPublishesApplicationOutputs()
    {
        var example = LoadText("terraform", "examples", "demo.tfvars");
        var outputs = LoadText("terraform", "outputs.tf");

        Assert.Contains("airlinedemo-swc-demo", example, StringComparison.Ordinal);
        Assert.Contains("rg-airlinedemo-swc-demo", example, StringComparison.Ordinal);
        Assert.DoesNotContain("subscription", example, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", example, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("output \"deployment_name\"", outputs, StringComparison.Ordinal);
        Assert.Contains("output \"resource_group_name\"", outputs, StringComparison.Ordinal);
        Assert.Contains("output \"region\"", outputs, StringComparison.Ordinal);
        Assert.Contains("output \"application_deployment\"", outputs, StringComparison.Ordinal);
        Assert.Contains("No resource endpoints or credentials", outputs,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TerraformModules_CreateTaggedDemoBoundaryWithSeparatedStorage()
    {
        var root = LoadText("terraform", "main.tf");
        var boundary = LoadText("terraform", "modules", "demo-boundary", "main.tf");
        var boundaryOutputs = LoadText("terraform", "modules", "demo-boundary", "outputs.tf");

        Assert.Contains("module \"demo_boundary\"", root, StringComparison.Ordinal);
        Assert.Contains("resource \"azurerm_resource_group\" \"this\"", boundary,
            StringComparison.Ordinal);
        Assert.Contains("tags     = var.tags", boundary, StringComparison.Ordinal);
        Assert.Contains("deployment  = var.deployment_name", root, StringComparison.Ordinal);
        Assert.Contains("owner       = var.owner", root, StringComparison.Ordinal);
        Assert.Contains("cost_center = var.cost_center", root, StringComparison.Ordinal);
        Assert.Contains("resource \"azurerm_storage_account\" \"evidence\"", boundary,
            StringComparison.Ordinal);
        Assert.Contains("evidence_storage_account_id", boundaryOutputs,
            StringComparison.Ordinal);
        Assert.DoesNotContain("runtime_host", boundary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TerraformEvidenceStorage_IsPrivateVersionedAndRetainedWithoutAHostPolicy()
    {
        var boundary = LoadText("terraform", "modules", "demo-boundary", "main.tf");
        var boundaryVariables = LoadText(
            "terraform", "modules", "demo-boundary", "variables.tf");

        Assert.Contains("public_network_access_enabled   = false", boundary,
            StringComparison.Ordinal);
        Assert.Contains("shared_access_key_enabled       = false", boundary,
            StringComparison.Ordinal);
        Assert.Contains("versioning_enabled = true", boundary, StringComparison.Ordinal);
        Assert.Contains("resource \"azurerm_storage_management_policy\" \"evidence\"",
            boundary, StringComparison.Ordinal);
        Assert.Contains("delete_after_days_since_creation = var.evidence_retention_days",
            boundary, StringComparison.Ordinal);
        Assert.Contains("evidence_retention_days == 90", boundaryVariables,
            StringComparison.Ordinal);

        Assert.DoesNotContain("runtime_host", boundary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("azurerm_storage_container", boundary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TerraformObservability_CreatesNinetyDayWorkspaceAndApplicationInsights()
    {
        var root = LoadText("terraform", "main.tf");
        var observability = LoadText("terraform", "modules", "observability", "main.tf");
        var outputs = LoadText("terraform", "outputs.tf");

        Assert.Contains("module \"observability\"", root, StringComparison.Ordinal);
        Assert.Contains("resource \"azurerm_log_analytics_workspace\" \"this\"",
            observability, StringComparison.Ordinal);
        Assert.Contains("resource \"azurerm_application_insights\" \"this\"",
            observability, StringComparison.Ordinal);
        Assert.Contains("retention_in_days   = var.retention_days", observability,
            StringComparison.Ordinal);
        Assert.Contains("retention_days == 90", LoadText(
            "terraform", "modules", "observability", "variables.tf"), StringComparison.Ordinal);
        Assert.Contains("output \"application_insights_id\"", outputs,
            StringComparison.Ordinal);
        Assert.Contains("output \"log_analytics_workspace_id\"", outputs,
            StringComparison.Ordinal);
        Assert.DoesNotContain("connection_string", outputs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("instrumentation_key", outputs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TerraformConfiguration_UsesOnlyApprovedBoundedResources()
    {
        var terraformRoot = new DirectoryInfo(Path.Combine(FindRepositoryRoot(), "terraform"));
        var terraformDirectory = terraformRoot
            .EnumerateFiles("*.tf", SearchOption.TopDirectoryOnly)
            .Concat(new DirectoryInfo(Path.Combine(terraformRoot.FullName, "modules"))
                .EnumerateFiles("*.tf", SearchOption.AllDirectories));
        var configuration = string.Join(
            Environment.NewLine,
            terraformDirectory.Select(file => File.ReadAllText(file.FullName)));

        Assert.Contains("source = \"./modules/demo-boundary\"", configuration,
            StringComparison.Ordinal);
        Assert.Contains("source = \"./modules/observability\"", configuration,
            StringComparison.Ordinal);
        Assert.Contains("source = \"./modules/container-apps\"", configuration,
            StringComparison.Ordinal);
        Assert.Contains("source = \"./modules/sql\"", configuration,
            StringComparison.Ordinal);
        Assert.Contains("source = \"./modules/document-intelligence\"", configuration,
            StringComparison.Ordinal);
        Assert.Contains("source = \"./modules/ai\"", configuration,
            StringComparison.Ordinal);

        var resourceTypes = Regex.Matches(
                configuration,
                @"resource\s+""(?<type>[^""]+)""\s+""[^""]+""")
            .Select(match => match.Groups["type"].Value)
            .ToArray();
        var approvedResourceTypes = new HashSet<string>(
            [
            "azurerm_cognitive_account",
            "azurerm_cognitive_deployment",
            "azurerm_application_insights",
            "azurerm_log_analytics_workspace",
            "azurerm_mssql_database",
            "azurerm_mssql_firewall_rule",
            "azurerm_mssql_server",
            "azurerm_resource_group",
            "azurerm_storage_account",
            "azurerm_storage_management_policy",
            "azurerm_container_app_environment",
            "azurerm_container_app",
            "azurerm_container_registry",
            "azurerm_user_assigned_identity",
            "azurerm_role_assignment"
            ],
            StringComparer.Ordinal);

        Assert.Equal(18, resourceTypes.Length);
        Assert.Empty(resourceTypes.Except(approvedResourceTypes, StringComparer.Ordinal));
        Assert.Equal(
            approvedResourceTypes.Count,
            resourceTypes.Distinct(StringComparer.Ordinal).Count());

        var moduleNames = Regex.Matches(
                configuration,
                @"module\s+""(?<name>[^""]+)""\s*\{")
            .Select(match => match.Groups["name"].Value)
            .ToArray();

        Assert.Equal(
            ["ai", "container_apps", "demo_boundary", "document_intelligence", "observability", "sql"],
            moduleNames.Order(StringComparer.Ordinal).ToArray());

        var declarationHeaders = string.Join(
            Environment.NewLine,
            Regex.Matches(
                    configuration,
                    @"(?:resource|module)\s+""[^""]+""(?:\s+""[^""]+"")?")
                .Select(match => match.Value));
        foreach (var prohibitedInfrastructure in new[]
                 {
                     "azurerm_fabric",
                     "azurerm_search_service",
                     "azurerm_kubernetes_cluster",
                     "azurerm_servicebus",
                     "azurerm_api_management",
                     "azurerm_consumption_reservation",
                     "azurerm_capacity_reservation",
                     "azurerm_function_app_flex_consumption",
                     "azurerm_service_plan",
                     "azurerm_storage_container",
                     "azurerm_virtual_network",
                     "azurerm_private_endpoint",
                     "foundry_agent_service",
                     "microsoft_agent_framework"
                 })
        {
            Assert.DoesNotContain(prohibitedInfrastructure, declarationHeaders,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TerraformBootstrap_IsolatedFromTheDisposableWorkloadRoot()
    {
        var bootstrapFiles = new DirectoryInfo(
                Path.Combine(FindRepositoryRoot(), "terraform", "bootstrap"))
            .EnumerateFiles("*.tf", SearchOption.TopDirectoryOnly)
            .ToArray();
        var bootstrap = string.Join(
            Environment.NewLine,
            bootstrapFiles.Select(file => File.ReadAllText(file.FullName)));
        var workloadRoot = string.Join(
            Environment.NewLine,
            new DirectoryInfo(Path.Combine(FindRepositoryRoot(), "terraform"))
                .EnumerateFiles("*.tf", SearchOption.TopDirectoryOnly)
                .Select(file => File.ReadAllText(file.FullName)));

        var resourceTypes = Regex.Matches(
                bootstrap,
                @"resource\s+""(?<type>[^""]+)""\s+""[^""]+""")
            .Select(match => match.Groups["type"].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "azurerm_resource_group",
                "azurerm_role_assignment",
                "azurerm_storage_account",
                "azurerm_storage_container"
            ],
            resourceTypes);
        Assert.DoesNotContain("module ", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("terraform/bootstrap", workloadRoot,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source = \"./bootstrap\"", workloadRoot,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rg-airlinedemo-swc-demo", bootstrap,
            StringComparison.Ordinal);
        Assert.DoesNotContain("var.resource_group_name", bootstrap,
            StringComparison.Ordinal);
        Assert.Contains("azurerm_resource_group.state", bootstrap,
            StringComparison.Ordinal);
        Assert.Contains("var.state_resource_group_name", bootstrap,
            StringComparison.Ordinal);
        Assert.Contains("azurerm_storage_account.state", bootstrap,
            StringComparison.Ordinal);
        Assert.Contains("azurerm_storage_container.state", bootstrap,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TerraformBootstrap_UsesPrivateStorageAndAnExplicitDenyByDefaultFirewall()
    {
        var bootstrap = LoadTerraformBootstrapConfiguration();
        var variables = LoadText("terraform", "bootstrap", "variables.tf");

        Assert.Contains("account_kind                    = \"StorageV2\"", bootstrap,
            StringComparison.Ordinal);
        Assert.Contains("min_tls_version                 = \"TLS1_2\"", bootstrap,
            StringComparison.Ordinal);
        Assert.Contains("https_traffic_only_enabled      = true", bootstrap,
            StringComparison.Ordinal);
        Assert.Contains("allow_nested_items_to_be_public = false", bootstrap,
            StringComparison.Ordinal);
        Assert.Contains("shared_access_key_enabled       = false", bootstrap,
            StringComparison.Ordinal);
        Assert.Contains("container_access_type = \"private\"", bootstrap,
            StringComparison.Ordinal);
        Assert.Contains("default_action = \"Deny\"", bootstrap,
            StringComparison.Ordinal);
        Assert.Contains("ip_rules       = var.approved_operator_ip_ranges", bootstrap,
            StringComparison.Ordinal);
        Assert.Contains("bypass         = []", bootstrap, StringComparison.Ordinal);
        Assert.Contains("variable \"approved_operator_ip_ranges\"", variables,
            StringComparison.Ordinal);
        Assert.DoesNotContain("default     =", variables[variables.IndexOf(
                "variable \"approved_operator_ip_ranges\"", StringComparison.Ordinal)..]
            .Split("variable \"", StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void TerraformBootstrap_GrantsOnlyContainerScopedBlobDataContributor()
    {
        var bootstrap = LoadTerraformBootstrapConfiguration();
        var roleStart = bootstrap.IndexOf(
            "resource \"azurerm_role_assignment\"",
            StringComparison.Ordinal);
        Assert.True(roleStart >= 0);
        var role = bootstrap[roleStart..];

        Assert.Contains("role_definition_name = \"Storage Blob Data Contributor\"", role,
            StringComparison.Ordinal);
        Assert.Contains("principal_id         = var.deployment_principal_object_id", role,
            StringComparison.Ordinal);
        Assert.Contains(
            "scope                = azurerm_storage_container.state.resource_manager_id",
            role, StringComparison.Ordinal);
        Assert.DoesNotContain("role_definition_name = \"Owner\"", bootstrap,
            StringComparison.Ordinal);
        Assert.DoesNotContain("role_definition_name = \"Contributor\"", bootstrap,
            StringComparison.Ordinal);
        Assert.DoesNotContain("azurerm_resource_group.state.id", role,
            StringComparison.Ordinal);
        Assert.DoesNotContain("azurerm_storage_account.state.id", role,
            StringComparison.Ordinal);
        Assert.DoesNotContain("subscription_id", role, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TerraformRoot_UsesEntraAuthenticatedAzureBlobBackendWithoutCommittedSecrets()
    {
        var backend = LoadText("terraform", "backend.tf");
        var terraformRoot = new DirectoryInfo(Path.Combine(FindRepositoryRoot(), "terraform"));
        var stateFiles = terraformRoot
            .EnumerateFiles("*.tfstate*", SearchOption.AllDirectories)
            .ToArray();

        Assert.Contains("backend \"azurerm\"", backend, StringComparison.Ordinal);
        Assert.Contains("use_azuread_auth = true", backend, StringComparison.Ordinal);
        Assert.Contains("container_name       = \"tfstate\"", backend,
            StringComparison.Ordinal);
        Assert.Contains("key                  = \"airlinedemo.tfstate\"", backend,
            StringComparison.Ordinal);
        Assert.DoesNotContain("access_key", backend, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sas_token", backend, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection_string", backend, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(stateFiles);
        Assert.DoesNotContain("backend \"azurerm\"", LoadTerraformBootstrapConfiguration(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TerraformBootstrapInstructions_DocumentIndependentApprovalOwnershipAndCleanup()
    {
        var instructions = LoadText("terraform", "bootstrap", "README.md");

        Assert.Contains("independent", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approval", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("terraform init", instructions, StringComparison.Ordinal);
        Assert.Contains("terraform plan", instructions, StringComparison.Ordinal);
        Assert.Contains("Owner:", instructions, StringComparison.Ordinal);
        Assert.Contains("Cost allocation:", instructions, StringComparison.Ordinal);
        Assert.Contains("Retention:", instructions, StringComparison.Ordinal);
        Assert.Contains("Separate cleanup path:", instructions, StringComparison.Ordinal);
        Assert.Contains("90 days", instructions, StringComparison.Ordinal);
        Assert.Contains("never be part of", instructions, StringComparison.Ordinal);
        Assert.Contains("disposable demo teardown", instructions, StringComparison.Ordinal);
        Assert.Contains("Storage Blob Data Contributor", instructions,
            StringComparison.Ordinal);
        Assert.Contains("Entra authentication", instructions, StringComparison.Ordinal);
        Assert.Contains("native lease locking", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void TerraformWorkloadModules_ConfigureApprovedContainerAppsSqlAndAiBoundaries()
    {
        var root = LoadText("terraform", "main.tf");
        var containerApps = LoadText("terraform", "modules", "container-apps", "main.tf");
        var sql = LoadText("terraform", "modules", "sql", "main.tf");
        var documentIntelligence = LoadText(
            "terraform", "modules", "document-intelligence", "main.tf");
        var ai = LoadText("terraform", "modules", "ai", "main.tf");

        Assert.Contains("module \"container_apps\"", root, StringComparison.Ordinal);
        Assert.Contains("resource \"azurerm_container_app_environment\" \"this\"",
            containerApps, StringComparison.Ordinal);
        Assert.Contains("resource \"azurerm_container_app\" \"this\"", containerApps,
            StringComparison.Ordinal);
        Assert.Contains("resource \"azurerm_container_registry\" \"application\"",
            containerApps, StringComparison.Ordinal);
        Assert.Contains("admin_enabled       = false", containerApps,
            StringComparison.Ordinal);
        Assert.Contains("image  = var.container_image", containerApps,
            StringComparison.Ordinal);
        Assert.Contains("target_port      = var.container_port", containerApps,
            StringComparison.Ordinal);
        Assert.Contains("dynamic \"ip_security_restriction\"", containerApps,
            StringComparison.Ordinal);
        Assert.Contains("min_replicas = var.min_replicas", containerApps,
            StringComparison.Ordinal);
        Assert.Contains("max_replicas = var.max_replicas", containerApps,
            StringComparison.Ordinal);
        Assert.Contains("azurerm_mssql_server", sql, StringComparison.Ordinal);
        Assert.Contains("sku_name                    = \"GP_S_Gen5_2\"", sql,
            StringComparison.Ordinal);
        Assert.Contains("min_capacity                = 0.5", sql, StringComparison.Ordinal);
        Assert.Contains("auto_pause_delay_in_minutes = 60", sql, StringComparison.Ordinal);
        Assert.Contains("azurerm_mssql_firewall_rule", sql, StringComparison.Ordinal);
        Assert.Contains("kind                = \"FormRecognizer\"", documentIntelligence,
            StringComparison.Ordinal);
        Assert.Contains("kind                = \"OpenAI\"", ai, StringComparison.Ordinal);
        Assert.Contains("azurerm_cognitive_deployment", ai, StringComparison.Ordinal);
    }

    [Fact]
    public void TerraformAiModule_RequiresTheApprovedModelVersionDeploymentTypeAndQuota()
    {
        var rootVariables = LoadText("terraform", "variables.tf");
        var aiVariables = LoadText("terraform", "modules", "ai", "variables.tf");
        var ai = LoadText("terraform", "modules", "ai", "main.tf");

        Assert.Contains("variable \"azure_openai_model\"", rootVariables,
            StringComparison.Ordinal);
        Assert.Contains("variable \"azure_openai_model_version\"", rootVariables,
            StringComparison.Ordinal);
        Assert.Contains("variable \"azure_openai_deployment_type\"", rootVariables,
            StringComparison.Ordinal);
        Assert.Contains("variable \"azure_openai_quota_tokens_minute\"", rootVariables,
            StringComparison.Ordinal);
        Assert.Contains("condition     = var.azure_openai_deployment_type == \"Standard\"",
            rootVariables, StringComparison.Ordinal);
        Assert.Contains("condition     = var.azure_openai_quota_tokens_minute == 30000",
            rootVariables, StringComparison.Ordinal);
        Assert.Contains("version = var.model_version", ai, StringComparison.Ordinal);
        Assert.Contains("name     = var.deployment_type", ai, StringComparison.Ordinal);
        Assert.Contains("capacity = var.quota_tokens_per_minute / 1000", ai,
            StringComparison.Ordinal);
        Assert.Contains("variable \"model_name\"", aiVariables, StringComparison.Ordinal);
        Assert.Contains("variable \"model_version\"", aiVariables, StringComparison.Ordinal);
        Assert.DoesNotContain("default     = \"gpt-", aiVariables,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TerraformWorkloadModules_UseTheTaggedDemoGroupAndNonSecretOutputs()
    {
        var root = LoadText("terraform", "main.tf");
        var outputs = LoadText("terraform", "outputs.tf");
        var moduleFiles = new[]
        {
            LoadText("terraform", "modules", "container-apps", "main.tf"),
            LoadText("terraform", "modules", "sql", "main.tf"),
            LoadText("terraform", "modules", "document-intelligence", "main.tf"),
            LoadText("terraform", "modules", "ai", "main.tf")
        };

        Assert.Contains("resource_group_name = module.demo_boundary.resource_group_name",
            root, StringComparison.Ordinal);
        Assert.All(moduleFiles, module => Assert.Contains("tags = var.tags", module,
            StringComparison.Ordinal));
        Assert.Contains("container_app_id", outputs, StringComparison.Ordinal);
        Assert.Contains("container_app_registry_id", outputs, StringComparison.Ordinal);
        Assert.Contains("sql_server_fully_qualified_domain_name", outputs,
            StringComparison.Ordinal);
        Assert.Contains("document_intelligence_endpoint", outputs,
            StringComparison.Ordinal);
        Assert.Contains("azure_openai_endpoint", outputs, StringComparison.Ordinal);
        Assert.DoesNotContain("primary_access_key", outputs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection_string", outputs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", outputs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TerraformWorkloadModules_UsePublicFirewallRestrictedEndpointsWithoutNetworkInfrastructure()
    {
        var containerApps = LoadText("terraform", "modules", "container-apps", "main.tf");
        var configuration = string.Join(
            Environment.NewLine,
            new[]
            {
                containerApps,
                LoadText("terraform", "modules", "sql", "main.tf"),
                LoadText("terraform", "modules", "document-intelligence", "main.tf"),
                LoadText("terraform", "modules", "ai", "main.tf")
            });

        Assert.Contains("external_enabled = true", containerApps,
            StringComparison.Ordinal);
        Assert.Contains("dynamic \"ip_security_restriction\"", containerApps,
            StringComparison.Ordinal);
        Assert.Contains("allowed_source_ranges", containerApps, StringComparison.Ordinal);
        Assert.Contains("action           = \"Allow\"", containerApps,
            StringComparison.Ordinal);
        Assert.Contains("default_action = \"Deny\"", configuration,
            StringComparison.Ordinal);
        Assert.Contains("start_ip_address = \"0.0.0.0\"", configuration,
            StringComparison.Ordinal);
        Assert.Contains("end_ip_address   = \"0.0.0.0\"", configuration,
            StringComparison.Ordinal);
        Assert.DoesNotContain("azurerm_virtual_network", configuration,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("azurerm_private_endpoint", configuration,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("virtual_network_subnet_id", configuration,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("local-exec", configuration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TerraformConfiguration_RemovesRetiredFunctionsAndDeploymentExecutionContracts()
    {
        var terraformRoot = new DirectoryInfo(Path.Combine(FindRepositoryRoot(), "terraform"));
        var files = terraformRoot
            .EnumerateFiles("*.tf", SearchOption.AllDirectories)
            .Where(file => !file.FullName.Contains(
                Path.Combine("terraform", "bootstrap"), StringComparison.Ordinal))
            .Concat([
                new FileInfo(Path.Combine(terraformRoot.FullName, "README.md")),
                new FileInfo(Path.Combine(terraformRoot.FullName, "examples", "demo.tfvars")),
                new FileInfo(Path.Combine(terraformRoot.FullName, "outputs.tf"))
            ]);
        var configuration = string.Join(
            Environment.NewLine,
            files.Select(file => File.ReadAllText(file.FullName)));

        foreach (var retiredContract in new[]
                 {
                     "Functions",
                     "Durable",
                     "dotnet-isolated",
                     "Function App",
                     "function_app_endpoint",
                     "local-exec",
                     "terraform apply -auto-approve",
                     "sql migration",
                     "fixture loading"
                 })
        {
            Assert.DoesNotContain(retiredContract, configuration,
                StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("container_image", configuration, StringComparison.Ordinal);
        Assert.Contains("container_port", configuration, StringComparison.Ordinal);
        Assert.Contains("container_allowed_source_ranges", configuration,
            StringComparison.Ordinal);
        Assert.Contains("container_min_replicas", configuration, StringComparison.Ordinal);
        Assert.Contains("container_max_replicas", configuration, StringComparison.Ordinal);
        Assert.Contains("admin_enabled       = false", configuration,
            StringComparison.Ordinal);
        Assert.Contains("min_replicas = var.min_replicas", configuration,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TerraformContainerApps_SeparatesRuntimeAndMigrationPrincipalsFromDeploymentCredentials()
    {
        var root = LoadText("terraform", "main.tf");
        var containerApps = LoadText("terraform", "modules", "container-apps", "main.tf");
        var moduleOutputs = LoadText("terraform", "modules", "container-apps", "outputs.tf");
        var outputs = LoadText("terraform", "outputs.tf");

        Assert.Contains("identity {\n    type = \"SystemAssigned\"\n  }", containerApps,
            StringComparison.Ordinal);
        Assert.Contains(
            "resource \"azurerm_user_assigned_identity\" \"migration\"",
            containerApps,
            StringComparison.Ordinal);
        Assert.Contains(
            "condition     = azurerm_container_app.this.identity[0].principal_id != azurerm_user_assigned_identity.migration.principal_id",
            containerApps,
            StringComparison.Ordinal);
        Assert.Contains(
            "value       = azurerm_container_app.this.identity[0].principal_id",
            moduleOutputs,
            StringComparison.Ordinal);
        Assert.Contains(
            "value       = azurerm_user_assigned_identity.migration.principal_id",
            moduleOutputs,
            StringComparison.Ordinal);
        Assert.Contains("output \"container_app_runtime_principal_id\"", outputs,
            StringComparison.Ordinal);
        Assert.Contains("output \"migration_identity_principal_id\"", outputs,
            StringComparison.Ordinal);
        Assert.Contains("container_app_runtime_principal_id", root, StringComparison.Ordinal);
        Assert.Contains("migration_identity_principal_id", root, StringComparison.Ordinal);

        Assert.DoesNotContain("deployment_principal", containerApps,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deployment_credential", containerApps,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("client_secret", outputs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection_string", outputs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TerraformContainerApps_GrantsOnlyExactResourceScopedRuntimeDataPlaneRoles()
    {
        var root = LoadText("terraform", "main.tf");
        var containerApps = LoadText("terraform", "modules", "container-apps", "main.tf");

        Assert.Contains(
            "evidence_storage_account_id = module.demo_boundary.evidence_storage_account_id",
            root,
            StringComparison.Ordinal);
        Assert.Contains(
            "document_intelligence_id    = module.document_intelligence.account_id",
            root,
            StringComparison.Ordinal);
        Assert.Contains(
            "azure_openai_id             = module.ai.account_id",
            root,
            StringComparison.Ordinal);

        Assert.Contains("scope                = var.evidence_storage_account_id", containerApps,
            StringComparison.Ordinal);
        Assert.Contains("role_definition_name = \"Storage Blob Data Reader\"", containerApps,
            StringComparison.Ordinal);
        Assert.Contains("scope                = var.document_intelligence_id", containerApps,
            StringComparison.Ordinal);
        Assert.Contains("role_definition_name = \"Cognitive Services User\"", containerApps,
            StringComparison.Ordinal);
        Assert.Contains("scope                = var.azure_openai_id", containerApps,
            StringComparison.Ordinal);
        Assert.Contains("role_definition_name = \"Cognitive Services OpenAI User\"", containerApps,
            StringComparison.Ordinal);
        Assert.Contains(
            "principal_id         = azurerm_container_app.this.identity[0].principal_id",
            containerApps,
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            Regex.Matches(containerApps, "resource \"azurerm_role_assignment\"").Count);
        Assert.DoesNotContain("role_definition_name = \"Owner\"", containerApps,
            StringComparison.Ordinal);
        Assert.DoesNotContain("role_definition_name = \"Contributor\"", containerApps,
            StringComparison.Ordinal);
        Assert.DoesNotContain("subscription_id", containerApps,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("function_app", containerApps,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deployment_principal", containerApps,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IdentityMapping_CoversLifecycleScopesReuseAndAssignmentOwnership()
    {
        var mappingText = LoadText("deployment", "identity-mapping.json");
        using var mapping = JsonDocument.Parse(mappingText);
        var identities = mapping.RootElement.GetProperty("identities");
        var expectedNames = new[]
        {
            "deploymentPrincipal",
            "stateBootstrapAccess",
            "containerAppRuntime",
            "migrationIdentity"
        };

        Assert.Equal(
            expectedNames.Order(StringComparer.Ordinal),
            identities.EnumerateObject().Select(property => property.Name)
                .Order(StringComparer.Ordinal));

        foreach (var name in expectedNames)
        {
            var identity = identities.GetProperty(name);
            Assert.True(identity.TryGetProperty("lifecycle", out _));
            Assert.True(identity.TryGetProperty("permittedScopes", out var scopes));
            Assert.Equal(JsonValueKind.Array, scopes.ValueKind);
            Assert.NotEmpty(scopes.EnumerateArray());
            Assert.True(identity.TryGetProperty("prohibitedReuse", out var prohibited));
            Assert.Equal(JsonValueKind.Array, prohibited.ValueKind);
            Assert.NotEmpty(prohibited.EnumerateArray());
            Assert.True(identity.TryGetProperty("assignments", out var assignments));
            Assert.Equal(JsonValueKind.Array, assignments.ValueKind);
            Assert.NotEmpty(assignments.EnumerateArray());
            foreach (var assignment in assignments.EnumerateArray())
            {
                Assert.True(assignment.TryGetProperty("ownership", out var ownership));
                Assert.NotEqual(JsonValueKind.Null, ownership.ValueKind);
            }
        }

        Assert.Equal(
            "terraform-output",
            identities.GetProperty("containerAppRuntime").GetProperty("principalId")
                .GetProperty("source").GetString());
        Assert.Equal(
            "container_app_runtime_principal_id",
            identities.GetProperty("containerAppRuntime").GetProperty("principalId")
                .GetProperty("output").GetString());
        Assert.Equal(
            "migration_identity_principal_id",
            identities.GetProperty("migrationIdentity").GetProperty("principalId")
                .GetProperty("output").GetString());
        Assert.NotEqual(
            identities.GetProperty("containerAppRuntime").GetProperty("principalId")
                .GetProperty("output").GetString(),
            identities.GetProperty("migrationIdentity").GetProperty("principalId")
                .GetProperty("output").GetString());
        Assert.Contains(
            "separately-supplied-sql-entra-administrator",
            mappingText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("clientSecret", mappingText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storageKey", mappingText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connectionString", mappingText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deploymentPrincipalValue", mappingText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Functions", mappingText, StringComparison.Ordinal);

        var runtime = identities.GetProperty("containerAppRuntime");
        foreach (var scope in runtime.GetProperty("permittedScopes").EnumerateArray())
        {
            var role = scope.TryGetProperty("roleDefinition", out var roleProperty)
                ? roleProperty.GetString()
                : null;
            Assert.NotEqual("Owner", role);
            Assert.NotEqual("Contributor", role);
            Assert.NotEqual("subscription", scope.GetProperty("scopeKind").GetString());
        }

        var runtimeAssignments = identities.GetProperty("containerAppRuntime")
            .GetProperty("assignments").EnumerateArray();
        Assert.All(runtimeAssignments, assignment =>
            Assert.Equal("terraform-managed", assignment.GetProperty("ownership").GetString()));
        Assert.All(
            identities.GetProperty("deploymentPrincipal").GetProperty("assignments")
                .EnumerateArray(),
            assignment => Assert.Equal(
                "terraform-bootstrap-managed",
                assignment.GetProperty("ownership").GetString()));
        Assert.All(
            identities.GetProperty("stateBootstrapAccess").GetProperty("assignments")
                .EnumerateArray(),
            assignment => Assert.Equal(
                "terraform-bootstrap-managed",
                assignment.GetProperty("ownership").GetString()));
        Assert.All(
            identities.GetProperty("migrationIdentity").GetProperty("assignments")
                .EnumerateArray(),
            assignment => Assert.Equal(
                "externally-supplied-sql-entra-administrator",
                assignment.GetProperty("ownership").GetString()));

        var distinctAssertions = mapping.RootElement
            .GetProperty("distinctIdentityAssertions").EnumerateArray().ToArray();
        Assert.Equal(3, distinctAssertions.Length);
        Assert.Contains(
            distinctAssertions,
            assertion => assertion.GetProperty("assertion").GetString() == "must-be-distinct");
    }

    [Fact]
    public void SqlMigrationProcedure_RequiresEntraAdminPathAndProhibitsRuntimeCredentialReuse()
    {
        var procedure = LoadText("deployment", "sql-migration-procedure.md");

        Assert.Contains("Entra", procedure, StringComparison.Ordinal);
        Assert.Contains("versioned", procedure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("separately supplied SQL Entra administrator path", procedure,
            StringComparison.Ordinal);
        Assert.Contains("Container Apps system-assigned runtime identity", procedure,
            StringComparison.Ordinal);
        Assert.Contains("must never be used", procedure, StringComparison.Ordinal);
        Assert.Contains("Terraform-managed user-assigned migration identity", procedure,
            StringComparison.Ordinal);
        Assert.Contains("not created by the workload Terraform", procedure,
            StringComparison.Ordinal);
        Assert.Contains("No app-registration client secret", procedure,
            StringComparison.Ordinal);
        Assert.Contains("storage key", procedure, StringComparison.Ordinal);
        Assert.Contains("connection string", procedure, StringComparison.Ordinal);
        Assert.Contains("secret-store integration", procedure, StringComparison.Ordinal);
        Assert.DoesNotContain("azurerm_container_app.this.identity", procedure,
            StringComparison.Ordinal);
        Assert.DoesNotContain("sql_password", procedure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TerraformResourceInventory_CoversResourcesBoundariesOutputsAndIdentities()
    {
        var inventoryText = LoadText("deployment", "resource-inventory.json");
        using var inventory = JsonDocument.Parse(inventoryText);
        var root = inventory.RootElement;

        Assert.Equal("terraform/", root.GetProperty("sourceOfTruth")
            .GetProperty("workloadRoot").GetString());
        Assert.True(root.GetProperty("sourceOfTruth")
            .GetProperty("stateBackendBootstrapIsOutsideWorkloadRoot").GetBoolean());
        Assert.Equal("deployment/identity-mapping.json", root.GetProperty("sourceOfTruth")
            .GetProperty("permissionInventory").GetString());

        var modules = root.GetProperty("modules").EnumerateArray().ToArray();
        Assert.Equal(7, modules.Length);
        Assert.Equal(
            [
                "ai",
                "container_apps",
                "demo_boundary",
                "document_intelligence",
                "observability",
                "protected_state_bootstrap",
                "sql"
            ],
            modules.Select(module => module.GetProperty("module").GetString())
                .Order(StringComparer.Ordinal));
        Assert.All(modules, module =>
        {
            Assert.NotEmpty(module.GetProperty("source").GetString() ?? string.Empty);
            Assert.NotEmpty(module.GetProperty("lifecycleOwner").GetString() ?? string.Empty);
            Assert.NotEmpty(module.GetProperty("dataBoundary").GetString() ?? string.Empty);
            Assert.NotEmpty(module.GetProperty("networkExposure").GetString() ?? string.Empty);
            Assert.NotEmpty(module.GetProperty("costTags").GetString() ?? string.Empty);
            Assert.NotEmpty(module.GetProperty("resources").EnumerateArray());
        });

        var expectedResources = new[]
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
            "module.ai.azurerm_cognitive_deployment.this",
            "terraform.bootstrap.azurerm_resource_group.state",
            "terraform.bootstrap.azurerm_storage_account.state",
            "terraform.bootstrap.azurerm_storage_container.state",
            "terraform.bootstrap.azurerm_role_assignment.deployment_state_blob_contributor"
        };
        var resources = root.GetProperty("resources").EnumerateArray().ToArray();
        Assert.Equal(expectedResources.Length, resources.Length);
        Assert.Equal(
            expectedResources.Order(StringComparer.Ordinal),
            resources.Select(resource => resource.GetProperty("address").GetString())
                .Order(StringComparer.Ordinal));

        Assert.All(resources, resource =>
        {
            Assert.NotEmpty(resource.GetProperty("module").GetString() ?? string.Empty);
            Assert.NotEmpty(resource.GetProperty("lifecycleOwner").GetString() ?? string.Empty);
            Assert.NotEmpty(resource.GetProperty("ownershipBoundary").GetString() ?? string.Empty);
            Assert.NotEmpty(resource.GetProperty("dataBoundary").GetString() ?? string.Empty);
            Assert.NotEmpty(resource.GetProperty("networkExposure").GetString() ?? string.Empty);
            Assert.Equal(JsonValueKind.Array, resource.GetProperty("costTags").ValueKind);
            Assert.NotEmpty(resource.GetProperty("costTags").EnumerateArray());
            Assert.Equal(JsonValueKind.Array, resource.GetProperty("nonSecretOutputs").ValueKind);
        });

        var boundaries = root.GetProperty("ownershipBoundaries");
        Assert.Equal("airlinedemo-demo",
            boundaries.GetProperty("disposableDemo").GetProperty("lifecycleOwner").GetString());
        Assert.Equal("airlinedemo-platform",
            boundaries.GetProperty("externallyOwnedRemoteState")
                .GetProperty("lifecycleOwner").GetString());
        Assert.Contains("must not be destroyed",
            boundaries.GetProperty("externallyOwnedRemoteState")
                .GetProperty("teardown").GetString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        var outputPolicy = root.GetProperty("nonSecretOutputPolicy");
        Assert.Contains("client secrets",
            outputPolicy.GetProperty("neverPublished").EnumerateArray()
                .Select(value => value.GetString()),
            StringComparer.Ordinal);
        Assert.Contains("private and separate",
            outputPolicy.GetProperty("runtimeHostStorageAccess").GetString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no runtime-host storage account",
            outputPolicy.GetProperty("runtimeHostStorageAccess").GetString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        var identities = root.GetProperty("identityInventory").EnumerateArray().ToArray();
        Assert.Equal(
            ["deployment", "runtime", "migration", "terraform-state"],
            identities.Select(identity => identity.GetProperty("name").GetString()));
        Assert.All(identities, identity =>
        {
            Assert.NotEmpty(identity.GetProperty("lifecycle").GetString() ?? string.Empty);
            Assert.NotEmpty(identity.GetProperty("minimumScope").GetString() ?? string.Empty);
            Assert.NotEmpty(identity.GetProperty("prohibitedReuse").EnumerateArray());
            Assert.NotEmpty(identity.GetProperty("assignments").EnumerateArray());
            Assert.All(identity.GetProperty("assignments").EnumerateArray(), assignment =>
            {
                Assert.NotEmpty(assignment.GetProperty("scope").GetString() ?? string.Empty);
                Assert.NotEmpty(assignment.GetProperty("role").GetString() ?? string.Empty);
                Assert.NotEmpty(assignment.GetProperty("implementation").GetString() ?? string.Empty);
                Assert.NotEmpty(assignment.GetProperty("ownership").GetString() ?? string.Empty);
                Assert.NotEmpty(assignment.GetProperty("approval").GetString() ?? string.Empty);
            });
        });

        var migrationAssignment = identities.Single(identity =>
                identity.GetProperty("name").GetString() == "migration")
            .GetProperty("assignments").EnumerateArray().Single();
        Assert.Equal("externally-supplied-sql-entra-administrator",
            migrationAssignment.GetProperty("ownership").GetString());
        Assert.Contains("Tenant-level",
            migrationAssignment.GetProperty("approval").GetString() ?? string.Empty,
            StringComparison.Ordinal);

        var implementation = root.GetProperty("roleAssignmentImplementation");
        Assert.Contains("terraform/bootstrap/main.tf",
            implementation.GetProperty("protectedStateWork").GetString() ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Contains("terraform/modules/container-apps/main.tf",
            implementation.GetProperty("identityOperatingModelWork").GetString() ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Equal("deployment/identity-mapping.json",
            implementation.GetProperty("canonicalPermissionDetails").GetString());
    }

    [Fact]
    public void TerraformResourceInventory_MatchesEveryTerraformResourceDeclaration()
    {
        var repositoryRoot = FindRepositoryRoot();
        var terraformRoot = new DirectoryInfo(Path.Combine(repositoryRoot, "terraform"));
        var inventoryText = LoadText("deployment", "resource-inventory.json");
        using var inventory = JsonDocument.Parse(inventoryText);
        var workloadModuleNamesByDirectory = Regex.Matches(
                File.ReadAllText(Path.Combine(terraformRoot.FullName, "main.tf")),
                @"(?ms)module\s+""(?<name>[^""]+)""\s*\{.*?source\s*=\s*""\./modules/(?<directory>[^""]+)""")
            .ToDictionary(
                match => match.Groups["directory"].Value,
                match => match.Groups["name"].Value,
                StringComparer.Ordinal);

        var inventoryResources = inventory.RootElement
            .GetProperty("resources")
            .EnumerateArray()
            .Select(resource => resource.GetProperty("address").GetString())
            .Where(address => address is not null)
            .Cast<string>()
            .ToArray();
        var declaredResources = terraformRoot
            .EnumerateFiles("*.tf", SearchOption.AllDirectories)
            .SelectMany(file => Regex.Matches(
                    File.ReadAllText(file.FullName),
                    @"(?m)^\s*resource\s+""(?<type>[^""]+)""\s+""(?<name>[^""]+)""")
                .Select(match => ResolveTerraformResourceAddress(
                    terraformRoot,
                    file,
                    workloadModuleNamesByDirectory,
                    match.Groups["type"].Value,
                    match.Groups["name"].Value)))
            .ToArray();

        Assert.Equal(inventoryResources.Length, inventoryResources.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(declaredResources.Length, declaredResources.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            declaredResources.Order(StringComparer.Ordinal).ToArray(),
            inventoryResources.Order(StringComparer.Ordinal).ToArray());
    }

    private static string ResolveTerraformResourceAddress(
        DirectoryInfo terraformRoot,
        FileInfo file,
        IReadOnlyDictionary<string, string> workloadModuleNamesByDirectory,
        string resourceType,
        string resourceName)
    {
        var relativeSegments = Path.GetRelativePath(terraformRoot.FullName, file.FullName)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        return relativeSegments switch
        {
            [ "bootstrap", .. ] =>
                $"terraform.bootstrap.{resourceType}.{resourceName}",
            [ "modules", var moduleDirectory, .. ] when
                workloadModuleNamesByDirectory.TryGetValue(moduleDirectory, out var moduleName) =>
                $"module.{moduleName}.{resourceType}.{resourceName}",
            [ .. ] when relativeSegments.Length == 1 =>
                $"{resourceType}.{resourceName}",
            _ => throw new InvalidOperationException(
                $"Terraform resource declaration is outside a supported root: {file.FullName}")
        };
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

    private static string LoadTerraformBootstrapConfiguration()
    {
        var bootstrapDirectory = new DirectoryInfo(Path.Combine(
            FindRepositoryRoot(), "terraform", "bootstrap"));
        return string.Join(
            Environment.NewLine,
            bootstrapDirectory
                .EnumerateFiles("*.tf", SearchOption.TopDirectoryOnly)
                .Select(file => File.ReadAllText(file.FullName)));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AirlineDemo.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
