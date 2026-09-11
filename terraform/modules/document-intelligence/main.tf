resource "azurerm_cognitive_account" "this" {
  name                = "doc-${var.deployment_name}"
  resource_group_name = var.resource_group_name
  location            = var.region
  kind                = "FormRecognizer"
  sku_name            = "S0"

  custom_subdomain_name         = "doc-${var.deployment_name}"
  local_auth_enabled            = false
  public_network_access_enabled = true

  network_acls {
    default_action = "Deny"
    ip_rules       = var.allowed_ip_ranges
  }

  identity {
    type = "SystemAssigned"
  }

  tags = var.tags
}
