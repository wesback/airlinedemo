variable "foundry_runtime_profile" {
  description = "Exact published agent versions and configuration digests. Null leaves the new runtime gateway deny-only until publication."
  type = map(object({
    name             = string
    version          = string
    configuration_id = string
  }))
  default = null

  validation {
    condition = var.foundry_runtime_profile == null ? true : (
      toset(keys(var.foundry_runtime_profile)) == toset(["copilot", "investigator"]) &&
      alltrue([for agent in values(var.foundry_runtime_profile) :
        can(regex("^[A-Za-z][A-Za-z0-9_-]{0,62}$", agent.name)) &&
        can(regex("^[1-9][0-9]{0,9}$", agent.version)) &&
      can(regex("^[a-f0-9]{64}$", agent.configuration_id))]) &&
      length(toset([for agent in values(var.foundry_runtime_profile) : agent.name])) == 2 &&
      length(toset([for agent in values(var.foundry_runtime_profile) : agent.configuration_id])) == 2
    )
    error_message = "Runtime profile requires two distinct exact copilot/investigator agent names, numeric versions and lowercase SHA-256 configuration identities."
  }
}

locals {
  foundry_gateway_operations = {
    conversation_create = { method = "POST", path = "/projects/{project}/openai/v1/conversations" }
    conversation_read   = { method = "GET", path = "/projects/{project}/openai/v1/conversations/{conversation}" }
    conversation_delete = { method = "DELETE", path = "/projects/{project}/openai/v1/conversations/{conversation}" }
    item_list           = { method = "GET", path = "/projects/{project}/openai/v1/conversations/{conversation}/items" }
    item_delete         = { method = "DELETE", path = "/projects/{project}/openai/v1/conversations/{conversation}/items/{item}" }
    response_create     = { method = "POST", path = "/projects/{project}/openai/v1/responses" }
    response_read       = { method = "GET", path = "/projects/{project}/openai/v1/responses/{response}" }
    response_delete     = { method = "DELETE", path = "/projects/{project}/openai/v1/responses/{response}" }
  }
  foundry_gateway_routes = merge(local.foundry_gateway_operations, {
    for method in ["GET", "POST", "DELETE", "PUT", "PATCH", "HEAD", "OPTIONS", "TRACE"] :
    "deny_${lower(method)}" => { method = method, path = "/*" }
  })
  foundry_gateway_policy = templatefile("${path.module}/foundry-gateway.xml.tftpl", {
    tenant_id         = var.tenant_id
    audience          = local.gateway_audience
    client_id         = azuread_application.gateway.client_id
    app_id            = local.gateway_approved_application_identity
    project_name      = local.foundry_project_name
    account_name      = local.foundry_account_name
    operation_ids     = join(", ", [for name in sort(keys(local.foundry_gateway_operations)) : "&quot;${name}&quot;"])
    configuration_ids = var.foundry_runtime_profile == null ? "" : join(", ", [for agent in values(var.foundry_runtime_profile) : "&quot;${agent.configuration_id}&quot;"])
    agents = var.foundry_runtime_profile == null ? [] : [
      for agent in values(var.foundry_runtime_profile) :
      "(name == \"${agent.name}\" &amp;&amp; version == \"${agent.version}\" &amp;&amp; configuration == \"${agent.configuration_id}\")"
    ]
    runtime_ready = var.foundry_runtime_profile == null ? "false" : "true"
  })
}

resource "azurerm_api_management_api" "foundry" {
  count                 = local.foundry_state_count
  name                  = "foundry-project-runtime"
  resource_group_name   = azurerm_resource_group.demo.name
  api_management_name   = azurerm_api_management.gateway.name
  revision              = "1"
  display_name          = "Private Foundry project runtime"
  path                  = "foundry"
  protocols             = ["https"]
  subscription_required = false
  service_url           = "https://${local.foundry_account_name}.services.ai.azure.com/api"
}

resource "azurerm_api_management_api_operation" "foundry" {
  for_each            = var.foundry_enabled ? local.foundry_gateway_routes : {}
  operation_id        = each.key
  api_name            = azurerm_api_management_api.foundry[0].name
  api_management_name = azurerm_api_management.gateway.name
  resource_group_name = azurerm_resource_group.demo.name
  display_name        = each.key
  method              = each.value.method
  url_template        = each.value.path

  dynamic "template_parameter" {
    for_each = regexall("\\{([^}]+)\\}", each.value.path)
    content {
      name     = template_parameter.value[0]
      required = true
      type     = "string"
    }
  }
}

resource "azurerm_api_management_api_policy" "foundry" {
  count               = local.foundry_state_count
  api_name            = azurerm_api_management_api.foundry[0].name
  api_management_name = azurerm_api_management.gateway.name
  resource_group_name = azurerm_resource_group.demo.name
  xml_content         = local.foundry_gateway_policy
}

resource "azurerm_role_assignment" "foundry_gateway" {
  count              = local.foundry_state_count
  scope              = local.foundry_project_id
  role_definition_id = "/subscriptions/${var.subscription_id}/providers/Microsoft.Authorization/roleDefinitions/eed3b665-ab3a-47b6-8f48-c9382fb1dad6"
  principal_id       = azurerm_api_management.gateway.identity[0].principal_id
  principal_type     = "ServicePrincipal"
  depends_on         = [azurerm_resource_group_template_deployment.foundry]
}

output "foundry_gateway_endpoint" {
  description = "Private APIM project endpoint; empty until the candidate is enabled."
  value       = var.foundry_enabled ? "${trimsuffix(local.model_endpoint, "/")}/foundry/projects/${local.foundry_project_name}" : ""
}

output "foundry_agent_setup" {
  description = "Nonsecret project and pinned runtime deployment inputs for the private publisher stage."
  value = var.foundry_enabled ? {
    project_name                   = local.foundry_project_name
    project_endpoint               = "https://${local.foundry_account_name}.services.ai.azure.com/api/projects/${local.foundry_project_name}"
    publisher_identity_resource_id = azurerm_user_assigned_identity.foundry_operator["publisher"].id
    runtime_deployment             = "runtime"
    runtime_model_name             = var.foundry_model_profile["runtime"].name
    runtime_model_version          = var.foundry_model_profile["runtime"].version
    filter_policy                  = azurerm_cognitive_account_rai_policy.foundry[0].name
  } : null
}
