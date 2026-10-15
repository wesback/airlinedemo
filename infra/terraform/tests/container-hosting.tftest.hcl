mock_provider "azurerm" {
  mock_resource "azurerm_container_registry" {
    defaults = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ContainerRegistry/registries/acrdemotest"
      login_server = "acrdemotest.azurecr.io"
    }
  }

  mock_resource "azurerm_user_assigned_identity" {
    defaults = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-airline-app"
      principal_id = "55555555-5555-5555-5555-555555555555"
      client_id    = "66666666-6666-6666-6666-666666666666"
    }
  }

  mock_resource "azurerm_container_app_environment" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.App/managedEnvironments/cae-demo-test"
    }
  }

  mock_resource "azurerm_container_app" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.App/containerApps/ca-airline-api"
    }
  }

  mock_resource "azurerm_private_dns_zone" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Network/privateDnsZones/privatelink.azurecr.io"
    }
  }

  mock_resource "azurerm_subnet" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Network/virtualNetworks/vnet-demo/subnets/snet-app"
    }
  }

  mock_resource "azurerm_key_vault" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test"
    }
  }

  mock_resource "azurerm_mssql_server" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Sql/servers/sql-demo-test"
    }
  }

  mock_resource "azurerm_storage_account" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stdemotest"
    }
  }

  mock_resource "azurerm_cognitive_account" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.CognitiveServices/accounts/aoai-demo-test"
    }
  }

  mock_resource "azurerm_virtual_network" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Network/virtualNetworks/vnet-demo"
    }
  }

  mock_resource "azurerm_log_analytics_workspace" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.OperationalInsights/workspaces/log-demo-test"
    }
  }
}

variables {
  subscription_id                 = "00000000-0000-0000-0000-000000000000"
  tenant_id                       = "11111111-1111-1111-1111-111111111111"
  location                        = "uksouth"
  resource_group_name             = "rg-demo-test"
  vnet_address_space              = ["10.60.0.0/16"]
  app_subnet_cidr                 = "10.60.1.0/24"
  private_endpoint_subnet_cidr    = "10.60.2.0/24"
  presenter_reviewer_cidrs        = ["203.0.113.0/24"]
  egress_allowed_destinations     = ["198.51.100.0/24"]
  sql_server_name                 = "sql-demo-test"
  sql_database_name               = "airlinedemo"
  storage_account_name            = "stdemotest"
  key_vault_name                  = "kv-demo-test"
  model_account_name              = "aoai-demo-test"
  model_endpoint_subdomain        = "aoai-demo-test"
  acr_name                        = "acrdemotest"
  container_apps_environment_name = "cae-demo-test"
  container_app_name              = "ca-airline-api"
  image_digest                    = "sha256:0000000000000000000000000000000000000000000000000000000000000000"
  demo_passcode_secret_uri        = "https://kv-demo-test.vault.azure.net/secrets/demo-passcode"
  sql_admin_group_name            = "grp-demo-sql-admins"
  sql_admin_group_object_id       = "22222222-2222-2222-2222-222222222222"
  terraform_principal_object_id   = "33333333-3333-3333-3333-333333333333"
  budget_amount                   = 100
  budget_start_date               = "2026-11-01T00:00:00Z"
  budget_contact_emails           = ["ops@example.invalid"]
  telemetry_reader_group_id       = "44444444-4444-4444-4444-444444444444"
  security_response_owner         = { name = "Response Owner", email = "owner@example.invalid" }
  security_alert_settings         = { enabled = true, failure_threshold = 5, window_minutes = 15, evaluation_frequency_minutes = 5 }
  terraform_state_backend = {
    storage_account_name = "stdemotfstate"
    container_name       = "tfstate"
    key                  = "demo.tfstate"
    use_azuread_auth     = true
    locking_enabled      = true
  }
}

run "private_acr_dns_and_container_apps_wiring" {
  command = plan

  assert {
    condition     = azurerm_container_registry.acr.sku == "Premium" && azurerm_container_registry.acr.admin_enabled == false && azurerm_container_registry.acr.public_network_access_enabled == false
    error_message = "ACR must be Premium, admin disabled and public access disabled."
  }

  assert {
    condition     = local.private_endpoints.acr.subresource == "registry" && local.private_dns_zones[local.private_endpoints.acr.zone] == "privatelink.azurecr.io"
    error_message = "ACR private endpoint must use subresource registry and the privatelink.azurecr.io zone."
  }

  assert {
    condition     = azurerm_container_app.app.ingress[0].allow_insecure_connections == false && azurerm_container_app.app.ingress[0].target_port == 8080
    error_message = "Container App ingress must disallow insecure connections and target port 8080."
  }

  assert {
    condition     = azurerm_container_app.app.identity[0].type == "UserAssigned"
    error_message = "Container App must use the user-assigned identity for registry pulls."
  }
}

run "ingress_denies_when_presenter_list_empty" {
  command = plan

  variables {
    presenter_reviewer_cidrs = []
  }

  assert {
    condition     = length([for r in azurerm_container_app.app.ingress[0].ip_security_restriction : r if r.action == "Deny"]) == 1 && length([for r in azurerm_container_app.app.ingress[0].ip_security_restriction : r if r.action == "Allow"]) == 0
    error_message = "An empty presenter_reviewer_cidrs list must produce a deny-all inbound rule and no allow rules."
  }
}

run "passcode_is_key_vault_secret_reference" {
  command = plan

  assert {
    condition     = [for s in azurerm_container_app.app.secret : s.key_vault_secret_id][0] == "https://kv-demo-test.vault.azure.net/secrets/demo-passcode"
    error_message = "The demo passcode must be a Container Apps secret referencing Key Vault."
  }
}

run "rejects_tag_only_image_reference" {
  command = plan

  variables {
    image_digest = "latest"
  }

  expect_failures = [
    var.image_digest,
  ]
}

run "rejects_missing_passcode_secret_uri" {
  command = plan

  variables {
    demo_passcode_secret_uri = ""
  }

  expect_failures = [
    var.demo_passcode_secret_uri,
  ]
}

run "rejects_invalid_presenter_cidr" {
  command = plan

  variables {
    presenter_reviewer_cidrs = ["203.0.113.0/33"]
  }

  expect_failures = [
    var.presenter_reviewer_cidrs,
  ]
}
