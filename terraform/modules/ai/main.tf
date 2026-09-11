resource "azurerm_cognitive_account" "this" {
  name                = "oai-${var.deployment_name}"
  resource_group_name = var.resource_group_name
  location            = var.region
  kind                = "OpenAI"
  sku_name            = "S0"

  custom_subdomain_name         = "oai-${var.deployment_name}"
  local_auth_enabled            = false
  public_network_access_enabled = true

  network_acls {
    default_action = "Deny"
    bypass         = "AzureServices"
    ip_rules       = var.allowed_ip_ranges
  }

  identity {
    type = "SystemAssigned"
  }

  tags = var.tags
}

resource "azurerm_cognitive_deployment" "this" {
  name                 = "${var.model_name}-${var.model_version}"
  cognitive_account_id = azurerm_cognitive_account.this.id

  model {
    format  = "OpenAI"
    name    = var.model_name
    version = var.model_version
  }

  sku {
    name     = var.deployment_type
    capacity = var.quota_tokens_per_minute / 1000
  }

  version_upgrade_option = "NoAutoUpgrade"
}
