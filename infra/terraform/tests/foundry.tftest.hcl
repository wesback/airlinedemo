mock_provider "azurerm" {
  override_during = plan
  mock_resource "azurerm_resource_group_template_deployment" {
    defaults = {
      output_content = jsonencode({
        projectPrincipalId = { value = "66666666-6666-6666-6666-666666666666" }
        projectInternalId  = { value = "0123456789abcdef0123456789abcdef" }
      })
    }
  }
  mock_resource "azurerm_resource_group" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test"
    }
  }
  mock_resource "azurerm_user_assigned_identity" {
    defaults = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-demo-test"
      principal_id = "99999999-9999-9999-9999-999999999999"
    }
  }
  mock_resource "azurerm_role_assignment" {
    defaults = {
      principal_type = "User"
    }
  }
  mock_resource "azurerm_api_management" {
    defaults = {
      identity = {
        principal_id = "aaaaaaaa-1111-2222-3333-444444444444"
        tenant_id    = "11111111-1111-1111-1111-111111111111"
      }
    }
  }
  mock_resource "azurerm_virtual_network" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Network/virtualNetworks/vnet-demo"
    }
  }
  mock_data "azurerm_virtual_network" {
    defaults = { address_space = ["10.21.0.0/16"] }
  }
  mock_resource "azurerm_subnet" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Network/virtualNetworks/vnet-demo/subnets/snet-foundry-agent"
    }
  }
  mock_resource "azurerm_network_security_group" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Network/networkSecurityGroups/nsg-foundry-agent"
    }
  }
  mock_resource "azurerm_mssql_server" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Sql/servers/sql-demo-test"
    }
  }
  mock_resource "azurerm_storage_account" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stdemotest"
    }
  }
  mock_resource "azurerm_cognitive_account" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.CognitiveServices/accounts/aoai-demo-test"
    }
  }
  mock_resource "azurerm_key_vault" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test"
    }
  }
  mock_resource "azurerm_cosmosdb_account" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.DocumentDB/databaseAccounts/cosmos-foundry"
    }
  }
  mock_resource "azurerm_search_service" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Search/searchServices/search-foundry"
    }
  }
  mock_resource "azurerm_private_dns_zone" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Network/privateDnsZones/privatelink.synthetic.invalid"
    }
  }
}
mock_provider "azuread" {
  override_during = plan
  mock_data "azuread_client_config" {
    defaults = { object_id = "00000000-0000-0000-0000-0000000000d0" }
  }
  mock_resource "azuread_application" {
    defaults = {
      id        = "/applications/00000000-0000-0000-0000-0000000000a1"
      client_id = "00000000-0000-0000-0000-0000000000c1"
    }
  }
  mock_resource "azuread_service_principal" {
    defaults = { object_id = "00000000-0000-0000-0000-0000000000b1" }
  }
}
mock_provider "random" {}

override_module {
  target = module.foundry_store_readback[0]
  outputs = {
    scopes = {
      containers = {
        files = {
          id     = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stdemotest/blobServices/default/containers/01234567-89ab-cdef-0123-456789abcdef-azureml-blobstore"
          access = "private"
        }
        agent_files = {
          id     = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stdemotest/blobServices/default/containers/01234567-89ab-cdef-0123-456789abcdef-azureml-agent"
          access = "private"
        }
      }
      cosmosDatabaseId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.DocumentDB/databaseAccounts/cosmos-foundry/sqlDatabases/enterprise_memory"
    }
  }
}

variables {
  name_prefix                           = "demo1"
  resource_group_name                   = "rg-demo-test"
  subscription_id                       = "00000000-0000-0000-0000-000000000000"
  tenant_id                             = "11111111-1111-1111-1111-111111111111"
  location                              = "swedencentral"
  model_name                            = "gpt-demo-model"
  model_version                         = "0000-00-00"
  presenter_reviewer_cidrs              = []
  sql_admin_group_name                  = "synthetic-operators"
  sql_admin_group_object_id             = "22222222-2222-2222-2222-222222222222"
  terraform_principal_object_id         = "33333333-3333-3333-3333-333333333333"
  telemetry_reader_group_id             = "44444444-4444-4444-4444-444444444444"
  budget_amount                         = 100
  budget_start_date                     = "2026-11-01T00:00:00Z"
  budget_contact_emails                 = ["ops@example.invalid"]
  gateway_endpoint                      = "https://gateway.private.invalid/"
  gateway_approved_application_identity = "55555555-5555-5555-5555-555555555555"
  security_response_owner               = { name = "Synthetic Operator", email = "ops@example.invalid" }
  security_alert_settings               = { enabled = true, failure_threshold = 5, window_minutes = 15, evaluation_frequency_minutes = 5 }
  terraform_state_backend = {
    storage_account_name = "stdemotfstate"
    container_name       = "tfstate"
    key                  = "demo.tfstate"
    use_azuread_auth     = true
    locking_enabled      = true
  }
  foundry_state_profile = {
    cosmos_total_throughput_limit  = 5000
    cosmos_backup_interval_minutes = 240
    cosmos_backup_retention_hours  = 24
    cosmos_consistency_level       = "Session"
    search_sku                     = "basic"
    search_partitions              = 1
    search_replicas                = 1
    storage_replication_type       = "LRS"
    blob_retention_days            = 7
    container_retention_days       = 7
  }
  foundry_model_profile = {
    runtime = { name = "synthetic-runtime", version = "2026-01-01", sku = "GlobalStandard", capacity = 1 }
    judge   = { name = "synthetic-judge", version = "2026-02-01", sku = "Standard", capacity = 2 }
  }
}

run "foundry_saved_template_uses_plan_known_resource_names" {
  command = plan
  variables {
    foundry_enabled = true
  }
  assert {
    condition = (
      jsondecode(azurerm_resource_group_template_deployment.foundry[0].template_content).resources[0].properties.capabilitySettings == {
        blobStore     = "${local.rg_scope}/providers/Microsoft.Storage/storageAccounts/${azurerm_storage_account.foundry[0].name}"
        documentStore = "${local.rg_scope}/providers/Microsoft.DocumentDB/databaseAccounts/${azurerm_cosmosdb_account.foundry[0].name}"
        vectorStore   = "${local.rg_scope}/providers/Microsoft.Search/searchServices/${azurerm_search_service.foundry[0].name}"
      }
    )
    error_message = "The exact saved ARM template must be reviewable before provider-assigned IDs exist."
  }
}

run "foundry_account_project_are_private_injected_and_single_owned" {
  command = plan
  variables {
    foundry_enabled = true
  }
  assert {
    condition = (
      azurerm_resource_group_template_deployment.foundry[0].deployment_mode == "Incremental" &&
      azurerm_resource_group_template_deployment.foundry[0].debug_level == "none" &&
      azurerm_resource_group_template_deployment.foundry[0].template_content == jsonencode(local.foundry_template) &&
      jsondecode(azurerm_resource_group_template_deployment.foundry[0].parameters_content) == {
        accountName = { value = local.foundry_account_name }
        projectName = { value = "airline-demo" }
      } &&
      length(local.foundry_template.resources) == 2 &&
      toset(keys(local.foundry_template.outputs)) == toset(["projectPrincipalId", "projectInternalId"]) &&
      [for resource in local.foundry_template.resources : resource.type] ==
      ["Microsoft.CognitiveServices/accounts", "Microsoft.CognitiveServices/accounts/projects"] &&
      alltrue([for resource in local.foundry_template.resources :
        resource.apiVersion == "2026-07-15-preview" && resource.location == "swedencentral" &&
      resource.identity == { type = "SystemAssigned" }])
    )
    error_message = "The protected Incremental deployment must own only the two pinned account/project resources."
  }
  assert {
    condition = (
      local.foundry_template.resources[0].kind == "AIServices" &&
      local.foundry_template.resources[0].sku.name == "S0" &&
      local.foundry_template.resources[0].properties.allowProjectManagement == true &&
      local.foundry_template.resources[0].properties.disableLocalAuth == true &&
      local.foundry_template.resources[0].properties.publicNetworkAccess == "Disabled" &&
      local.foundry_template.resources[0].properties.networkInjections == [{
        scenario                   = "agent"
        subnetArmId                = "${local.rg_scope}/providers/Microsoft.Network/virtualNetworks/${azurerm_virtual_network.demo.name}/subnets/${azurerm_subnet.foundry_agent[0].name}"
        useMicrosoftManagedNetwork = false
      }] &&
      local.foundry_template.resources[0].properties.capabilitySettings == tomap({
        blobStore     = "${local.rg_scope}/providers/Microsoft.Storage/storageAccounts/${azurerm_storage_account.foundry[0].name}"
        documentStore = "${local.rg_scope}/providers/Microsoft.DocumentDB/databaseAccounts/${azurerm_cosmosdb_account.foundry[0].name}"
        vectorStore   = "${local.rg_scope}/providers/Microsoft.Search/searchServices/${azurerm_search_service.foundry[0].name}"
      }) &&
      local.foundry_template.resources[1].properties.capabilitySettings ==
      local.foundry_template.resources[0].properties.capabilitySettings &&
      local.foundry_template.resources[1].dependsOn ==
      ["[resourceId('Microsoft.CognitiveServices/accounts', parameters('accountName'))]"]
    )
    error_message = "Creation-time injection and both capability settings must reference the exact private subnet and BYO stores."
  }
  assert {
    condition = (
      one(azurerm_private_endpoint.foundry[0].private_service_connection).private_connection_resource_id == local.foundry_account_id &&
      one(azurerm_private_endpoint.foundry[0].private_service_connection).subresource_names == tolist(["account"]) &&
      azurerm_private_endpoint.foundry[0].subnet_id == azurerm_subnet.private_endpoints.id &&
      one(azurerm_private_endpoint.foundry[0].private_dns_zone_group).private_dns_zone_ids == tolist([
        azurerm_private_dns_zone.foundry["project"].id,
        azurerm_private_dns_zone.demo["model"].id,
        azurerm_private_dns_zone.demo["contentsafety"].id
      ]) &&
      azurerm_cognitive_deployment.model.cognitive_account_id == azurerm_cognitive_account.model.id &&
      local.foundry_account_id != azurerm_cognitive_account.model.id
    )
    error_message = "Foundry needs its own private account endpoint with all three namespaces; the retained legacy model is separate."
  }
}

run "foundry_store_readback_uses_ARM_containers_and_service_database" {
  command = plan
  module {
    source = "./modules/foundry-store-readback"
  }
  variables {
    containers = {
      files       = "01234567-89ab-cdef-0123-456789abcdef-azureml-blobstore"
      agent_files = "01234567-89ab-cdef-0123-456789abcdef-azureml-agent"
    }
    storage_account_id  = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stdemotest"
    cosmos_account_name = "cosmos-foundry"
  }
  override_data {
    target = data.azurerm_storage_container.project["files"]
    values = {
      id                    = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stdemotest/blobServices/default/containers/01234567-89ab-cdef-0123-456789abcdef-azureml-blobstore"
      container_access_type = "private"
    }
  }
  override_data {
    target = data.azurerm_storage_container.project["agent_files"]
    values = {
      id                    = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stdemotest/blobServices/default/containers/01234567-89ab-cdef-0123-456789abcdef-azureml-agent"
      container_access_type = "private"
    }
  }
  override_data {
    target = data.azurerm_cosmosdb_sql_database.project
    values = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.DocumentDB/databaseAccounts/cosmos-foundry/sqlDatabases/enterprise_memory"
    }
  }
  assert {
    condition = (
      alltrue([for key, container in data.azurerm_storage_container.project :
        container.name == var.containers[key] &&
        container.storage_account_id == var.storage_account_id &&
        container.storage_account_name == null &&
        output.scopes.containers[key].id == "${var.storage_account_id}/blobServices/default/containers/${var.containers[key]}" &&
      output.scopes.containers[key].access == "private"]) &&
      data.azurerm_cosmosdb_sql_database.project.account_name == "cosmos-foundry" &&
      data.azurerm_cosmosdb_sql_database.project.resource_group_name == "rg-demo-test" &&
      data.azurerm_cosmosdb_sql_database.project.name == "enterprise_memory" &&
      output.scopes.cosmosDatabaseId == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.DocumentDB/databaseAccounts/cosmos-foundry/sqlDatabases/enterprise_memory"
    )
    error_message = "Readback must use exact ARM account/container names and pass through discovered IDs/private ACLs without creating stores."
  }
}

run "foundry_models_pin_versions_capacity_and_all_safety_filters" {
  command = plan
  variables {
    foundry_enabled = true
  }
  assert {
    condition = (
      toset(keys(azurerm_cognitive_deployment.foundry)) == toset(["runtime", "judge"]) &&
      alltrue([for key, model in azurerm_cognitive_deployment.foundry :
        model.name == key && model.cognitive_account_id == local.foundry_account_id &&
        one(model.model).format == "OpenAI" &&
        one(model.model).name == var.foundry_model_profile[key].name &&
        one(model.model).version == var.foundry_model_profile[key].version &&
        one(model.sku).name == var.foundry_model_profile[key].sku &&
        one(model.sku).capacity == var.foundry_model_profile[key].capacity &&
        model.version_upgrade_option == "NoAutoUpgrade" &&
      model.rai_policy_name == azurerm_cognitive_account_rai_policy.foundry[0].name])
    )
    error_message = "Both approved models must use their exact explicit version, size and filter policy without automatic upgrade."
  }
  assert {
    condition = (
      azurerm_cognitive_account_rai_policy.foundry[0].cognitive_account_id == local.foundry_account_id &&
      azurerm_cognitive_account_rai_policy.foundry[0].base_policy_name == "Microsoft.Default" &&
      azurerm_cognitive_account_rai_policy.foundry[0].mode == "Blocking" &&
      toset([for filter in azurerm_cognitive_account_rai_policy.foundry[0].content_filter :
        "${filter.name}/${filter.source}/${filter.severity_threshold}/${filter.filter_enabled}/${filter.block_enabled}"
        ]) == toset([
        "Hate/Prompt/Medium/true/true", "Hate/Completion/Medium/true/true",
        "Sexual/Prompt/Medium/true/true", "Sexual/Completion/Medium/true/true",
        "Violence/Prompt/Medium/true/true", "Violence/Completion/Medium/true/true",
        "Selfharm/Prompt/Medium/true/true", "Selfharm/Completion/Medium/true/true"
      ])
    )
    error_message = "All four categories must block Medium and High inputs and outputs, not use inherited defaults."
  }
}

run "foundry_rejects_missing_model_profile" {
  command = plan
  variables {
    foundry_enabled       = true
    foundry_model_profile = null
  }
  expect_failures = [var.foundry_model_profile]
}

run "foundry_rejects_unpinned_model_version" {
  command = plan
  variables {
    foundry_enabled = true
    foundry_model_profile = {
      runtime = { name = "synthetic-runtime", version = "latest", sku = "GlobalStandard", capacity = 1 }
      judge   = { name = "synthetic-judge", version = "2026-02-01", sku = "Standard", capacity = 2 }
    }
  }
  expect_failures = [var.foundry_model_profile]
}

run "foundry_rejects_missing_judge_model" {
  command = plan
  variables {
    foundry_enabled = true
    foundry_model_profile = {
      runtime = { name = "synthetic-runtime", version = "2026-01-01", sku = "GlobalStandard", capacity = 1 }
    }
  }
  expect_failures = [var.foundry_model_profile]
}

run "foundry_rejects_provisioned_model_sku" {
  command = plan
  variables {
    foundry_enabled = true
    foundry_model_profile = {
      runtime = { name = "synthetic-runtime", version = "2026-01-01", sku = "GlobalProvisionedManaged", capacity = 1 }
      judge   = { name = "synthetic-judge", version = "2026-02-01", sku = "Standard", capacity = 2 }
    }
  }
  expect_failures = [var.foundry_model_profile]
}

run "foundry_rejects_fractional_model_capacity" {
  command = plan
  variables {
    foundry_enabled = true
    foundry_model_profile = {
      runtime = { name = "synthetic-runtime", version = "2026-01-01", sku = "GlobalStandard", capacity = 0.5 }
      judge   = { name = "synthetic-judge", version = "2026-02-01", sku = "Standard", capacity = 2 }
    }
  }
  expect_failures = [var.foundry_model_profile]
}

run "foundry_runtime_grants_use_exact_project_generated_scopes" {
  command = plan
  variables {
    foundry_enabled = true
  }
  assert {
    condition = (
      local.foundry_workspace_id == "01234567-89ab-cdef-0123-456789abcdef" &&
      toset(keys(azurerm_role_assignment.foundry_runtime)) == toset([
        "cosmos_operator", "storage_operator", "search_data", "search_operator", "files", "agent_files", "inference"
      ]) &&
      alltrue([for grant in values(azurerm_role_assignment.foundry_runtime) :
      grant.principal_id == "66666666-6666-6666-6666-666666666666" && grant.principal_type == "ServicePrincipal"]) &&
      azurerm_role_assignment.foundry_runtime["files"].scope ==
      "${azurerm_storage_account.foundry[0].id}/blobServices/default/containers/01234567-89ab-cdef-0123-456789abcdef-azureml-blobstore" &&
      azurerm_role_assignment.foundry_runtime["files"].role_definition_name == "Storage Blob Data Contributor" &&
      azurerm_role_assignment.foundry_runtime["agent_files"].scope ==
      "${azurerm_storage_account.foundry[0].id}/blobServices/default/containers/01234567-89ab-cdef-0123-456789abcdef-azureml-agent" &&
      azurerm_role_assignment.foundry_runtime["agent_files"].role_definition_name == "Storage Blob Data Owner"
    )
    error_message = "Blob data grants must bind only to exact generated project containers and project identity."
  }
  assert {
    condition = (
      alltrue([for key, expected in {
        cosmos_operator  = { scope = azurerm_cosmosdb_account.foundry[0].id, role = "Cosmos DB Operator" }
        storage_operator = { scope = azurerm_storage_account.foundry[0].id, role = "Storage Account Contributor" }
        search_data      = { scope = azurerm_search_service.foundry[0].id, role = "Search Index Data Contributor" }
        search_operator  = { scope = azurerm_search_service.foundry[0].id, role = "Search Service Contributor" }
        inference        = { scope = local.foundry_account_id, role = "Cognitive Services OpenAI User" }
        } :
        azurerm_role_assignment.foundry_runtime[key].scope == expected.scope &&
      azurerm_role_assignment.foundry_runtime[key].role_definition_name == expected.role]) &&
      azurerm_cosmosdb_sql_role_assignment.foundry_runtime[0].principal_id == "66666666-6666-6666-6666-666666666666" &&
      azurerm_cosmosdb_sql_role_assignment.foundry_runtime[0].scope == "${azurerm_cosmosdb_account.foundry[0].id}/dbs/enterprise_memory" &&
      azurerm_cosmosdb_sql_role_assignment.foundry_runtime[0].role_definition_id ==
      "${azurerm_cosmosdb_account.foundry[0].id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"
    )
    error_message = "Only documented management/search/inference grants and database-scoped Cosmos data rights are permitted."
  }
}

run "foundry_publisher_and_evaluator_have_disjoint_project_privileges" {
  command = plan
  variables {
    foundry_enabled = true
  }
  override_resource {
    target          = azurerm_role_definition.foundry_operator["publisher"]
    override_during = plan
    values = {
      role_definition_resource_id = "/subscriptions/00000000-0000-0000-0000-000000000000/providers/Microsoft.Authorization/roleDefinitions/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
    }
  }
  override_resource {
    target          = azurerm_role_definition.foundry_operator["evaluator"]
    override_during = plan
    values = {
      role_definition_resource_id = "/subscriptions/00000000-0000-0000-0000-000000000000/providers/Microsoft.Authorization/roleDefinitions/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
    }
  }
  override_resource {
    target          = azurerm_user_assigned_identity.foundry_operator["publisher"]
    override_during = plan
    values          = { principal_id = "77777777-7777-7777-7777-777777777777" }
  }
  override_resource {
    target          = azurerm_user_assigned_identity.foundry_operator["evaluator"]
    override_during = plan
    values          = { principal_id = "88888888-8888-8888-8888-888888888888" }
  }
  assert {
    condition = (
      toset(keys(azurerm_role_assignment.foundry_operator)) == toset(["publisher", "evaluator"]) &&
      azurerm_role_assignment.foundry_operator["publisher"].principal_id == "77777777-7777-7777-7777-777777777777" &&
      azurerm_role_assignment.foundry_operator["evaluator"].principal_id == "88888888-8888-8888-8888-888888888888" &&
      azurerm_role_assignment.foundry_operator["publisher"].role_definition_id != azurerm_role_assignment.foundry_operator["evaluator"].role_definition_id &&
      alltrue([for key, grant in azurerm_role_assignment.foundry_operator :
        grant.scope == local.foundry_project_id && grant.principal_type == "ServicePrincipal" &&
        grant.role_definition_id == azurerm_role_definition.foundry_operator[key].role_definition_resource_id &&
        !contains([local.foundry_principal_id, azurerm_user_assigned_identity.app.principal_id, var.terraform_principal_object_id], grant.principal_id) &&
        one(azurerm_role_definition.foundry_operator[key].permissions).actions == tolist(["Microsoft.CognitiveServices/accounts/projects/read"]) &&
        one(azurerm_role_definition.foundry_operator[key].permissions).not_actions == null &&
        one(azurerm_role_definition.foundry_operator[key].permissions).not_data_actions == null &&
      azurerm_role_definition.foundry_operator[key].assignable_scopes == tolist([azurerm_resource_group.demo.id])])
    )
    error_message = "Publisher/evaluator must be separate project-scoped identities without app, agent or Terraform identity substitution."
  }
  assert {
    condition = (
      toset(one(azurerm_role_definition.foundry_operator["publisher"].permissions).data_actions) == toset([
        "Microsoft.CognitiveServices/accounts/AIServices/agents/read",
        "Microsoft.CognitiveServices/accounts/AIServices/agents/write",
        "Microsoft.CognitiveServices/accounts/AIServices/agents/delete"
      ]) &&
      toset(one(azurerm_role_definition.foundry_operator["evaluator"].permissions).data_actions) == toset([
        "Microsoft.CognitiveServices/accounts/AIServices/evaluations/read",
        "Microsoft.CognitiveServices/accounts/AIServices/evaluations/write",
        "Microsoft.CognitiveServices/accounts/OpenAI/evals/read",
        "Microsoft.CognitiveServices/accounts/OpenAI/evals/write"
      ])
    )
    error_message = "Publisher cannot evaluate or infer; evaluator cannot mutate agents or business/store/role state."
  }
}

run "foundry_rejects_missing_generated_project_identifier" {
  command = plan
  variables {
    foundry_enabled = true
  }
  override_resource {
    target          = azurerm_resource_group_template_deployment.foundry[0]
    override_during = plan
    values          = { output_content = "{\"projectPrincipalId\":{\"value\":\"66666666-6666-6666-6666-666666666666\"}}" }
  }
  expect_failures = [
    azurerm_role_assignment.foundry_runtime["cosmos_operator"],
    azurerm_role_assignment.foundry_runtime["storage_operator"],
    azurerm_role_assignment.foundry_runtime["search_data"],
    azurerm_role_assignment.foundry_runtime["search_operator"],
    azurerm_role_assignment.foundry_runtime["files"],
    azurerm_role_assignment.foundry_runtime["agent_files"],
    azurerm_role_assignment.foundry_runtime["inference"],
    azurerm_cosmosdb_sql_role_assignment.foundry_runtime[0]
  ]
}

run "foundry_rejects_generated_identifier_scope_injection" {
  command = plan
  variables {
    foundry_enabled = true
  }
  override_resource {
    target          = azurerm_resource_group_template_deployment.foundry[0]
    override_during = plan
    values = {
      output_content = "{\"projectPrincipalId\":{\"value\":\"66666666-6666-6666-6666-666666666666\"},\"projectInternalId\":{\"value\":\"../../other-project\"}}"
    }
  }
  expect_failures = [
    azurerm_role_assignment.foundry_runtime["cosmos_operator"],
    azurerm_role_assignment.foundry_runtime["storage_operator"],
    azurerm_role_assignment.foundry_runtime["search_data"],
    azurerm_role_assignment.foundry_runtime["search_operator"],
    azurerm_role_assignment.foundry_runtime["files"],
    azurerm_role_assignment.foundry_runtime["agent_files"],
    azurerm_role_assignment.foundry_runtime["inference"],
    azurerm_cosmosdb_sql_role_assignment.foundry_runtime[0]
  ]
}

run "foundry_rejects_malformed_project_principal" {
  command = plan
  variables {
    foundry_enabled = true
  }
  override_resource {
    target          = azurerm_resource_group_template_deployment.foundry[0]
    override_during = plan
    values = {
      output_content = "{\"projectPrincipalId\":{\"value\":\"not-a-guid\"},\"projectInternalId\":{\"value\":\"0123456789abcdef0123456789abcdef\"}}"
    }
  }
  expect_failures = [
    azurerm_role_assignment.foundry_runtime["cosmos_operator"],
    azurerm_role_assignment.foundry_runtime["storage_operator"],
    azurerm_role_assignment.foundry_runtime["search_data"],
    azurerm_role_assignment.foundry_runtime["search_operator"],
    azurerm_role_assignment.foundry_runtime["files"],
    azurerm_role_assignment.foundry_runtime["agent_files"],
    azurerm_role_assignment.foundry_runtime["inference"],
    azurerm_cosmosdb_sql_role_assignment.foundry_runtime[0]
  ]
}

run "foundry_rejects_missing_discovered_blob_scope" {
  command = plan
  variables {
    foundry_enabled = true
  }
  override_module {
    target = module.foundry_store_readback[0]
    outputs = { scopes = {
      containers = {
        agent_files = {
          id     = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stdemotest/blobServices/default/containers/01234567-89ab-cdef-0123-456789abcdef-azureml-agent"
          access = "private"
        }
      }
      cosmosDatabaseId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.DocumentDB/databaseAccounts/cosmos-foundry/sqlDatabases/enterprise_memory"
    } }
  }
  expect_failures = [azurerm_role_assignment.foundry_runtime["files"]]
}

run "foundry_rejects_another_project_blob_scope" {
  command = plan
  variables {
    foundry_enabled = true
  }
  override_module {
    target = module.foundry_store_readback[0]
    outputs = { scopes = {
      containers = {
        files = {
          id     = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stdemotest/blobServices/default/containers/another-project-azureml-blobstore"
          access = "private"
        }
        agent_files = {
          id     = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stdemotest/blobServices/default/containers/01234567-89ab-cdef-0123-456789abcdef-azureml-agent"
          access = "private"
        }
      }
      cosmosDatabaseId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.DocumentDB/databaseAccounts/cosmos-foundry/sqlDatabases/enterprise_memory"
    } }
  }
  expect_failures = [azurerm_role_assignment.foundry_runtime["files"]]
}

run "foundry_rejects_public_discovered_blob_container" {
  command = plan
  variables {
    foundry_enabled = true
  }
  override_module {
    target = module.foundry_store_readback[0]
    outputs = { scopes = {
      containers = {
        files = {
          id     = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stdemotest/blobServices/default/containers/01234567-89ab-cdef-0123-456789abcdef-azureml-blobstore"
          access = "blob"
        }
        agent_files = {
          id     = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stdemotest/blobServices/default/containers/01234567-89ab-cdef-0123-456789abcdef-azureml-agent"
          access = "private"
        }
      }
      cosmosDatabaseId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.DocumentDB/databaseAccounts/cosmos-foundry/sqlDatabases/enterprise_memory"
    } }
  }
  expect_failures = [azurerm_role_assignment.foundry_runtime["files"]]
}

run "foundry_rejects_another_discovered_cosmos_database" {
  command = plan
  variables {
    foundry_enabled = true
  }
  override_module {
    target = module.foundry_store_readback[0]
    outputs = { scopes = {
      containers = {
        files = {
          id     = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stdemotest/blobServices/default/containers/01234567-89ab-cdef-0123-456789abcdef-azureml-blobstore"
          access = "private"
        }
        agent_files = {
          id     = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stdemotest/blobServices/default/containers/01234567-89ab-cdef-0123-456789abcdef-azureml-agent"
          access = "private"
        }
      }
      cosmosDatabaseId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.DocumentDB/databaseAccounts/cosmos-foundry/sqlDatabases/another_database"
    } }
  }
  expect_failures = [azurerm_cosmosdb_sql_role_assignment.foundry_runtime[0]]
}

run "foundry_gateway_has_exact_operations_and_least_project_consumer" {
  command = plan
  override_resource {
    target          = azurerm_user_assigned_identity.foundry_operator["publisher"]
    override_during = plan
    values          = { principal_id = "77777777-7777-7777-7777-777777777777" }
  }
  override_resource {
    target          = azurerm_user_assigned_identity.foundry_operator["evaluator"]
    override_during = plan
    values          = { principal_id = "88888888-8888-8888-8888-888888888888" }
  }
  variables {
    foundry_enabled  = true
    gateway_endpoint = "https://gateway.private.invalid/"
    foundry_runtime_profile = {
      copilot      = { name = "airline-copilot", version = "1", configuration_id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
      investigator = { name = "airline-investigator", version = "2", configuration_id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
    }
  }
  assert {
    condition = (
      azurerm_api_management_api.foundry[0].path == "foundry" &&
      azurerm_api_management_api.foundry[0].service_url == "https://${local.foundry_account_name}.services.ai.azure.com/api" &&
      azurerm_api_management_api.foundry[0].protocols == toset(["https"]) &&
      output.foundry_gateway_endpoint == "https://gateway.private.invalid/foundry/projects/airline-demo" &&
      output.foundry_agent_setup.project_name == "airline-demo" &&
      output.foundry_agent_setup.project_endpoint == "https://${local.foundry_account_name}.services.ai.azure.com/api/projects/airline-demo" &&
      output.foundry_agent_setup.publisher_identity_resource_id ==
      azurerm_user_assigned_identity.foundry_operator["publisher"].id &&
      output.foundry_agent_setup.runtime_deployment == "runtime" &&
      output.foundry_agent_setup.runtime_model_name == var.foundry_model_profile["runtime"].name &&
      output.foundry_agent_setup.runtime_model_version == var.foundry_model_profile["runtime"].version &&
      output.foundry_agent_setup.filter_policy == azurerm_cognitive_account_rai_policy.foundry[0].name &&
      azurerm_api_management.gateway.virtual_network_type == "Internal" &&
      length(azurerm_api_management_api_operation.foundry) == 16 &&
      alltrue([for key, expected in {
        conversation_create = { method = "POST", path = "/projects/{project}/openai/v1/conversations" }
        conversation_read   = { method = "GET", path = "/projects/{project}/openai/v1/conversations/{conversation}" }
        conversation_delete = { method = "DELETE", path = "/projects/{project}/openai/v1/conversations/{conversation}" }
        item_list           = { method = "GET", path = "/projects/{project}/openai/v1/conversations/{conversation}/items" }
        item_delete         = { method = "DELETE", path = "/projects/{project}/openai/v1/conversations/{conversation}/items/{item}" }
        response_create     = { method = "POST", path = "/projects/{project}/openai/v1/responses" }
        response_read       = { method = "GET", path = "/projects/{project}/openai/v1/responses/{response}" }
        response_delete     = { method = "DELETE", path = "/projects/{project}/openai/v1/responses/{response}" }
        } : azurerm_api_management_api_operation.foundry[key].method == expected.method &&
      azurerm_api_management_api_operation.foundry[key].url_template == expected.path]) &&
      alltrue([for method in ["GET", "POST", "DELETE", "PUT", "PATCH", "HEAD", "OPTIONS", "TRACE"] :
        azurerm_api_management_api_operation.foundry["deny_${lower(method)}"].method == method &&
      azurerm_api_management_api_operation.foundry["deny_${lower(method)}"].url_template == "/*"])
    )
    error_message = "The internal gateway must have exactly eight admitted operations and eight deny-only method fallbacks."
  }
  assert {
    condition = (
      azurerm_role_assignment.foundry_gateway[0].scope == local.foundry_project_id &&
      azurerm_role_assignment.foundry_gateway[0].principal_id == "aaaaaaaa-1111-2222-3333-444444444444" &&
      azurerm_role_assignment.foundry_gateway[0].principal_type == "ServicePrincipal" &&
      azurerm_role_assignment.foundry_gateway[0].role_definition_id ==
      "/subscriptions/00000000-0000-0000-0000-000000000000/providers/Microsoft.Authorization/roleDefinitions/eed3b665-ab3a-47b6-8f48-c9382fb1dad6" &&
      !contains(concat(
        [for grant in values(azurerm_role_assignment.foundry_runtime) : grant.principal_id],
        [for grant in values(azurerm_role_assignment.foundry_operator) : grant.principal_id],
        [azurerm_role_assignment.foundry_gateway[0].principal_id]
      ), azurerm_user_assigned_identity.app.principal_id)
    )
    error_message = "Only APIM receives the project-scoped Agent Consumer role; app direct invocation/admin rights must remain absent."
  }
  assert {
    condition = (
      azurerm_api_management_api_policy.foundry[0].xml_content == local.foundry_gateway_policy &&
      alltrue([for required in [
        "Gateway.Invoke", local.gateway_audience, local.gateway_approved_application_identity,
        "x-gateway-arm", "x-gateway-run", "x-gateway-package", "x-gateway-case",
        "x-gateway-model-configuration", "x-gateway-suite-version", "x-gateway-call-identity",
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", "gateway_operation_forbidden",
        "gateway_target_forbidden", "gateway_agent_forbidden", "agent_reference",
        "reservation_id", "attempt_id", "JTokenType.String", "Newtonsoft.Json.Linq.JArray", "Newtonsoft.Json.Formatting.None",
        "airline-copilot", "airline-investigator", "max_output_tokens", "truncation",
        "https://ai.azure.com", "copy-unmatched-params=\"true\"", "<forward-request timeout=\"30\" />"
      ] : strcontains(azurerm_api_management_api_policy.foundry[0].xml_content, required)]) &&
      !strcontains(azurerm_api_management_api_policy.foundry[0].xml_content, "<retry") &&
      !strcontains(azurerm_api_management_api_policy.foundry[0].xml_content, "<trace") &&
      !strcontains(azurerm_api_management_api_policy.foundry[0].xml_content, "listSecrets")
    )
    error_message = "Rendered gateway policy must preserve caller/correlation and exact target/agent controls without retry or content tracing."
  }
}

run "foundry_gateway_is_deny_only_before_agent_publication" {
  command = plan
  variables {
    foundry_enabled = true
  }
  assert {
    condition     = strcontains(azurerm_api_management_api_policy.foundry[0].xml_content, "if (!false) { return true; }")
    error_message = "Unpublished agents must leave the runtime gateway denied, not select latest or direct inference."
  }
}

run "foundry_gateway_rejects_latest_agent_versions" {
  command = plan
  variables {
    foundry_runtime_profile = {
      copilot      = { name = "airline-copilot", version = "latest", configuration_id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
      investigator = { name = "airline-investigator", version = "2", configuration_id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
    }
  }
  expect_failures = [var.foundry_runtime_profile]
}

run "foundry_gateway_rejects_ambiguous_configuration_identity" {
  command = plan
  variables {
    foundry_runtime_profile = {
      copilot      = { name = "airline-copilot", version = "1", configuration_id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
      investigator = { name = "airline-investigator", version = "2", configuration_id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
    }
  }
  expect_failures = [var.foundry_runtime_profile]
}

run "foundry_disabled_preserves_existing_network" {
  command = plan
  assert {
    condition = (
      length(azurerm_subnet.foundry_agent) == 0 &&
      length(azurerm_network_security_group.foundry_agent) == 0 &&
      length(azurerm_private_dns_zone.foundry) == 0 &&
      length(azurerm_cosmosdb_account.foundry) == 0 &&
      length(azurerm_storage_account.foundry) == 0 &&
      length(azurerm_search_service.foundry) == 0 &&
      length(azurerm_private_endpoint.foundry_state) == 0 &&
      length(azurerm_resource_group_template_deployment.foundry) == 0 &&
      length(azurerm_private_endpoint.foundry) == 0 &&
      length(azurerm_cognitive_deployment.foundry) == 0 &&
      length(azurerm_cognitive_account_rai_policy.foundry) == 0 &&
      length(azurerm_role_assignment.foundry_runtime) == 0 &&
      length(azurerm_cosmosdb_sql_role_assignment.foundry_runtime) == 0 &&
      length(azurerm_user_assigned_identity.foundry_operator) == 0 &&
      length(azurerm_role_definition.foundry_operator) == 0 &&
      length(azurerm_role_assignment.foundry_operator) == 0 &&
      length(azurerm_api_management_api.foundry) == 0 &&
      length(azurerm_api_management_api_operation.foundry) == 0 &&
      length(azurerm_api_management_api_policy.foundry) == 0 &&
      length(azurerm_role_assignment.foundry_gateway) == 0 &&
      output.foundry_gateway_endpoint == "" &&
      azurerm_subnet.app.address_prefixes == tolist(["10.20.1.0/24"]) &&
      azurerm_subnet.private_endpoints.address_prefixes == tolist(["10.20.2.0/24"])
    )
    error_message = "The disabled Foundry candidate must leave the existing network unchanged."
  }
}

run "foundry_agent_network_is_private_delegated_and_linked" {
  command = plan
  variables {
    foundry_enabled = true
    deployment_executor = {
      vnet_id          = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-tfstate/providers/Microsoft.Network/virtualNetworks/vnet-executor"
      blob_dns_zone_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-tfstate/providers/Microsoft.Network/privateDnsZones/privatelink.blob.core.windows.net"
    }
  }
  assert {
    condition = (
      azurerm_subnet.foundry_agent[0].address_prefixes == tolist(["10.20.5.0/24"]) &&
      azurerm_subnet.foundry_agent[0].default_outbound_access_enabled == false &&
      one(azurerm_subnet.foundry_agent[0].delegation).service_delegation[0].name == "Microsoft.App/environments" &&
      azurerm_subnet_network_security_group_association.foundry_agent[0].network_security_group_id == azurerm_network_security_group.foundry_agent[0].id &&
      azurerm_subnet_network_security_group_association.foundry_agent[0].subnet_id == azurerm_subnet.foundry_agent[0].id
    )
    error_message = "The dedicated /24 must disable default outbound and attach its delegated subnet to its NSG."
  }
  assert {
    condition = (
      sort([for zone in azurerm_private_dns_zone.foundry : zone.name]) ==
      tolist(["privatelink.documents.azure.com", "privatelink.search.windows.net", "privatelink.services.ai.azure.com"]) &&
      length(azurerm_private_dns_zone_virtual_network_link.foundry_application) == 3 &&
      length(azurerm_private_dns_zone_virtual_network_link.foundry_executor) == 3 &&
      alltrue([for link in azurerm_private_dns_zone_virtual_network_link.foundry_executor :
      link.virtual_network_id == var.deployment_executor.vnet_id && link.registration_enabled == false]) &&
      !contains(keys(azurerm_private_dns_zone.demo), "blob") &&
      azurerm_private_dns_zone_virtual_network_link.demo["blob"].resource_group_name == "rg-demo-tfstate" &&
      one(azurerm_private_endpoint.foundry_state["blob"].private_dns_zone_group).private_dns_zone_ids == tolist([var.deployment_executor.blob_dns_zone_id])
    )
    error_message = "Both networks need the three new namespaces without replacing the existing shared Blob namespace."
  }
  assert {
    condition = (
      one([for rule in azurerm_network_security_group.foundry_agent[0].security_rule :
      rule if rule.name == "deny-other-egress"]).access == "Deny" &&
      one([for rule in azurerm_network_security_group.foundry_agent[0].security_rule :
      rule if rule.name == "allow-entra"]).destination_address_prefix == "AzureActiveDirectory" &&
      one([for rule in azurerm_network_security_group.foundry_agent[0].security_rule :
      rule if rule.name == "allow-data"]).destination_address_prefix == "10.20.2.0/24" &&
      alltrue([for rule in azurerm_network_security_group.foundry_agent[0].security_rule :
      rule.access != "Allow" || !contains(["Internet", "*", "0.0.0.0/0"], rule.destination_address_prefix)])
    )
    error_message = "Foundry egress must allow explicit private/platform dependencies, never general internet access."
  }
  assert {
    condition = (
      one([for rule in azurerm_network_security_group.foundry_agent[0].security_rule :
      rule if rule.name == "allow-self"]).destination_port_range == "*" &&
      one([for rule in azurerm_network_security_group.foundry_agent[0].security_rule :
      rule if rule.name == "allow-data"]).destination_port_range == "443" &&
      alltrue([for rule in azurerm_network_security_group.foundry_agent[0].security_rule :
      !contains(coalesce(rule.destination_port_ranges, toset([])), "*")])
    )
    error_message = "Azure requires wildcard/single ports in destinationPortRange, never a wildcard inside destinationPortRanges."
  }
}

run "foundry_rejects_overlapping_application_subnet" {
  command = plan
  variables {
    foundry_enabled           = true
    foundry_agent_subnet_cidr = "10.20.1.0/24"
  }
  expect_failures = [azurerm_subnet.foundry_agent[0]]
}

run "foundry_rejects_public_subnet" {
  command = plan
  variables {
    foundry_enabled           = true
    foundry_agent_subnet_cidr = "203.0.113.0/24"
  }
  expect_failures = [var.foundry_agent_subnet_cidr]
}

run "foundry_rejects_non_sweden_region" {
  command = plan
  variables {
    foundry_enabled = true
    location        = "uksouth"
  }
  expect_failures = [azurerm_subnet.foundry_agent[0]]
}

run "foundry_rejects_missing_network_security_group" {
  command = plan
  variables {
    foundry_enabled = true
  }
  override_resource {
    target          = azurerm_network_security_group.foundry_agent[0]
    override_during = plan
    values          = { id = "" }
  }
  expect_failures = [azurerm_subnet_network_security_group_association.foundry_agent[0]]
}

run "foundry_rejects_overlapping_executor" {
  command = plan
  variables {
    foundry_enabled = true
    deployment_executor = {
      vnet_id          = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-tfstate/providers/Microsoft.Network/virtualNetworks/vnet-executor"
      blob_dns_zone_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-tfstate/providers/Microsoft.Network/privateDnsZones/privatelink.blob.core.windows.net"
    }
  }
  override_data {
    target          = data.azurerm_virtual_network.foundry_executor[0]
    override_during = plan
    values          = { address_space = ["10.20.0.0/16"] }
  }
  expect_failures = [azurerm_subnet.foundry_agent[0]]
}

run "foundry_state_is_private_keyless_and_explicitly_sized" {
  command = plan
  variables {
    foundry_enabled = true
  }
  override_module {
    target = module.foundry_store_readback[0]
    outputs = { scopes = {
      containers = {
        files = {
          id     = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stfoundrytest/blobServices/default/containers/01234567-89ab-cdef-0123-456789abcdef-azureml-blobstore"
          access = "private"
        }
        agent_files = {
          id     = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stfoundrytest/blobServices/default/containers/01234567-89ab-cdef-0123-456789abcdef-azureml-agent"
          access = "private"
        }
      }
      cosmosDatabaseId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.DocumentDB/databaseAccounts/cosmos-foundry/sqlDatabases/enterprise_memory"
    } }
  }
  override_resource {
    target          = azurerm_storage_account.foundry[0]
    override_during = plan
    values = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stfoundrytest"
    }
  }
  override_resource {
    target          = azurerm_private_dns_zone.foundry["document"]
    override_during = plan
    values = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Network/privateDnsZones/privatelink.documents.azure.com"
    }
  }
  override_resource {
    target          = azurerm_private_dns_zone.foundry["search"]
    override_during = plan
    values = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Network/privateDnsZones/privatelink.search.windows.net"
    }
  }
  override_resource {
    target          = azurerm_private_dns_zone.demo["blob"]
    override_during = plan
    values = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Network/privateDnsZones/privatelink.blob.core.windows.net"
    }
  }
  assert {
    condition = (
      azurerm_cosmosdb_account.foundry[0].local_authentication_enabled == false &&
      azurerm_cosmosdb_account.foundry[0].public_network_access_enabled == false &&
      azurerm_cosmosdb_account.foundry[0].network_acl_bypass_for_azure_services == false &&
      azurerm_cosmosdb_account.foundry[0].minimal_tls_version == "Tls12" &&
      one(azurerm_cosmosdb_account.foundry[0].capacity).total_throughput_limit == 5000 &&
      one(azurerm_cosmosdb_account.foundry[0].consistency_policy).consistency_level == "Session" &&
      one(azurerm_cosmosdb_account.foundry[0].backup).type == "Periodic" &&
      one(azurerm_cosmosdb_account.foundry[0].backup).interval_in_minutes == 240 &&
      one(azurerm_cosmosdb_account.foundry[0].backup).retention_in_hours == 24 &&
      one(azurerm_cosmosdb_account.foundry[0].backup).storage_redundancy == "Local" &&
      one(azurerm_cosmosdb_account.foundry[0].geo_location).location == "swedencentral"
    )
    error_message = "Cosmos must be keyless/private with exact explicit capacity, local backup retention and residency."
  }
  assert {
    condition = (
      azurerm_storage_account.foundry[0].shared_access_key_enabled == false &&
      azurerm_storage_account.foundry[0].allow_nested_items_to_be_public == false &&
      azurerm_storage_account.foundry[0].public_network_access_enabled == false &&
      azurerm_storage_account.foundry[0].default_to_oauth_authentication == true &&
      azurerm_storage_account.foundry[0].min_tls_version == "TLS1_2" &&
      azurerm_storage_account.foundry[0].https_traffic_only_enabled == true &&
      azurerm_storage_account.foundry[0].account_replication_type == "LRS" &&
      one(azurerm_storage_account.foundry[0].network_rules).default_action == "Deny" &&
      one(azurerm_storage_account.foundry[0].network_rules).bypass == toset(["None"]) &&
      one(one(azurerm_storage_account.foundry[0].blob_properties).delete_retention_policy).days == 7 &&
      one(one(azurerm_storage_account.foundry[0].blob_properties).container_delete_retention_policy).days == 7 &&
      azurerm_storage_account.blob.id != azurerm_storage_account.foundry[0].id &&
      one(one(azurerm_storage_account.blob.blob_properties).delete_retention_policy).days == 14 &&
      one(azurerm_mssql_database.airline.short_term_retention_policy).retention_days == 35
    )
    error_message = "New identity-only Blob storage must preserve the separate existing evidence/SQL retention."
  }
  assert {
    condition = (
      azurerm_search_service.foundry[0].local_authentication_enabled == false &&
      azurerm_search_service.foundry[0].location == "swedencentral" &&
      azurerm_search_service.foundry[0].public_network_access_enabled == false &&
      one(azurerm_search_service.foundry[0].identity).type == "SystemAssigned" &&
      azurerm_search_service.foundry[0].sku == "basic" &&
      azurerm_search_service.foundry[0].partition_count == 1 &&
      azurerm_search_service.foundry[0].replica_count == 1
    )
    error_message = "Search must use the exact explicit private/keyless candidate size and system identity."
  }
  assert {
    condition = (
      length(azurerm_private_endpoint.foundry_state) == 3 &&
      alltrue([for key, expected in {
        document = { id = azurerm_cosmosdb_account.foundry[0].id, group = "Sql", zone = azurerm_private_dns_zone.foundry["document"].id }
        search   = { id = azurerm_search_service.foundry[0].id, group = "searchService", zone = azurerm_private_dns_zone.foundry["search"].id }
        blob     = { id = azurerm_storage_account.foundry[0].id, group = "blob", zone = azurerm_private_dns_zone.demo["blob"].id }
        } :
        azurerm_private_endpoint.foundry_state[key].subnet_id == azurerm_subnet.private_endpoints.id &&
        one(azurerm_private_endpoint.foundry_state[key].private_service_connection).private_connection_resource_id == expected.id &&
        one(azurerm_private_endpoint.foundry_state[key].private_service_connection).subresource_names == tolist([expected.group]) &&
        one(azurerm_private_endpoint.foundry_state[key].private_service_connection).is_manual_connection == false &&
        one(azurerm_private_endpoint.foundry_state[key].private_dns_zone_group).private_dns_zone_ids == tolist([expected.zone])
      ])
    )
    error_message = "Each distinct store must use its own exact private endpoint, subresource and shared DNS namespace."
  }
  assert {
    condition = (
      azurerm_role_assignment.foundry_provisioner_blob[0].scope == azurerm_storage_account.foundry[0].id &&
      azurerm_role_assignment.foundry_provisioner_blob[0].role_definition_name == "Storage Blob Data Contributor" &&
      azurerm_role_assignment.foundry_provisioner_blob[0].principal_id == var.terraform_principal_object_id &&
      azurerm_role_assignment.foundry_provisioner_blob[0].principal_type == "User" &&
      azurerm_role_assignment.foundry_provisioner_cosmos[0].scope == azurerm_cosmosdb_account.foundry[0].id &&
      azurerm_role_assignment.foundry_provisioner_cosmos[0].role_definition_name == "Cosmos DB Operator" &&
      azurerm_role_assignment.foundry_provisioner_cosmos[0].principal_id == var.terraform_principal_object_id &&
      azurerm_role_assignment.foundry_provisioner_cosmos[0].principal_type == "User"
    )
    error_message = "Only the existing Terraform principal receives the two account-scoped provisioning grants."
  }
}

run "foundry_rejects_missing_state_profile" {
  command = plan
  variables {
    foundry_enabled       = true
    foundry_state_profile = null
  }
  expect_failures = [var.foundry_state_profile]
}

run "foundry_search_in_germany_retains_swedish_private_topology" {
    command = plan
    variables {
      foundry_enabled = true
      foundry_state_profile = {
        cosmos_total_throughput_limit  = 5000
        cosmos_backup_interval_minutes = 240
        cosmos_backup_retention_hours  = 8
        cosmos_consistency_level       = "Session"
        search_sku                     = "standard"
        search_partitions              = 1
        search_replicas                = 1
        search_location                = "germanywestcentral"
        storage_replication_type       = "LRS"
        blob_retention_days            = 7
        container_retention_days       = 7
      }
    }
    assert {
      condition = (
        azurerm_search_service.foundry[0].location == "germanywestcentral" &&
        azurerm_search_service.foundry[0].sku == "standard" &&
        azurerm_search_service.foundry[0].public_network_access_enabled == false &&
        azurerm_search_service.foundry[0].local_authentication_enabled == false &&
        azurerm_private_endpoint.foundry_state["search"].location == "swedencentral" &&
        azurerm_cosmosdb_account.foundry[0].location == "swedencentral" &&
        azurerm_storage_account.foundry[0].location == "swedencentral" &&
        local.foundry_template.resources[0].location == "swedencentral"
      )
      error_message = "Only Search relocates; Swedish Foundry and private access remain unchanged."
    }
  }

run "foundry_rejects_unbounded_cosmos_capacity" {
  command = plan
  variables {
    foundry_enabled = true
    foundry_state_profile = {
      cosmos_total_throughput_limit  = -1
      cosmos_backup_interval_minutes = 240
      cosmos_backup_retention_hours  = 24
      cosmos_consistency_level       = "Session"
      search_sku                     = "basic"
      search_partitions              = 1
      search_replicas                = 1
      storage_replication_type       = "LRS"
      blob_retention_days            = 7
      container_retention_days       = 7
    }
  }
  expect_failures = [var.foundry_state_profile]
}

run "foundry_rejects_free_search_tier" {
  command = plan
  variables {
    foundry_enabled = true
    foundry_state_profile = {
      cosmos_total_throughput_limit  = 5000
      cosmos_backup_interval_minutes = 240
      cosmos_backup_retention_hours  = 24
      cosmos_consistency_level       = "Session"
      search_sku                     = "free"
      search_partitions              = 1
      search_replicas                = 1
      storage_replication_type       = "LRS"
      blob_retention_days            = 7
      container_retention_days       = 7
    }
  }
  expect_failures = [var.foundry_state_profile]
}

run "foundry_rejects_cross_region_storage_replication" {
  command = plan
  variables {
    foundry_enabled = true
    foundry_state_profile = {
      cosmos_total_throughput_limit  = 5000
      cosmos_backup_interval_minutes = 240
      cosmos_backup_retention_hours  = 24
      cosmos_consistency_level       = "Session"
      search_sku                     = "basic"
      search_partitions              = 1
      search_replicas                = 1
      storage_replication_type       = "GRS"
      blob_retention_days            = 7
      container_retention_days       = 7
    }
  }
  expect_failures = [var.foundry_state_profile]
}
