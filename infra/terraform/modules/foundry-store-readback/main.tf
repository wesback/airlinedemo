terraform {
  required_providers {
    azurerm = {
      source = "hashicorp/azurerm"
    }
  }
}

variable "containers" {
  description = "Exact service-generated project container names."
  type        = map(string)
}

variable "storage_account_id" {
  description = "Exact state storage account ARM ID; never use the legacy data-plane lookup."
  type        = string
}

variable "cosmos_account_name" {
  description = "Exact account containing the service-created enterprise_memory database."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group containing the project state stores."
  type        = string
}

data "azurerm_storage_container" "project" {
  for_each           = var.containers
  name               = each.value
  storage_account_id = var.storage_account_id
}

data "azurerm_cosmosdb_sql_database" "project" {
  name                = "enterprise_memory"
  resource_group_name = var.resource_group_name
  account_name        = var.cosmos_account_name
}

output "scopes" {
  description = "Read-only service scopes and provider-normalized container ACLs for fail-closed grant checks."
  value = {
    containers = {
      for key, container in data.azurerm_storage_container.project : key => {
        id     = container.id
        access = container.container_access_type
      }
    }
    cosmosDatabaseId = data.azurerm_cosmosdb_sql_database.project.id
  }
}
