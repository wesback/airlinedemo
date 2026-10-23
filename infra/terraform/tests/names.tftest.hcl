mock_provider "azurerm" {}

# Only the inputs that cannot be derived. Every name and range comes from name_prefix.
variables {
  subscription_id               = "00000000-0000-0000-0000-000000000000"
  tenant_id                     = "11111111-1111-1111-1111-111111111111"
  location                      = "uksouth"
  name_prefix                   = "demo1"
  presenter_reviewer_cidrs      = ["203.0.113.0/24"]
  sql_admin_group_name          = "grp-demo-sql-admins"
  sql_admin_group_object_id     = "22222222-2222-2222-2222-222222222222"
  model_name                    = "gpt-demo-model"
  model_version                 = "0000-00-00"
  terraform_principal_object_id = "33333333-3333-3333-3333-333333333333"
  budget_amount                 = 100
  budget_start_date             = "2026-11-01T00:00:00Z"
  budget_contact_emails         = ["ops@example.invalid"]
  telemetry_reader_group_id     = "44444444-4444-4444-4444-444444444444"
  image_digest                  = "sha256:0000000000000000000000000000000000000000000000000000000000000000"
  demo_passcode_secret_uri      = "https://kv-demo-test.vault.azure.net/secrets/demo-passcode"
  security_response_owner       = { name = "Response Owner", email = "owner@example.invalid" }
  terraform_state_backend       = { storage_account_name = "stdemotfstate" }
  resource_group_name               = null
  vnet_address_space                = ["10.20.0.0/16"]
  app_subnet_cidr                   = null
  private_endpoint_subnet_cidr      = null
  egress_allowed_destinations       = []
  sql_server_name                   = null
  sql_database_name                 = "airlinedemo"
  key_vault_name                    = null
  model_account_name                = null
  model_endpoint_subdomain          = null
  acr_name                          = null
  container_apps_environment_name   = null
  container_app_name                = null
  storage_account_name              = null
}

run "names_and_ranges_derive_from_prefix" {
  command = plan

  assert {
    condition     = azurerm_resource_group.demo.name == "rg-demo1" && azurerm_container_app.app.name == "ca-demo1" && azurerm_container_app_environment.demo.name == "cae-demo1" && azurerm_cognitive_deployment.model.name == "gpt-demo1"
    error_message = "Resource group, container app, environment and model deployment names must derive from name_prefix."
  }

  assert {
    condition     = can(regex("^stdemo1[0-9a-f]{6}$", azurerm_storage_account.blob.name)) && can(regex("^kv-demo1-[0-9a-f]{6}$", azurerm_key_vault.kv.name)) && can(regex("^acrdemo1[0-9a-f]{6}$", azurerm_container_registry.acr.name)) && can(regex("^sql-demo1-[0-9a-f]{6}$", azurerm_mssql_server.sql.name))
    error_message = "Globally unique names must be the prefix plus a six character hexadecimal suffix."
  }

  assert {
    condition     = length(azurerm_storage_account.blob.name) <= 24 && length(azurerm_key_vault.kv.name) <= 24
    error_message = "Storage account and key vault names must fit the 24 character Azure limit."
  }

  assert {
    condition     = azurerm_subnet.app.address_prefixes == tolist(["10.20.1.0/24"]) && azurerm_subnet.private_endpoints.address_prefixes == tolist(["10.20.2.0/24"]) && azurerm_virtual_network.demo.address_space == toset(["10.20.0.0/16"])
    error_message = "Subnets must be the second and third /24 of the default 10.20.0.0/16 range."
  }
}

run "explicit_values_override_derived_names" {
  command = plan

  variables {
    resource_group_name = "rg-explicit"
    app_subnet_cidr     = "10.99.5.0/24"
    vnet_address_space  = ["10.99.0.0/16"]
  }

  assert {
    condition     = azurerm_resource_group.demo.name == "rg-explicit" && azurerm_subnet.app.address_prefixes == tolist(["10.99.5.0/24"]) && azurerm_subnet.private_endpoints.address_prefixes == tolist(["10.99.2.0/24"])
    error_message = "An explicit name or range must win; the other subnet still derives from the explicit vnet range."
  }
}

run "missing_prefix_without_explicit_names_is_rejected" {
  command = plan

  variables {
    name_prefix = null
  }

  expect_failures = [var.name_prefix]
}

run "invalid_prefix_is_rejected" {
  command = plan

  variables {
    name_prefix = "Demo_1"
  }

  expect_failures = [var.name_prefix]
}
