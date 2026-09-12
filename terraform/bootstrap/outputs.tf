output "state_resource_group_name" {
  description = "Name of the independently owned state resource group."
  value       = azurerm_resource_group.state.name
}

output "state_storage_account_name" {
  description = "Name of the protected state storage account."
  value       = azurerm_storage_account.state.name
}

output "state_container_name" {
  description = "Name of the private state container."
  value       = azurerm_storage_container.state.name
}

output "state_container_resource_id" {
  description = "Resource-manager scope used for the deployment principal's state permission."
  value       = azurerm_storage_container.state.resource_manager_id
}
