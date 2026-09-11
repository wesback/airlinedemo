resource "azurerm_mssql_server" "this" {
  name                          = "sql-${var.deployment_name}"
  resource_group_name           = var.resource_group_name
  location                      = var.region
  version                       = "12.0"
  minimum_tls_version           = "1.2"
  public_network_access_enabled = true

  azuread_administrator {
    login_username              = var.sql_aad_admin_login
    object_id                   = var.sql_aad_admin_object_id
    azuread_authentication_only = true
  }

  identity {
    type = "SystemAssigned"
  }

  tags = var.tags
}

resource "azurerm_mssql_database" "this" {
  name                        = "sqldb-${var.deployment_name}"
  server_id                   = azurerm_mssql_server.this.id
  sku_name                    = "GP_S_Gen5_2"
  max_size_gb                 = 5
  min_capacity                = 0.5
  auto_pause_delay_in_minutes = 60
  collation                   = "SQL_Latin1_General_CP1_CI_AS"
  zone_redundant              = false
  tags                        = var.tags
}

resource "azurerm_mssql_firewall_rule" "azure_services" {
  name             = "AllowAzureServices"
  server_id        = azurerm_mssql_server.this.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}
