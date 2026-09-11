output "sql_server_id" {
  description = "Non-secret resource identifier for the Azure SQL logical server."
  value       = azurerm_mssql_server.this.id
}

output "sql_server_fully_qualified_domain_name" {
  description = "Public Azure SQL endpoint protected by the server firewall."
  value       = azurerm_mssql_server.this.fully_qualified_domain_name
}

output "sql_database_id" {
  description = "Non-secret resource identifier for the workflow database."
  value       = azurerm_mssql_database.this.id
}
