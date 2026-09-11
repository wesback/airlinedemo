output "function_app_id" {
  description = "Non-secret resource identifier for the Flex Consumption Function App."
  value       = azurerm_function_app_flex_consumption.this.id
}

output "function_app_endpoint" {
  description = "HTTPS endpoint for the Flex Consumption Function App."
  value       = "https://${azurerm_function_app_flex_consumption.this.default_hostname}"
}
