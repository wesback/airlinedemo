variable "foundry_state_profile" {
  description = "Explicit candidate state sizing and retention; no defaults or live compatibility are inferred. Current preflight evidence and confirmation remain required."
  type = object({
    cosmos_total_throughput_limit  = number
    cosmos_backup_interval_minutes = number
    cosmos_backup_retention_hours  = number
    cosmos_consistency_level       = string
    search_sku                     = string
    search_partitions              = number
    search_replicas                = number
    search_location                = optional(string)
    storage_replication_type       = string
    blob_retention_days            = number
    container_retention_days       = number
  })
  default = null

  validation {
    condition = var.foundry_state_profile == null ? true : (
      var.foundry_state_profile.search_location == null ||
      contains(["swedencentral", "germanywestcentral", "francecentral"], var.foundry_state_profile.search_location)
    )
    error_message = "Foundry Search location must be Sweden Central, Germany West Central or France Central."
  }
  validation {
    condition     = !var.foundry_enabled || var.foundry_state_profile != null
    error_message = "Enabling Foundry requires an explicit foundry_state_profile; capacity and retention must not be guessed."
  }
  validation {
    condition = var.foundry_state_profile == null ? true : (
      var.foundry_state_profile.cosmos_total_throughput_limit > 0 &&
      floor(var.foundry_state_profile.cosmos_total_throughput_limit) == var.foundry_state_profile.cosmos_total_throughput_limit &&
      var.foundry_state_profile.search_sku != "free" &&
      contains(["LRS", "ZRS"], var.foundry_state_profile.storage_replication_type) &&
      alltrue([for count in [var.foundry_state_profile.search_partitions, var.foundry_state_profile.search_replicas] :
      count > 0 && floor(count) == count])
    )
    error_message = "Foundry requires a bounded positive integral Cosmos ceiling, private-endpoint-capable Search with positive integral counts, and region-local LRS/ZRS storage."
  }
}

locals {
  foundry_state_count = var.foundry_enabled && var.foundry_state_profile != null ? 1 : 0
  foundry_state_endpoints = local.foundry_state_count == 0 ? {} : {
    document = { id = azurerm_cosmosdb_account.foundry[0].id, group = "Sql", zone = azurerm_private_dns_zone.foundry["document"].id }
    search   = { id = azurerm_search_service.foundry[0].id, group = "searchService", zone = azurerm_private_dns_zone.foundry["search"].id }
    blob     = { id = azurerm_storage_account.foundry[0].id, group = "blob", zone = local.blob_dns_zone_id }
  }
}

resource "azurerm_cosmosdb_account" "foundry" {
  count                                 = local.foundry_state_count
  name                                  = "cosmos-${local.name_prefix}-${local.name_suffix}-foundry"
  location                              = azurerm_resource_group.demo.location
  resource_group_name                   = azurerm_resource_group.demo.name
  offer_type                            = "Standard"
  kind                                  = "GlobalDocumentDB"
  local_authentication_enabled          = false
  public_network_access_enabled         = false
  network_acl_bypass_for_azure_services = false
  minimal_tls_version                   = "Tls12"

  capacity {
    total_throughput_limit = var.foundry_state_profile.cosmos_total_throughput_limit
  }
  consistency_policy {
    consistency_level = var.foundry_state_profile.cosmos_consistency_level
  }
  backup {
    type                = "Periodic"
    interval_in_minutes = var.foundry_state_profile.cosmos_backup_interval_minutes
    retention_in_hours  = var.foundry_state_profile.cosmos_backup_retention_hours
    storage_redundancy  = "Local"
  }
  geo_location {
    location          = var.location
    failover_priority = 0
  }
  identity {
    type = "SystemAssigned"
  }
  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_storage_account" "foundry" {
  count                           = local.foundry_state_count
  name                            = "stf${local.name_prefix}${local.name_suffix}"
  location                        = azurerm_resource_group.demo.location
  resource_group_name             = azurerm_resource_group.demo.name
  account_kind                    = "StorageV2"
  account_tier                    = "Standard"
  account_replication_type        = var.foundry_state_profile.storage_replication_type
  min_tls_version                 = "TLS1_2"
  https_traffic_only_enabled      = true
  shared_access_key_enabled       = false
  default_to_oauth_authentication = true
  allow_nested_items_to_be_public = false
  public_network_access_enabled   = false

  network_rules {
    default_action = "Deny"
    bypass         = ["None"]
  }
  blob_properties {
    versioning_enabled = false
    delete_retention_policy {
      days = var.foundry_state_profile.blob_retention_days
    }
    container_delete_retention_policy {
      days = var.foundry_state_profile.container_retention_days
    }
  }
  identity {
    type = "SystemAssigned"
  }
  lifecycle {
    prevent_destroy = true
    # Tenant security automation owns the Defender storage-scanner access links.
    ignore_changes = [network_rules[0].private_link_access]
  }
}

resource "azurerm_search_service" "foundry" {
  count                         = local.foundry_state_count
  name                          = "srch-${local.name_prefix}-${local.name_suffix}-foundry"
  location                      = coalesce(var.foundry_state_profile.search_location, azurerm_resource_group.demo.location)
  resource_group_name           = azurerm_resource_group.demo.name
  sku                           = var.foundry_state_profile.search_sku
  partition_count               = var.foundry_state_profile.search_partitions
  replica_count                 = var.foundry_state_profile.search_replicas
  local_authentication_enabled  = false
  public_network_access_enabled = false

  identity {
    type = "SystemAssigned"
  }
  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_private_endpoint" "foundry_state" {
  for_each            = local.foundry_state_endpoints
  name                = "pe-foundry-${each.key}"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name
  subnet_id           = azurerm_subnet.private_endpoints.id

  private_service_connection {
    name                           = "foundry-${each.key}"
    private_connection_resource_id = each.value.id
    subresource_names              = [each.value.group]
    is_manual_connection           = false
  }
  private_dns_zone_group {
    name                 = "foundry-${each.key}"
    private_dns_zone_ids = [each.value.zone]
  }
}

resource "azurerm_role_assignment" "foundry_provisioner_blob" {
  count                = local.foundry_state_count
  scope                = azurerm_storage_account.foundry[0].id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = var.terraform_principal_object_id
}

resource "azurerm_role_assignment" "foundry_provisioner_cosmos" {
  count                = local.foundry_state_count
  scope                = azurerm_cosmosdb_account.foundry[0].id
  role_definition_name = "Cosmos DB Operator"
  principal_id         = var.terraform_principal_object_id
}
