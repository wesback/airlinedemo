variable "deployment_executor" {
  description = "Independent private executor network and shared Blob DNS zone; null keeps the original in-VNet deployment path."
  type = object({
    vnet_id          = string
    blob_dns_zone_id = string
  })
  default = null
}

locals {
  executor_count   = var.deployment_executor == null ? 0 : 1
  blob_dns_zone_id = var.deployment_executor == null ? azurerm_private_dns_zone.demo["blob"].id : var.deployment_executor.blob_dns_zone_id
}

resource "azurerm_virtual_network_peering" "app_to_executor" {
  count                     = local.executor_count
  name                      = "deployment-executor"
  resource_group_name       = azurerm_resource_group.demo.name
  virtual_network_name      = azurerm_virtual_network.demo.name
  remote_virtual_network_id = var.deployment_executor.vnet_id
}

resource "azurerm_virtual_network_peering" "executor_to_app" {
  count                     = local.executor_count
  name                      = "${local.name_prefix}-application"
  resource_group_name       = split("/", var.deployment_executor.vnet_id)[4]
  virtual_network_name      = split("/", var.deployment_executor.vnet_id)[8]
  remote_virtual_network_id = azurerm_virtual_network.demo.id
}

resource "azurerm_private_dns_zone_virtual_network_link" "executor" {
  for_each              = var.deployment_executor == null ? {} : { for k, v in local.private_dns_zones : k => v if k != "blob" }
  name                  = "deployment-executor"
  resource_group_name   = azurerm_resource_group.demo.name
  private_dns_zone_name = azurerm_private_dns_zone.demo[each.key].name
  virtual_network_id    = var.deployment_executor.vnet_id
}

resource "azurerm_private_dns_zone_virtual_network_link" "monitor_executor" {
  for_each              = var.deployment_executor == null ? {} : local.monitor_private_dns_zones
  name                  = "deployment-executor"
  resource_group_name   = azurerm_resource_group.demo.name
  private_dns_zone_name = azurerm_private_dns_zone.monitor[each.key].name
  virtual_network_id    = var.deployment_executor.vnet_id
}
