output "resource_group_id" {
  description = "Resource identifier for the dedicated demo resource group."
  value       = azurerm_resource_group.this.id
}

output "resource_group_name" {
  description = "Name of the dedicated demo resource group."
  value       = azurerm_resource_group.this.name
}

output "runtime_host_storage_account_id" {
  description = "Resource identifier for Durable Functions host/task-hub storage."
  value       = azurerm_storage_account.runtime_host.id
}

output "runtime_host_storage_account_name" {
  description = "Name of Durable Functions host/task-hub storage."
  value       = azurerm_storage_account.runtime_host.name
}

output "evidence_storage_account_id" {
  description = "Resource identifier for private evidence storage."
  value       = azurerm_storage_account.evidence.id
}

output "evidence_storage_account_name" {
  description = "Name of private evidence storage."
  value       = azurerm_storage_account.evidence.name
}
