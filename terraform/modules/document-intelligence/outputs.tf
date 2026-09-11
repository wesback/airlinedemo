output "account_id" {
  description = "Non-secret resource identifier for Document Intelligence."
  value       = azurerm_cognitive_account.this.id
}

output "endpoint" {
  description = "Public Document Intelligence endpoint protected by network ACLs."
  value       = azurerm_cognitive_account.this.endpoint
}
