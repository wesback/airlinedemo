mock_provider "azurerm" {
  mock_resource "azurerm_user_assigned_identity" {
    defaults = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-mock"
      principal_id = "55555555-5555-5555-5555-555555555555"
      client_id    = "77777777-7777-7777-7777-777777777777"
    }
  }

  mock_resource "azurerm_container_registry" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ContainerRegistry/registries/acrdemotest"
    }
  }

  mock_resource "azurerm_key_vault" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test"
    }
  }

  mock_resource "azurerm_key_vault_key" {
    defaults = {
      id                    = "https://kv-demo-test.vault.azure.net/keys/release-image-signing/0000"
      resource_versionless_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test/keys/release-image-signing"
    }
  }

  mock_resource "azurerm_key_vault_certificate" {
    defaults = {
      id                  = "https://kv-demo-test.vault.azure.net/certificates/release-image-signing-certificate/0000"
      resource_manager_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test/certificates/release-image-signing-certificate"
    }
  }

  mock_resource "azurerm_container_app" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.App/containerApps/ca-airline-demo-test"
    }
  }

  mock_resource "azurerm_container_app_environment" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.App/managedEnvironments/cae-demo-test"
    }
  }

  mock_resource "azurerm_resource_group" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test"
    }
  }

  mock_resource "azurerm_virtual_network" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Network/virtualNetworks/vnet-demo-test"
    }
  }

  mock_resource "azurerm_subnet" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Network/virtualNetworks/vnet-demo-test/subnets/snet-app"
    }
  }

  mock_resource "azurerm_network_security_group" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Network/networkSecurityGroups/nsg-app"
    }
  }

  mock_resource "azurerm_private_dns_zone" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Network/privateDnsZones/privatelink.database.windows.net"
    }
  }

  mock_resource "azurerm_private_endpoint" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Network/privateEndpoints/pe-demo-test"
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

  mock_resource "azurerm_storage_container" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stdemotest/blobServices/default/containers/evidence"
    }
  }

  mock_resource "azurerm_cognitive_account" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.CognitiveServices/accounts/aoai-demo-test"
    }
  }

  mock_resource "azurerm_application_insights" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Insights/components/appi-airline-telemetry"
    }
  }

  mock_resource "azurerm_log_analytics_workspace" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.OperationalInsights/workspaces/log-airline-telemetry"
    }
  }

  mock_resource "azurerm_monitor_action_group" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Insights/actionGroups/ag-security-owner"
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
  sql_admin_group_name            = "grp-demo-sql-admins"
  sql_admin_group_object_id       = "22222222-2222-2222-2222-222222222222"
  storage_account_name            = "stdemotest"
  key_vault_name                  = "kv-demo-test"
  model_account_name              = "aoai-demo-test"
  model_endpoint_subdomain        = "aoai-demo-test"
  terraform_principal_object_id   = "33333333-3333-3333-3333-333333333333"
  budget_amount                   = 100
  budget_start_date               = "2026-11-01T00:00:00Z"
  budget_contact_emails           = ["ops@example.invalid"]
  telemetry_reader_group_id       = "44444444-4444-4444-4444-444444444444"
  sql_database_name               = "airlinedemo"
  acr_name                        = "acrdemotest"
  container_apps_environment_name = "cae-demo-test"
  container_app_name              = "ca-airline-demo-test"
  image_digest                    = "sha256:0000000000000000000000000000000000000000000000000000000000000000"
  demo_passcode_secret_uri        = "https://kv-demo-test.vault.azure.net/secrets/demo-passcode"
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

run "registry_is_private_and_pull_scoped" {
  command = apply

  override_resource {
    target = azurerm_container_registry.acr
    values = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ContainerRegistry/registries/acrdemotest"
    }
  }

  override_resource {
    target = azurerm_user_assigned_identity.app
    values = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-airline-app"
      principal_id = "55555555-5555-5555-5555-555555555555"
    }
  }

  assert {
    condition     = azurerm_container_registry.acr.public_network_access_enabled == false
    error_message = "The ACR must have public network access disabled."
  }

  assert {
    condition     = azurerm_container_registry.acr.admin_enabled == false
    error_message = "The ACR admin user must be disabled; no registry password may exist."
  }

  assert {
    condition     = azurerm_private_endpoint.demo["acr"].private_service_connection[0].subresource_names[0] == "registry"
    error_message = "The ACR private endpoint must target the registry subresource."
  }

  assert {
    condition     = azurerm_private_endpoint.demo["acr"].private_dns_zone_group[0].private_dns_zone_ids[0] == azurerm_private_dns_zone.demo["acr"].id && azurerm_private_dns_zone.demo["acr"].name == "privatelink.azurecr.io"
    error_message = "The ACR private endpoint must be registered in the privatelink.azurecr.io zone."
  }

  assert {
    condition     = azurerm_role_assignment.app_acr_pull.role_definition_name == "AcrPull" && azurerm_role_assignment.app_acr_pull.scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ContainerRegistry/registries/acrdemotest" && azurerm_role_assignment.app_acr_pull.principal_id == "55555555-5555-5555-5555-555555555555"
    error_message = "The app identity's only ACR grant must be AcrPull scoped to the ACR."
  }

  assert {
    condition     = length(regexall("role_definition_name\\s*=\\s*.AcrPull.", join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")]))) == 1
    error_message = "Exactly one AcrPull assignment may exist in the configuration; any extra or broader registry grant fails."
  }

  assert {
    condition     = length([for b in flatten([for f in fileset(path.module, "*.tf") : split("resource \"", file("${path.module}/${f}"))]) : b if length(regexall("^azurerm_role_assignment\"", b)) > 0 && length(regexall("azurerm_user_assigned_identity\\.app\\.principal_id", b)) > 0 && length(regexall("azurerm_container_registry\\.acr\\.id", b)) > 0]) == 1 && length(regexall("role_definition_name\\s*=\\s*\"AcrPull\"", join("", [for b in flatten([for f in fileset(path.module, "*.tf") : split("resource \"", file("${path.module}/${f}"))]) : b if length(regexall("^azurerm_role_assignment\"", b)) > 0 && length(regexall("azurerm_user_assigned_identity\\.app\\.principal_id", b)) > 0 && length(regexall("azurerm_container_registry\\.acr\\.id", b)) > 0]))) == 1
    error_message = "The app identity may hold exactly one role assignment on the ACR, and it must be AcrPull; any other ACR role assignment for the app identity (such as AcrPush) fails."
  }

  assert {
    condition     = length([for b in flatten([for f in fileset(path.module, "*.tf") : split("resource \"", file("${path.module}/${f}"))]) : b if length(regexall("^azurerm_role_assignment\"", b)) > 0 && length(regexall("role_definition_name\\s*=\\s*\"Acr", b)) > 0 && length(regexall("(?m)^\\s*principal_id\\s*=\\s*azurerm_user_assigned_identity\\.(app|build|release)\\.principal_id\\s*$", b)) == 0]) == 0
    error_message = "An AcrPull or AcrPush assignment's principal must be exactly azurerm_user_assigned_identity.app, .build or .release principal_id on its own line. A local, variable, index, function or interpolated principal could hide a registry grant to the app identity this test cannot see, so it fails."
  }

  assert {
    condition     = length([for b in flatten([for f in fileset(path.module, "*.tf") : split("resource \"", file("${path.module}/${f}"))]) : b if length(regexall("^azurerm_role_assignment\"", b)) > 0 && length(regexall("role_definition_name\\s*=\\s*\"Acr", b)) > 0 && length(regexall("(?m)^\\s*scope\\s*=\\s*azurerm_container_registry\\.acr\\.id\\s*$", b)) == 0]) == 0
    error_message = "Every AcrPull or AcrPush assignment must be scoped to azurerm_container_registry.acr.id; any registry grant on another scope fails."
  }

  assert {
    condition     = length([for b in flatten([for f in fileset(path.module, "*.tf") : split("resource \"", file("${path.module}/${f}"))]) : b if length(regexall("^azurerm_role_assignment\"", b)) > 0 && (length(regexall("(?m)^\\s*role_definition_name\\s*=\\s*\"[^\"]+\"\\s*$", b)) != 1 || length(regexall("role_definition_id", b)) > 0)]) == 0
    error_message = "Every role assignment must name its role with one literal role_definition_name and must not use role_definition_id. A computed or local role could hide an ACR grant to the app identity, so it fails."
  }

  assert {
    condition     = length([for b in flatten([for f in fileset(path.module, "*.tf") : split("resource \"", file("${path.module}/${f}"))]) : b if length(regexall("^azurerm_role_assignment\"", b)) > 0 && length(regexall("azurerm_container_registry\\.acr\\.id", b)) > 0 && length(regexall("(?m)^\\s*principal_id\\s*=\\s*azurerm_user_assigned_identity\\.(app|build|release)\\.principal_id\\s*$", b)) == 0]) == 0
    error_message = "Any role assignment that references the ACR must name the app, build or release identity's principal_id directly. Any other ACR grant fails."
  }
}

run "federation_is_limited_to_trusted_workflows" {
  command = apply

  override_resource {
    target = azurerm_user_assigned_identity.build
    values = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-airline-build"
      principal_id = "66666666-6666-6666-6666-666666666666"
      client_id    = "88888888-8888-8888-8888-888888888888"
    }
  }

  override_resource {
    target = azurerm_user_assigned_identity.release
    values = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-airline-release"
      principal_id = "77777777-7777-7777-7777-777777777777"
      client_id    = "99999999-9999-9999-9999-999999999999"
    }
  }

  assert {
    condition     = azurerm_federated_identity_credential.build.user_assigned_identity_id == azurerm_user_assigned_identity.build.id
    error_message = "The build federated credential must be attached to the build identity."
  }

  assert {
    condition     = azurerm_federated_identity_credential.build.subject == "repo:wesback/airlinedemo:ref:refs/heads/main" && length(azurerm_federated_identity_credential.build.audience) == 1 && contains(azurerm_federated_identity_credential.build.audience, "api://AzureADTokenExchange") && azurerm_federated_identity_credential.build.issuer == "https://token.actions.githubusercontent.com"
    error_message = "Build federation must trust only wesback/airlinedemo on refs/heads/main with audience api://AzureADTokenExchange."
  }

  assert {
    condition     = azurerm_federated_identity_credential.release.user_assigned_identity_id == azurerm_user_assigned_identity.release.id
    error_message = "The release federated credential must be attached to the release identity."
  }

  assert {
    condition     = azurerm_federated_identity_credential.release.subject == "repo:wesback/airlinedemo:environment:production-release" && length(azurerm_federated_identity_credential.release.audience) == 1 && contains(azurerm_federated_identity_credential.release.audience, "api://AzureADTokenExchange") && azurerm_federated_identity_credential.release.issuer == "https://token.actions.githubusercontent.com"
    error_message = "Release federation must trust only wesback/airlinedemo in environment production-release with audience api://AzureADTokenExchange."
  }

  assert {
    condition     = length(regexall("resource \"azurerm_federated_identity_credential\"", join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")]))) == 2
    error_message = "Only the build (main) and release (production-release) federated credentials may be declared; any other credential fails."
  }
}

run "signing_rights_are_resource_scoped" {
  command = apply

  override_resource {
    target = azurerm_key_vault_key.signing
    values = {
      resource_versionless_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test/keys/release-image-signing"
    }
  }

  override_resource {
    target = azurerm_key_vault_certificate.signing
    values = {
      resource_manager_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test/certificates/release-image-signing-certificate"
    }
  }

  override_resource {
    target = azurerm_container_registry.acr
    values = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ContainerRegistry/registries/acrdemotest"
    }
  }

  override_resource {
    target = azurerm_key_vault.kv
    values = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test"
    }
  }

  override_resource {
    target = azurerm_container_app.app
    values = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.App/containerApps/ca-airline-demo-test"
    }
  }

  override_resource {
    target = azurerm_user_assigned_identity.build
    values = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-airline-build"
      principal_id = "66666666-6666-6666-6666-666666666666"
    }
  }

  override_resource {
    target = azurerm_user_assigned_identity.release
    values = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-airline-release"
      principal_id = "77777777-7777-7777-7777-777777777777"
    }
  }

  assert {
    condition     = azurerm_role_assignment.build_acr_push.role_definition_name == "AcrPush" && azurerm_role_assignment.build_acr_push.scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ContainerRegistry/registries/acrdemotest" && azurerm_role_assignment.build_acr_push.principal_id == azurerm_user_assigned_identity.build.principal_id
    error_message = "The build identity must hold AcrPush scoped to the ACR only."
  }

  assert {
    condition     = azurerm_role_assignment.release_acr_push.role_definition_name == "AcrPush" && azurerm_role_assignment.release_acr_push.scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ContainerRegistry/registries/acrdemotest" && azurerm_role_assignment.release_acr_push.principal_id == azurerm_user_assigned_identity.release.principal_id
    error_message = "The release identity must hold AcrPush scoped to the ACR only."
  }

  assert {
    condition     = azurerm_role_assignment.release_signing_key.role_definition_name == "Key Vault Crypto User" && azurerm_role_assignment.release_signing_key.scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test/keys/release-image-signing" && azurerm_role_assignment.release_signing_key.principal_id == azurerm_user_assigned_identity.release.principal_id
    error_message = "The release identity must hold Key Vault Crypto User scoped to the signing key only."
  }

  assert {
    condition     = azurerm_role_assignment.release_signing_certificate.role_definition_name == "Key Vault Certificate User" && azurerm_role_assignment.release_signing_certificate.scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test/certificates/release-image-signing-certificate" && azurerm_role_assignment.release_signing_certificate.principal_id == azurerm_user_assigned_identity.release.principal_id
    error_message = "The release identity must hold Key Vault Certificate User scoped to the signing certificate only."
  }

  assert {
    condition     = azurerm_role_assignment.release_container_app.role_definition_name == "Container Apps Contributor" && azurerm_role_assignment.release_container_app.scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.App/containerApps/ca-airline-demo-test" && azurerm_role_assignment.release_container_app.principal_id == azurerm_user_assigned_identity.release.principal_id
    error_message = "The release identity must hold Container Apps Contributor scoped to the application only."
  }

  assert {
    condition     = length(regexall("resource \"azurerm_role_assignment\"", file("${path.module}/release-identity.tf"))) == 5 && length(regexall("role_definition_name\\s*=", file("${path.module}/release-identity.tf"))) == 5 && length(regexall("scope\\s*=", file("${path.module}/release-identity.tf"))) == 5
    error_message = "release-identity.tf must declare exactly the five expected role assignments; an extra or broader assignment fails."
  }

  assert {
    condition     = length(regexall("(?m)scope\\s*=\\s*(azurerm_container_registry[.]acr[.]id|azurerm_container_app[.]app[.]id|azurerm_key_vault_key[.]signing[.]resource_versionless_id|azurerm_key_vault_certificate[.]signing[.]resource_manager_id)\\s*$", file("${path.module}/release-identity.tf"))) == 5
    error_message = "Every workflow role must be scoped to the ACR, the application, the signing key, or the signing certificate; never to a subscription, resource group, or vault."
  }

  assert {
    condition     = length(regexall("user_assigned_identity[.](build|release)[.]principal_id", join("\n", [for f in fileset(path.module, "**/*.tf") : file("${path.module}/${f}")]))) == 5
    error_message = "Only the five expected role assignments may reference the build or release identity; no other configuration may grant them roles."
  }

  assert {
    condition     = length(regexall("user_assigned_identity[.](?:build|release)[.]", join("\n", [for f in fileset(path.module, "**/*.tf") : file("${path.module}/${f}")]))) == 9 && length(regexall("user_assigned_identity[.](?:build|release)[.]", file("${path.module}/release-identity.tf"))) == 9
    error_message = "The build and release identities may be referenced only by the nine known references in release-identity.tf (five role principals, two federated credentials, two outputs). Any extra reference, including one through a local, fails."
  }

  assert {
    condition     = length(flatten([for f in fileset(path.module, "**/*.tf") : regexall("(?m)^resource\\s+\"azurerm_role_assignment\"", file("${path.module}/${f}"))])) == 12
    error_message = "The configuration must declare exactly twelve role assignments (seven in other files, five in release-identity.tf). Any new grant fails, including one whose principal or scope comes through a var or local, until this count is deliberately updated."
  }

  assert {
    condition     = !can(regex("client_secret|storage_key|access_key|access_policy|azuread_application_password|admin_password", file("${path.module}/release-identity.tf")))
    error_message = "The workflow identities must not be given a client secret, storage key, or Key Vault access policy."
  }
}

run "release_outputs_contain_no_secrets" {
  command = apply

  override_resource {
    target = azurerm_user_assigned_identity.build
    values = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-airline-build"
      principal_id = "66666666-6666-6666-6666-666666666666"
      client_id    = "88888888-8888-8888-8888-888888888888"
    }
  }

  override_resource {
    target = azurerm_user_assigned_identity.release
    values = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-airline-release"
      principal_id = "77777777-7777-7777-7777-777777777777"
      client_id    = "99999999-9999-9999-9999-999999999999"
    }
  }

  assert {
    condition     = !can(regex("(?i)secret|password|passwd|accountkey|connectionstring|private[ _-]?key|BEGIN [A-Z ]*KEY|sharedaccesssignature|sig=", output.build_identity_client_id)) && !can(regex("(?i)secret|password|passwd|accountkey|connectionstring|private[ _-]?key|BEGIN [A-Z ]*KEY|sharedaccesssignature|sig=", output.release_identity_client_id))
    error_message = "Release workflow outputs must not contain any credential, connection string, client secret, private key, or signing-key material."
  }

  assert {
    condition     = length(regexall("output \"", file("${path.module}/release-identity.tf"))) == 2 && length(regexall("(?m)^\\s*value\\s*=\\s*azurerm_user_assigned_identity[.](build|release)[.]client_id\\s*$", file("${path.module}/release-identity.tf"))) == 2
    error_message = "The release outputs must be exactly the two workflow client IDs; any other output value fails."
  }

  assert {
    condition     = length(flatten([for f in fileset(path.module, "*.tf") : regexall("(?m)^output\\s+\"", file("${path.module}/${f}"))])) == 4 && !can(regex("(?i)secret|password|passwd|accountkey|connectionstring|private[ _-]?key|BEGIN [A-Z ]*KEY|sharedaccesssignature|sig=", join("\n", [for o in [output.app_sql_grant_statements, output.model_endpoint, output.build_identity_client_id, output.release_identity_client_id] : jsonencode(o)])))
    error_message = "Every Terraform output in the configuration (all *.tf files) must be one of the four known outputs, and none may contain secret material."
  }
}

run "federated_identities_are_distinct_principals" {
  command = apply

  override_resource {
    target = azurerm_user_assigned_identity.build
    values = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-airline-build"
      principal_id = "66666666-6666-6666-6666-666666666666"
    }
  }

  override_resource {
    target = azurerm_user_assigned_identity.release
    values = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-airline-release"
      principal_id = "77777777-7777-7777-7777-777777777777"
    }
  }

  assert {
    condition     = azurerm_user_assigned_identity.build.name != azurerm_user_assigned_identity.release.name && azurerm_federated_identity_credential.build.name != azurerm_federated_identity_credential.release.name
    error_message = "Build and release must be separate identities so a build cannot release."
  }

  assert {
    condition     = var.terraform_principal_object_id != azurerm_user_assigned_identity.build.principal_id && var.terraform_principal_object_id != azurerm_user_assigned_identity.release.principal_id
    error_message = "The terraform_principal_object_id input must not resolve to the build or release workflow identity, or the existing Contributor grant would hand a workflow identity subscription-wide rights."
  }
}
