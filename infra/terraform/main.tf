terraform {
  required_version = ">= 1.9.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }

  # Entra auth only; the azurerm backend takes a blob lease for state locking.
  # Backend blocks cannot read variables, so terraform_state_backend validates the
  # intended settings and CI must not override use_azuread_auth via -backend-config.
  backend "azurerm" {
    use_azuread_auth = true
  }
}

provider "azurerm" {
  subscription_id = var.subscription_id
  tenant_id       = var.tenant_id
  # Storage has shared keys disabled, so container management must use Entra auth.
  storage_use_azuread = true
  features {}
}

locals {
  rg_scope = "/subscriptions/${var.subscription_id}/resourceGroups/${var.resource_group_name}"

  private_dns_zones = {
    sql   = "privatelink.database.windows.net"
    blob  = "privatelink.blob.core.windows.net"
    model = "privatelink.openai.azure.com"
    kv    = "privatelink.vaultcore.azure.net"
    acr   = "privatelink.azurecr.io"
  }

  private_endpoints = {
    sql   = { resource_id = azurerm_mssql_server.sql.id, subresource = "sqlServer", zone = "sql" }
    blob  = { resource_id = azurerm_storage_account.blob.id, subresource = "blob", zone = "blob" }
    model = { resource_id = azurerm_cognitive_account.model.id, subresource = "account", zone = "model" }
    kv    = { resource_id = azurerm_key_vault.kv.id, subresource = "vault", zone = "kv" }
    acr   = { resource_id = azurerm_container_registry.acr.id, subresource = "registry", zone = "acr" }
  }

  presenter_reviewer_rules = { for i, c in var.presenter_reviewer_cidrs : tostring(i) => c }
  egress_rules             = { for i, c in var.egress_allowed_destinations : tostring(i) => c }
}

resource "azurerm_resource_group" "demo" {
  name     = var.resource_group_name
  location = var.location
}

resource "azurerm_virtual_network" "demo" {
  name                = "vnet-demo"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name
  address_space       = var.vnet_address_space
}

resource "azurerm_subnet" "app" {
  name                 = "snet-app"
  resource_group_name  = azurerm_resource_group.demo.name
  virtual_network_name = azurerm_virtual_network.demo.name
  address_prefixes     = [var.app_subnet_cidr]

  delegation {
    name = "container-apps-environment"

    service_delegation {
      name    = "Microsoft.App/environments"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

resource "azurerm_subnet" "private_endpoints" {
  name                              = "snet-private-endpoints"
  resource_group_name               = azurerm_resource_group.demo.name
  virtual_network_name              = azurerm_virtual_network.demo.name
  address_prefixes                  = [var.private_endpoint_subnet_cidr]
  private_endpoint_network_policies = "Enabled"
}

# Allow rules exist only for configured CIDRs; priority 4096 denies are the default.
resource "azurerm_network_security_group" "app" {
  name                = "nsg-app"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name
}

resource "azurerm_subnet_network_security_group_association" "app" {
  subnet_id                 = azurerm_subnet.app.id
  network_security_group_id = azurerm_network_security_group.app.id
}

resource "azurerm_network_security_rule" "ingress_allow" {
  for_each                    = local.presenter_reviewer_rules
  name                        = "allow-presenter-reviewer-${each.key}"
  priority                    = 100 + tonumber(each.key)
  direction                   = "Inbound"
  access                      = "Allow"
  protocol                    = "Tcp"
  source_port_range           = "*"
  destination_port_range      = "443"
  source_address_prefix       = each.value
  destination_address_prefix  = var.app_subnet_cidr
  resource_group_name         = azurerm_resource_group.demo.name
  network_security_group_name = azurerm_network_security_group.app.name
}

resource "azurerm_network_security_rule" "ingress_deny_all" {
  name                        = "deny-all-inbound"
  priority                    = 4096
  direction                   = "Inbound"
  access                      = "Deny"
  protocol                    = "*"
  source_port_range           = "*"
  destination_port_range      = "*"
  source_address_prefix       = "*"
  destination_address_prefix  = "*"
  resource_group_name         = azurerm_resource_group.demo.name
  network_security_group_name = azurerm_network_security_group.app.name
}

resource "azurerm_network_security_rule" "egress_private_endpoints" {
  name                        = "allow-private-endpoints-443"
  priority                    = 100
  direction                   = "Outbound"
  access                      = "Allow"
  protocol                    = "Tcp"
  source_port_range           = "*"
  destination_port_range      = "443"
  source_address_prefix       = var.app_subnet_cidr
  destination_address_prefix  = var.private_endpoint_subnet_cidr
  resource_group_name         = azurerm_resource_group.demo.name
  network_security_group_name = azurerm_network_security_group.app.name
}

resource "azurerm_network_security_rule" "egress_allow" {
  for_each                    = local.egress_rules
  name                        = "allow-egress-${each.key}"
  priority                    = 110 + tonumber(each.key)
  direction                   = "Outbound"
  access                      = "Allow"
  protocol                    = "Tcp"
  source_port_range           = "*"
  destination_port_range      = "443"
  source_address_prefix       = var.app_subnet_cidr
  destination_address_prefix  = each.value
  resource_group_name         = azurerm_resource_group.demo.name
  network_security_group_name = azurerm_network_security_group.app.name
}

resource "azurerm_network_security_rule" "egress_deny_all" {
  name                        = "deny-all-outbound"
  priority                    = 4096
  direction                   = "Outbound"
  access                      = "Deny"
  protocol                    = "*"
  source_port_range           = "*"
  destination_port_range      = "*"
  source_address_prefix       = "*"
  destination_address_prefix  = "*"
  resource_group_name         = azurerm_resource_group.demo.name
  network_security_group_name = azurerm_network_security_group.app.name
}

resource "azurerm_mssql_server" "sql" {
  name                          = var.sql_server_name
  resource_group_name           = azurerm_resource_group.demo.name
  location                      = azurerm_resource_group.demo.location
  version                       = "12.0"
  minimum_tls_version           = "1.2"
  public_network_access_enabled = false

  azuread_administrator {
    login_username              = var.sql_admin_group_name
    object_id                   = var.sql_admin_group_object_id
    azuread_authentication_only = true
  }
}

resource "azurerm_mssql_server_transparent_data_encryption" "sql" {
  server_id = azurerm_mssql_server.sql.id
}

resource "azurerm_storage_account" "blob" {
  name                              = var.storage_account_name
  resource_group_name               = azurerm_resource_group.demo.name
  location                          = azurerm_resource_group.demo.location
  account_tier                      = "Standard"
  account_replication_type          = "LRS"
  min_tls_version                   = "TLS1_2"
  https_traffic_only_enabled        = true
  public_network_access_enabled     = false
  allow_nested_items_to_be_public   = false
  shared_access_key_enabled         = false
  default_to_oauth_authentication   = true
  infrastructure_encryption_enabled = true

  # Evidence versioning and 14-day recovery of deleted blobs and containers (RETENTION-01).
  blob_properties {
    versioning_enabled = true

    delete_retention_policy {
      days = 14
    }

    container_delete_retention_policy {
      days = 14
    }
  }
}

resource "azurerm_key_vault" "kv" {
  name                          = var.key_vault_name
  resource_group_name           = azurerm_resource_group.demo.name
  location                      = azurerm_resource_group.demo.location
  tenant_id                     = var.tenant_id
  sku_name                      = "standard"
  rbac_authorization_enabled    = true
  purge_protection_enabled      = true
  soft_delete_retention_days    = 90
  public_network_access_enabled = false

  network_acls {
    default_action = "Deny"
    bypass         = "AzureServices"
  }
}

resource "azurerm_cognitive_account" "model" {
  name                          = var.model_account_name
  resource_group_name           = azurerm_resource_group.demo.name
  location                      = "swedencentral"
  kind                          = "OpenAI"
  sku_name                      = "S0"
  custom_subdomain_name         = var.model_endpoint_subdomain
  public_network_access_enabled = false
  local_auth_enabled            = false

  network_acls {
    default_action = "Deny"
  }
}

resource "azurerm_api_management" "gateway" {
  name                = "apim-foundry-${var.resource_group_name}"
  location            = var.gateway_region
  resource_group_name = azurerm_resource_group.demo.name
  publisher_name      = "Airline Demo"
  publisher_email     = "owner@example.invalid"
  sku_name            = "${var.gateway_api_management_tier}_1"
  public_network_access_enabled = false
  virtual_network_type          = "Internal"

  virtual_network_configuration {
    subnet_id = azurerm_subnet.private_endpoints.id
  }

  identity {
    type = "SystemAssigned"
  }
}

resource "azurerm_role_assignment" "gateway_cognitive_services" {
  scope                = azurerm_cognitive_account.model.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_api_management.gateway.identity[0].principal_id
}

resource "azurerm_api_management_api" "gateway_model" {
  name                = "foundry-model-gateway"
  resource_group_name = azurerm_resource_group.demo.name
  api_management_name = azurerm_api_management.gateway.name
  revision            = "1"
  display_name        = "Foundry private model gateway"
  path                = "openai"
  protocols           = ["https"]
  subscription_required = false
  service_url         = "https://${var.model_endpoint_subdomain}.privatelink.openai.azure.com/openai"
}

resource "azurerm_api_management_api_operation" "gateway_chat_completions" {
  operation_id        = "chat-completions"
  api_name            = azurerm_api_management_api.gateway_model.name
  api_management_name = azurerm_api_management.gateway.name
  resource_group_name = azurerm_resource_group.demo.name
  display_name        = "Private chat completions"
  method              = "POST"
  url_template        = "deployments/${var.gateway_model_deployment}/chat/completions?api-version=${var.gateway_api_version}"
}

resource "azurerm_api_management_api_operation_policy" "gateway_chat_completions" {
  api_name            = azurerm_api_management_api.gateway_model.name
  api_management_name = azurerm_api_management.gateway.name
  resource_group_name = azurerm_resource_group.demo.name
  operation_id        = azurerm_api_management_api_operation.gateway_chat_completions.operation_id
  xml_content         = <<XML
<policies>
  <inbound>
    <base />
    <validate-jwt header-name="Authorization" failed-validation-httpcode="401" failed-validation-error-message="gateway caller identity rejected">
      <openid-config url="https://login.microsoftonline.com/${var.tenant_id}/v2.0/.well-known/openid-configuration" />
      <audiences>
        <audience>${var.gateway_audience}</audience>
      </audiences>
      <required-claims>
        <claim name="oid" match="any">
          <value>${var.gateway_approved_application_identity}</value>
        </claim>
      </required-claims>
    </validate-jwt>
    <rewrite-uri template="/openai/deployments/${var.gateway_model_deployment}/chat/completions?api-version=${var.gateway_api_version}" copy-unmatched-params="false" />
    <authentication-managed-identity resource="https://cognitiveservices.azure.com" />
  </inbound>
</policies>
XML
}

resource "azurerm_cognitive_deployment" "model" {
  name                 = var.model_deployment_name
  cognitive_account_id = azurerm_cognitive_account.model.id

  model {
    format  = "OpenAI"
    name    = var.model_name
    version = var.model_version
  }

  sku {
    name = "GlobalStandard"
  }
}

resource "azurerm_private_dns_zone" "demo" {
  for_each            = local.private_dns_zones
  name                = each.value
  resource_group_name = azurerm_resource_group.demo.name
}

resource "azurerm_private_dns_zone_virtual_network_link" "demo" {
  for_each              = local.private_dns_zones
  name                  = "link-${each.key}"
  resource_group_name   = azurerm_resource_group.demo.name
  private_dns_zone_name = azurerm_private_dns_zone.demo[each.key].name
  virtual_network_id    = azurerm_virtual_network.demo.id
}

resource "azurerm_private_endpoint" "demo" {
  for_each            = local.private_endpoints
  name                = "pe-${each.key}"
  resource_group_name = azurerm_resource_group.demo.name
  location            = azurerm_resource_group.demo.location
  subnet_id           = azurerm_subnet.private_endpoints.id

  private_service_connection {
    name                           = "psc-${each.key}"
    private_connection_resource_id = each.value.resource_id
    subresource_names              = [each.value.subresource]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "dzg-${each.key}"
    private_dns_zone_ids = [azurerm_private_dns_zone.demo[each.value.zone].id]
  }
}

resource "azurerm_role_assignment" "terraform_contributor" {
  scope                = local.rg_scope
  role_definition_name = "Contributor"
  principal_id         = var.terraform_principal_object_id

  # Fails the plan if this existing Contributor grant would go to a workflow identity.
  lifecycle {
    precondition {
      condition     = lower(var.terraform_principal_object_id) != lower(azurerm_user_assigned_identity.build.principal_id) && lower(var.terraform_principal_object_id) != lower(azurerm_user_assigned_identity.release.principal_id)
      error_message = "terraform_principal_object_id must not be the build or release workflow identity, or the Contributor grant would hand that workflow subscription-wide rights."
    }
  }
}

resource "azurerm_consumption_budget_resource_group" "demo" {
  name              = "budget-${var.resource_group_name}"
  resource_group_id = azurerm_resource_group.demo.id
  amount            = var.budget_amount
  time_grain        = "Monthly"

  time_period {
    start_date = var.budget_start_date
  }

  notification {
    enabled        = true
    threshold      = 80
    operator       = "GreaterThan"
    contact_emails = var.budget_contact_emails
  }
}

output "model_endpoint" {
  description = "Private Foundry AI Gateway endpoint; secrets and model keys are never output."
  value       = var.gateway_endpoint
}
