variable "foundry_model_profile" {
  description = "Explicit pinned runtime and judge deployments; current model/SKU/quota/cost evidence is required before apply."
  type = map(object({
    name     = string
    version  = string
    sku      = string
    capacity = number
  }))
  default = null

  validation {
    condition = !var.foundry_enabled || (
      var.foundry_model_profile == null ? false :
      toset(keys(var.foundry_model_profile)) == toset(["runtime", "judge"])
    )
    error_message = "Enabling Foundry requires an explicit runtime and judge foundry_model_profile."
  }
  validation {
    condition = var.foundry_model_profile == null ? true : alltrue([
      for model in values(var.foundry_model_profile) :
      length(trimspace(model.name)) > 0 && length(trimspace(model.version)) > 0 &&
      model.name == trimspace(model.name) && model.version == trimspace(model.version) &&
      lower(trimspace(model.version)) != "latest" &&
      contains(["Standard", "GlobalStandard", "DataZoneStandard"], model.sku) &&
      model.capacity > 0 && floor(model.capacity) == model.capacity
    ])
    error_message = "Foundry models need exact names/versions, an approved pay-as-you-go SKU and positive integral capacity; latest is forbidden."
  }
}

locals {
  foundry_account_name = "ai-${local.name_prefix}-${local.name_suffix}"
  foundry_project_name = "airline-demo"
  foundry_account_id   = "${local.rg_scope}/providers/Microsoft.CognitiveServices/accounts/${local.foundry_account_name}"
  foundry_project_id   = "${local.foundry_account_id}/projects/${local.foundry_project_name}"
  foundry_stores = local.foundry_state_count == 0 ? {} : {
    blobStore     = "${local.rg_scope}/providers/Microsoft.Storage/storageAccounts/${azurerm_storage_account.foundry[0].name}"
    documentStore = "${local.rg_scope}/providers/Microsoft.DocumentDB/databaseAccounts/${azurerm_cosmosdb_account.foundry[0].name}"
    vectorStore   = "${local.rg_scope}/providers/Microsoft.Search/searchServices/${azurerm_search_service.foundry[0].name}"
  }
  foundry_project_reference = "reference(resourceId('Microsoft.CognitiveServices/accounts/projects', parameters('accountName'), parameters('projectName')), '2026-07-15-preview'"
  foundry_workspace_reference = "toLower(format('{0}-{1}-{2}-{3}-{4}', ${join(", ", [
    for part in [[0, 8], [8, 4], [12, 4], [16, 4], [20, 12]] :
    "substring(${local.foundry_project_reference}).internalId, ${part[0]}, ${part[1]})"
  ])}))"
  foundry_container_references = {
    for key, suffix in { files = "-azureml-blobstore", agent_files = "-azureml-agent" } :
    key => "reference(concat('${try(local.foundry_stores.blobStore, "")}/blobServices/default/containers/', ${local.foundry_workspace_reference}, '${suffix}'), '2025-08-01', 'Full')"
  }
  foundry_template = {
    "$schema"      = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#"
    contentVersion = "1.0.0.0"
    parameters = {
      accountName = { type = "string" }
      projectName = { type = "string" }
    }
    outputs = {
      projectPrincipalId = {
        type  = "string"
        value = "[${local.foundry_project_reference}, 'Full').identity.principalId]"
      }
      projectInternalId = {
        type  = "string"
        value = "[${local.foundry_project_reference}).internalId]"
      }
      projectStoreScopes = {
        type = "object"
        value = {
          containers = {
            for key, reference in local.foundry_container_references : key => {
              id     = "[${reference}.id]"
              access = "[${reference}.properties.publicAccess]"
            }
          }
          cosmosDatabaseId = "[reference('${try(local.foundry_stores.documentStore, "")}/sqlDatabases/enterprise_memory', '2024-08-15', 'Full').id]"
        }
      }
    }
    resources = [
      {
        type       = "Microsoft.CognitiveServices/accounts"
        apiVersion = "2026-07-15-preview"
        name       = "[parameters('accountName')]"
        location   = var.location
        kind       = "AIServices"
        sku        = { name = "S0" }
        identity   = { type = "SystemAssigned" }
        properties = {
          allowProjectManagement = true
          customSubDomainName    = "[parameters('accountName')]"
          disableLocalAuth       = true
          publicNetworkAccess    = "Disabled"
          capabilitySettings     = local.foundry_stores
          networkInjections = [{
            scenario                   = "agent"
            subnetArmId                = var.foundry_enabled ? "${local.rg_scope}/providers/Microsoft.Network/virtualNetworks/${azurerm_virtual_network.demo.name}/subnets/${azurerm_subnet.foundry_agent[0].name}" : null
            useMicrosoftManagedNetwork = false
          }]
        }
      },
      {
        type       = "Microsoft.CognitiveServices/accounts/projects"
        apiVersion = "2026-07-15-preview"
        name       = "[format('{0}/{1}', parameters('accountName'), parameters('projectName'))]"
        location   = var.location
        identity   = { type = "SystemAssigned" }
        dependsOn  = ["[resourceId('Microsoft.CognitiveServices/accounts', parameters('accountName'))]"]
        properties = {
          displayName        = "[parameters('projectName')]"
          capabilitySettings = local.foundry_stores
        }
      }
    ]
  }
  foundry_models = local.foundry_state_count > 0 && var.foundry_model_profile != null ? var.foundry_model_profile : {}
}

resource "azurerm_resource_group_template_deployment" "foundry" {
  count               = local.foundry_state_count
  name                = "foundry-account-project"
  resource_group_name = azurerm_resource_group.demo.name
  deployment_mode     = "Incremental"
  debug_level         = "none"
  template_content    = jsonencode(local.foundry_template)
  parameters_content = jsonencode({
    accountName = { value = local.foundry_account_name }
    projectName = { value = local.foundry_project_name }
  })

  depends_on = [
    azurerm_subnet_network_security_group_association.foundry_agent,
    azurerm_private_endpoint.foundry_state,
    azurerm_role_assignment.foundry_provisioner_blob,
    azurerm_role_assignment.foundry_provisioner_cosmos
  ]

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_private_endpoint" "foundry" {
  count               = local.foundry_state_count
  name                = "pe-foundry-account"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name
  subnet_id           = azurerm_subnet.private_endpoints.id

  private_service_connection {
    name                           = "foundry-account"
    private_connection_resource_id = local.foundry_account_id
    subresource_names              = ["account"]
    is_manual_connection           = false
  }
  private_dns_zone_group {
    name = "foundry-account"
    private_dns_zone_ids = [
      azurerm_private_dns_zone.foundry["project"].id,
      azurerm_private_dns_zone.demo["model"].id,
      azurerm_private_dns_zone.demo["contentsafety"].id
    ]
  }
  depends_on = [azurerm_resource_group_template_deployment.foundry]
}

resource "azurerm_cognitive_account_rai_policy" "foundry" {
  count                = local.foundry_state_count
  name                 = "demo-medium-high"
  cognitive_account_id = local.foundry_account_id
  base_policy_name     = "Microsoft.Default"
  mode                 = "Blocking"

  dynamic "content_filter" {
    for_each = setproduct(["Hate", "Sexual", "Violence", "Selfharm"], ["Prompt", "Completion"])
    content {
      name               = content_filter.value[0]
      source             = content_filter.value[1]
      filter_enabled     = true
      block_enabled      = true
      severity_threshold = "Medium"
    }
  }
  depends_on = [azurerm_resource_group_template_deployment.foundry]
}

resource "azurerm_cognitive_deployment" "foundry" {
  for_each               = local.foundry_models
  name                   = each.key
  cognitive_account_id   = local.foundry_account_id
  rai_policy_name        = azurerm_cognitive_account_rai_policy.foundry[0].name
  version_upgrade_option = "NoAutoUpgrade"

  model {
    format  = "OpenAI"
    name    = each.value.name
    version = each.value.version
  }
  sku {
    name     = each.value.sku
    capacity = each.value.capacity
  }
  lifecycle {
    prevent_destroy = true
  }
}
