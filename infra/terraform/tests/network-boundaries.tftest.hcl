mock_provider "azurerm" {}

variables {
  subscription_id               = "00000000-0000-0000-0000-000000000000"
  tenant_id                     = "11111111-1111-1111-1111-111111111111"
  location                      = "uksouth"
  resource_group_name           = "rg-demo-test"
  vnet_address_space            = ["10.60.0.0/16"]
  app_subnet_cidr               = "10.60.1.0/24"
  private_endpoint_subnet_cidr  = "10.60.2.0/24"
  presenter_reviewer_cidrs      = ["203.0.113.0/24"]
  egress_allowed_destinations   = ["198.51.100.0/24"]
  sql_server_name               = "sql-demo-test"
  sql_admin_group_name          = "grp-demo-sql-admins"
  sql_admin_group_object_id     = "22222222-2222-2222-2222-222222222222"
  storage_account_name          = "stdemotest"
  key_vault_name                = "kv-demo-test"
  model_account_name            = "aoai-demo-test"
  model_endpoint_subdomain      = "aoai-demo-test"
  terraform_principal_object_id = "33333333-3333-3333-3333-333333333333"
  budget_amount                 = 100
  budget_start_date             = "2026-11-01T00:00:00Z"
  budget_contact_emails         = ["ops@example.invalid"]
  terraform_state_backend = {
    storage_account_name = "stdemotfstate"
    container_name       = "tfstate"
    key                  = "demo.tfstate"
    use_azuread_auth     = true
    locking_enabled      = true
  }
}

run "ingress_and_egress_allowlists" {
  command = plan

  assert {
    condition     = azurerm_network_security_rule.ingress_allow["0"].source_address_prefix == "203.0.113.0/24" && azurerm_network_security_rule.ingress_deny_all.access == "Deny"
    error_message = "Approved presenter/reviewer CIDR must be allowed inbound and inbound must end in deny-all."
  }
  assert {
    condition     = azurerm_network_security_rule.egress_allow["0"].destination_address_prefix == "198.51.100.0/24" && azurerm_network_security_rule.egress_deny_all.access == "Deny"
    error_message = "Only the approved egress destination may be allowed and outbound must end in deny-all."
  }
}

run "empty_allowlists_deny_everything" {
  command = plan

  variables {
    presenter_reviewer_cidrs    = []
    egress_allowed_destinations = []
  }

  assert {
    condition     = length(azurerm_network_security_rule.ingress_allow) == 0 && length(azurerm_network_security_rule.egress_allow) == 0
    error_message = "Empty allowlists must create no inbound or outbound allow rules."
  }
}

run "data_endpoints_are_private" {
  command = plan

  assert {
    condition     = azurerm_mssql_server.sql.public_network_access_enabled == false && azurerm_storage_account.blob.public_network_access_enabled == false && azurerm_cognitive_account.model.public_network_access_enabled == false && azurerm_cognitive_account.model.local_auth_enabled == false
    error_message = "SQL, Blob and model public access must be disabled, and model key auth off."
  }
  assert {
    condition     = alltrue([for k in ["sql", "blob", "model"] : contains(keys(azurerm_private_endpoint.demo), k)])
    error_message = "SQL, Blob and model each need a private endpoint."
  }
}

run "encryption_and_no_key_outputs" {
  command = plan

  assert {
    condition     = azurerm_storage_account.blob.infrastructure_encryption_enabled == true && azurerm_storage_account.blob.shared_access_key_enabled == false && azurerm_key_vault.kv.purge_protection_enabled == true
    error_message = "Blob needs infrastructure encryption and no shared keys; Key Vault needs purge protection."
  }
  assert {
    condition     = azurerm_mssql_server.sql.minimum_tls_version == "1.2" && azurerm_mssql_server.sql.azuread_administrator[0].azuread_authentication_only == true
    error_message = "SQL must require TLS 1.2 and Entra-only auth."
  }
  assert {
    condition     = output.model_endpoint == "https://aoai-demo-test.openai.azure.com/"
    error_message = "Model endpoint must be HTTPS on the approved subdomain."
  }
}

run "rejects_anonymous_backend" {
  command = plan

  variables {
    terraform_state_backend = {
      storage_account_name = "stdemotfstate"
      container_name       = "tfstate"
      key                  = "demo.tfstate"
      use_azuread_auth     = false
      locking_enabled      = true
    }
  }

  expect_failures = [var.terraform_state_backend]
}

run "rejects_backend_without_locking" {
  command = plan

  variables {
    terraform_state_backend = {
      storage_account_name = "stdemotfstate"
      container_name       = "tfstate"
      key                  = "demo.tfstate"
      use_azuread_auth     = true
      locking_enabled      = false
    }
  }

  expect_failures = [var.terraform_state_backend]
}
