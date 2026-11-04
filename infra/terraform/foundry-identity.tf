variable "foundry_agent_container_name" {
  description = "Exact existing private service-generated agent container discovered during setup; null retains the unsalted naming contract."
  type        = string
  default     = null

  validation {
    condition = var.foundry_agent_container_name == null ? true : can(regex(
      "^${local.foundry_workspace_id}-([0-9a-f]{12}-)?azureml-agent$",
      var.foundry_agent_container_name
    ))
    error_message = "The agent container must belong to the exact service-generated project workspace, with only an optional 12-hex service suffix."
  }
}

locals {
  foundry_project_outputs = jsondecode(local.foundry_state_count == 0 ? "{}" : azurerm_resource_group_template_deployment.foundry[0].output_content)
  foundry_principal_id    = try(local.foundry_project_outputs.projectPrincipalId.value, "")
  foundry_internal_id     = try(local.foundry_project_outputs.projectInternalId.value, "")
  foundry_store_scopes    = try(module.foundry_store_readback[0].scopes, {})
  foundry_identity_valid = (
    can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", local.foundry_principal_id)) &&
    can(regex("^[0-9a-fA-F]{32}$", local.foundry_internal_id))
  )
  foundry_workspace_id = try(join("-", regex("^([0-9a-f]{8})([0-9a-f]{4})([0-9a-f]{4})([0-9a-f]{4})([0-9a-f]{12})$", lower(local.foundry_internal_id))), "")
  foundry_blob_containers = local.foundry_state_count == 0 ? {} : {
    files       = "${local.foundry_workspace_id}-azureml-blobstore"
    agent_files = var.foundry_agent_container_name == null ? "${local.foundry_workspace_id}-azureml-agent" : var.foundry_agent_container_name
  }
  foundry_runtime_grants = local.foundry_state_count == 0 ? {} : {
    # ARM retains this casing for an existing Cosmos extension-role scope.
    cosmos_operator  = { scope = replace(azurerm_cosmosdb_account.foundry[0].id, "/\\/resourceGroups\\//", "/resourcegroups/"), role = "Cosmos DB Operator" }
    storage_operator = { scope = azurerm_storage_account.foundry[0].id, role = "Storage Account Contributor" }
    search_data      = { scope = azurerm_search_service.foundry[0].id, role = "Search Index Data Contributor" }
    search_operator  = { scope = azurerm_search_service.foundry[0].id, role = "Search Service Contributor" }
    files = {
      scope = try(local.foundry_store_scopes.containers.files.id, "")
      role  = "Storage Blob Data Contributor"
    }
    agent_files = {
      scope = try(local.foundry_store_scopes.containers.agent_files.id, "")
      role  = "Storage Blob Data Owner"
    }
    inference = { scope = local.foundry_account_id, role = "Cognitive Services OpenAI User" }
  }
  foundry_operator_actions = {
    publisher = [
      "Microsoft.CognitiveServices/accounts/AIServices/agents/read",
      "Microsoft.CognitiveServices/accounts/AIServices/agents/write",
      "Microsoft.CognitiveServices/accounts/AIServices/agents/delete"
    ]
    evaluator = [
      "Microsoft.CognitiveServices/accounts/AIServices/evaluations/read",
      "Microsoft.CognitiveServices/accounts/AIServices/evaluations/write",
      "Microsoft.CognitiveServices/accounts/OpenAI/evals/read",
      "Microsoft.CognitiveServices/accounts/OpenAI/evals/write"
    ]
  }
}

module "foundry_store_readback" {
  source              = "./modules/foundry-store-readback"
  count               = local.foundry_state_count
  containers          = local.foundry_blob_containers
  storage_account_id  = azurerm_storage_account.foundry[0].id
  cosmos_account_name = azurerm_cosmosdb_account.foundry[0].name
  resource_group_name = azurerm_resource_group.demo.name

  depends_on = [azurerm_resource_group_template_deployment.foundry]
}

resource "azurerm_role_assignment" "foundry_runtime" {
  for_each             = local.foundry_runtime_grants
  scope                = each.value.scope
  role_definition_name = each.value.role
  principal_id         = local.foundry_principal_id
  principal_type       = "ServicePrincipal"

  lifecycle {
    precondition {
      condition     = local.foundry_identity_valid
      error_message = "Foundry runtime grants require the exact service-generated principal GUID and 32-hex project internal ID; no broader scope fallback."
    }
    precondition {
      condition = contains(["files", "agent_files"], each.key) ? (
        each.value.scope == "${azurerm_storage_account.foundry[0].id}/blobServices/default/containers/${local.foundry_blob_containers[each.key]}" &&
        try(local.foundry_store_scopes.containers[each.key].access, "") == "private"
      ) : true
      error_message = "Foundry Blob data grants require the exact existing private project container; missing or different service scopes are not replaced."
    }
  }
}

resource "azurerm_cosmosdb_sql_role_assignment" "foundry_runtime" {
  count               = local.foundry_state_count
  resource_group_name = azurerm_resource_group.demo.name
  account_name        = azurerm_cosmosdb_account.foundry[0].name
  role_definition_id  = "${azurerm_cosmosdb_account.foundry[0].id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"
  principal_id        = local.foundry_principal_id
  scope               = "${azurerm_cosmosdb_account.foundry[0].id}/dbs/enterprise_memory"

  lifecycle {
    precondition {
      condition     = local.foundry_identity_valid
      error_message = "Foundry database grants require validated service-generated project identity; account-wide data scope is forbidden."
    }
    precondition {
      condition     = try(local.foundry_store_scopes.cosmosDatabaseId, "") == "${azurerm_cosmosdb_account.foundry[0].id}/sqlDatabases/enterprise_memory"
      error_message = "Foundry Cosmos data grants require the existing service-created enterprise_memory database in the exact state account."
    }
  }
}

resource "azurerm_user_assigned_identity" "foundry_operator" {
  for_each            = var.foundry_enabled ? local.foundry_operator_actions : {}
  name                = "id-${local.name_prefix}-foundry-${each.key}"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name
}

resource "azurerm_role_definition" "foundry_operator" {
  for_each    = var.foundry_enabled ? local.foundry_operator_actions : {}
  name        = "${local.name_prefix}-${local.name_suffix}-foundry-${each.key}"
  scope       = azurerm_resource_group.demo.id
  description = "Project-scoped Foundry ${each.key}; no application, store, role-assignment or subscription authority."

  permissions {
    actions      = ["Microsoft.CognitiveServices/accounts/projects/read"]
    data_actions = each.value
  }
  assignable_scopes = [azurerm_resource_group.demo.id]
}

resource "azurerm_role_assignment" "foundry_operator" {
  for_each           = var.foundry_enabled ? local.foundry_operator_actions : {}
  scope              = local.foundry_project_id
  role_definition_id = azurerm_role_definition.foundry_operator[each.key].role_definition_resource_id
  principal_id       = azurerm_user_assigned_identity.foundry_operator[each.key].principal_id
  principal_type     = "ServicePrincipal"
  depends_on         = [azurerm_resource_group_template_deployment.foundry]
}
