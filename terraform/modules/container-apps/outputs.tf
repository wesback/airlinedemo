output "container_app_id" {
  description = "Non-secret resource identifier for the Container App."
  value       = azurerm_container_app.this.id
}

output "container_registry_id" {
  description = "Non-secret resource identifier for the application container registry."
  value       = azurerm_container_registry.application.id
}
