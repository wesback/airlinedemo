locals {
  state_tags = {
    owner       = var.owner
    cost_center = var.cost_center
    managed_by  = "terraform-bootstrap"
    purpose     = "airlinedemo-terraform-state"
  }
}

resource "azurerm_resource_group" "state" {
  name     = var.state_resource_group_name
  location = var.region
  tags     = local.state_tags
}

resource "azurerm_storage_account" "state" {
  name                            = var.state_storage_account_name
  resource_group_name             = azurerm_resource_group.state.name
  location                        = azurerm_resource_group.state.location
  account_kind                    = "StorageV2"
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  https_traffic_only_enabled      = true
  allow_nested_items_to_be_public = false
  public_network_access_enabled   = true
  shared_access_key_enabled       = false
  tags                            = local.state_tags

  network_rules {
    default_action = "Deny"
    ip_rules       = var.approved_operator_ip_ranges
    bypass         = []
  }

  blob_properties {
    versioning_enabled = true
  }
}

resource "azurerm_storage_container" "state" {
  name                  = var.state_container_name
  storage_account_id    = azurerm_storage_account.state.id
  container_access_type = "private"
}

resource "azurerm_role_assignment" "deployment_state_blob_contributor" {
  scope                = azurerm_storage_container.state.resource_manager_id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = var.deployment_principal_object_id
}
