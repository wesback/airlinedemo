output "application_insights_id" {
  description = "Resource identifier for Application Insights."
  value       = azurerm_application_insights.this.id
}

output "log_analytics_workspace_id" {
  description = "Resource identifier for the Log Analytics workspace."
  value       = azurerm_log_analytics_workspace.this.id
}
