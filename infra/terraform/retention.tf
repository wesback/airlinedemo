# Retention controls for evidence and business audit (RETENTION-01..03).
# Blob versioning and 14-day soft delete are set on the evidence storage account in main.tf (blob_properties).

# Point-in-time restore window for the SQL database. Audit rows are never expired or overwritten by policy:
# app grants in identity-and-telemetry.tf keep audit.case_events append-only.
resource "azurerm_mssql_database" "airline" {
  name      = local.sql_database_name
  server_id = azurerm_mssql_server.sql.id

  short_term_retention_policy {
    retention_days = 35
  }
}
