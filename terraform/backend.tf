terraform {
  backend "azurerm" {
    resource_group_name  = "rg-airlinedemo-state"
    storage_account_name = "stairlinedemostate"
    container_name       = "tfstate"
    key                  = "airlinedemo.tfstate"

    # The azurerm backend uses Azure Blob native leases for state locking.
    use_azuread_auth = true
  }
}
