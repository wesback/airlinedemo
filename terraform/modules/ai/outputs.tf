output "account_id" {
  description = "Non-secret resource identifier for the Azure OpenAI account."
  value       = azurerm_cognitive_account.this.id
}

output "endpoint" {
  description = "Public Azure OpenAI endpoint protected by network ACLs."
  value       = azurerm_cognitive_account.this.endpoint
}

output "deployment_id" {
  description = "Non-secret resource identifier for the approved model deployment."
  value       = azurerm_cognitive_deployment.this.id
}

output "deployment_name" {
  description = "Name of the approved Azure OpenAI model deployment."
  value       = azurerm_cognitive_deployment.this.name
}
