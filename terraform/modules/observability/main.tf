resource "azurerm_log_analytics_workspace" "this" {
  name                = substr("law-${var.deployment_name}", 0, 63)
  location            = var.region
  resource_group_name = var.resource_group_name
  sku                 = "PerGB2018"
  retention_in_days   = var.retention_days
  tags                = var.tags
}

resource "azurerm_application_insights" "this" {
  name                = "appi-${var.deployment_name}"
  location            = var.region
  resource_group_name = var.resource_group_name
  application_type    = "web"
  workspace_id        = azurerm_log_analytics_workspace.this.id
  retention_in_days   = var.retention_days
  tags                = var.tags
}
