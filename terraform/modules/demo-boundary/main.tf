locals {
  storage_name_prefix = substr(replace(lower(var.deployment_name), "-", ""), 0, 16)
}

resource "azurerm_resource_group" "this" {
  name     = var.resource_group_name
  location = var.region
  tags     = var.tags
}

resource "azurerm_storage_account" "evidence" {
  name                     = "st${local.storage_name_prefix}evid"
  resource_group_name      = azurerm_resource_group.this.name
  location                 = azurerm_resource_group.this.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  account_kind             = "StorageV2"

  min_tls_version                 = "TLS1_2"
  allow_nested_items_to_be_public = false
  public_network_access_enabled   = false
  shared_access_key_enabled       = false
  tags                            = var.tags

  network_rules {
    default_action = "Deny"
  }

  blob_properties {
    versioning_enabled = true
  }
}

resource "azurerm_storage_management_policy" "evidence" {
  storage_account_id = azurerm_storage_account.evidence.id

  rule {
    name    = "retain-evidence-versions"
    enabled = true

    filters {
      blob_types = ["blockBlob"]
    }

    actions {
      base_blob {
        delete_after_days_since_modification_greater_than = var.evidence_retention_days
      }

      snapshot {
        delete_after_days_since_creation_greater_than = var.evidence_retention_days
      }

      version {
        delete_after_days_since_creation = var.evidence_retention_days
      }
    }
  }
}
