output "container_app_id" {
  description = "Non-secret resource identifier for the Container App."
  value       = azurerm_container_app.this.id
}

output "container_registry_id" {
  description = "Non-secret resource identifier for the application container registry."
  value       = azurerm_container_registry.application.id
}

output "container_app_runtime_principal_id" {
  description = "Non-secret principal ID of the Container App system-assigned runtime identity."
  value       = azurerm_container_app.this.identity[0].principal_id
}

output "migration_identity_id" {
  description = "Non-secret resource ID of the Terraform-managed migration identity."
  value       = azurerm_user_assigned_identity.migration.id
}

output "migration_identity_client_id" {
  description = "Non-secret client ID of the Terraform-managed migration identity."
  value       = azurerm_user_assigned_identity.migration.client_id
}

output "migration_identity_principal_id" {
  description = "Non-secret principal ID of the Terraform-managed migration identity."
  value       = azurerm_user_assigned_identity.migration.principal_id
}
