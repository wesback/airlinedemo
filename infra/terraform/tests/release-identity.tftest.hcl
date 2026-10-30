mock_provider "azuread" {
  mock_data "azuread_client_config" {
    defaults = {
      object_id = "00000000-0000-0000-0000-0000000000d0"
    }
  }
  mock_resource "azuread_application" {
    defaults = {
      id        = "/applications/00000000-0000-0000-0000-0000000000a1"
      client_id = "00000000-0000-0000-0000-0000000000c1"
    }
  }
  mock_resource "azuread_service_principal" {
    defaults = {
      object_id = "00000000-0000-0000-0000-0000000000b1"
    }
  }
}
mock_provider "azurerm" {
  mock_resource "azurerm_user_assigned_identity" {
    defaults = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-mock"
      principal_id = "aBcDeF01-2345-6789-abcd-EF0123456789"
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

  mock_resource "azurerm_key_vault_certificate" {
    defaults = {
      id                              = "https://kv-demo-test.vault.azure.net/certificates/release-image-signing-certificate/0000"
      resource_manager_id             = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test/certificates/release-image-signing-certificate/versions/0000"
      resource_manager_versionless_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test/certificates/release-image-signing-certificate"
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

  mock_resource "azurerm_monitor_private_link_scope" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Insights/privateLinkScopes/ampls-airline-telemetry"
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
  gateway_endpoint                = "https://apim-demo-test.private.invalid/"
  model_deployment_name           = "gpt-demo-test"
  model_name                      = "gpt-demo-model"
  model_version                   = "0000-00-00"
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
      principal_id = "aBcDeF01-2345-6789-abcd-EF0123456789"
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
    condition     = azurerm_private_endpoint.demo["acr"].private_service_connection[0].private_connection_resource_id == azurerm_container_registry.acr.id
    error_message = "The ACR private endpoint must target the ACR resource itself, not another resource."
  }

  assert {
    condition     = azurerm_private_endpoint.demo["acr"].private_dns_zone_group[0].private_dns_zone_ids[0] == azurerm_private_dns_zone.demo["acr"].id && azurerm_private_dns_zone.demo["acr"].name == "privatelink.azurecr.io"
    error_message = "The ACR private endpoint must be registered in the privatelink.azurecr.io zone."
  }

  assert {
    condition     = azurerm_role_assignment.app_acr_pull.role_definition_name == "AcrPull" && azurerm_role_assignment.app_acr_pull.scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ContainerRegistry/registries/acrdemotest" && azurerm_role_assignment.app_acr_pull.principal_id == "aBcDeF01-2345-6789-abcd-EF0123456789"
    error_message = "The app identity's only ACR grant must be AcrPull scoped to the ACR."
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
    condition     = azurerm_federated_identity_credential.build.subject == "repo:wesback@14137481/airlinedemo@1364258050:ref:refs/heads/main" && length(azurerm_federated_identity_credential.build.audience) == 1 && contains(azurerm_federated_identity_credential.build.audience, "api://AzureADTokenExchange") && azurerm_federated_identity_credential.build.issuer == "https://token.actions.githubusercontent.com"
    error_message = "Build federation must trust only owner 14137481 and repository 1364258050 on refs/heads/main with audience api://AzureADTokenExchange."
  }

  assert {
    condition     = azurerm_federated_identity_credential.release.user_assigned_identity_id == azurerm_user_assigned_identity.release.id
    error_message = "The release federated credential must be attached to the release identity."
  }

  assert {
    condition     = azurerm_federated_identity_credential.release.subject == "repo:wesback@14137481/airlinedemo@1364258050:environment:production-release" && length(azurerm_federated_identity_credential.release.audience) == 1 && contains(azurerm_federated_identity_credential.release.audience, "api://AzureADTokenExchange") && azurerm_federated_identity_credential.release.issuer == "https://token.actions.githubusercontent.com"
    error_message = "Release federation must trust only owner 14137481 and repository 1364258050 in environment production-release with audience api://AzureADTokenExchange."
  }

}

run "signing_rights_are_resource_scoped" {
  command = apply

  override_resource {
    target = azurerm_key_vault_certificate.signing
    values = {
      resource_manager_id             = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test/certificates/release-image-signing-certificate/versions/0000"
      resource_manager_versionless_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test/certificates/release-image-signing-certificate"
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
    target = azurerm_container_app.app[0]
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
    condition     = azurerm_role_assignment.release_signing_key.role_definition_name == "Key Vault Crypto User" && azurerm_role_assignment.release_signing_key.scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test/keys/release-image-signing-certificate" && azurerm_role_assignment.release_signing_key.principal_id == azurerm_user_assigned_identity.release.principal_id
    error_message = "The release identity must hold Key Vault Crypto User scoped to the key named like the signing certificate (vault id + /keys/ + certificate name) only."
  }

  assert {
    condition     = azurerm_role_assignment.release_signing_certificate.role_definition_name == "Key Vault Certificate User" && azurerm_role_assignment.release_signing_certificate.scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test/certificates/release-image-signing-certificate" && azurerm_role_assignment.release_signing_certificate.principal_id == azurerm_user_assigned_identity.release.principal_id
    error_message = "The release identity must hold Key Vault Certificate User on the versionless signing certificate, not a certificate version or the vault."
  }

  assert {
    condition     = azurerm_key_vault_certificate.signing.certificate_policy[0].x509_certificate_properties[0].subject == "CN=airline-demo-release-signing, O=Airline Demo, ST=Brussels, C=BE"
    error_message = "The signing certificate subject must carry the C, ST and O attributes a Notation trust policy requires."
  }

  assert {
    condition     = azurerm_role_assignment.release_container_app[0].role_definition_name == "Container Apps Contributor" && azurerm_role_assignment.release_container_app[0].scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.App/containerApps/ca-airline-demo-test" && azurerm_role_assignment.release_container_app[0].principal_id == azurerm_user_assigned_identity.release.principal_id
    error_message = "The release identity must hold Container Apps Contributor scoped to the application only."
  }

  assert {
    condition     = azurerm_role_assignment.terraform_contributor.role_definition_name == "Contributor" && azurerm_role_assignment.terraform_contributor.scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test" && azurerm_role_assignment.terraform_contributor.principal_id == "33333333-3333-3333-3333-333333333333"
    error_message = "The existing Contributor grant must stay on the resource group for the Terraform principal only."
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
    condition     = output.model_endpoint == "https://apim-demo-test.private.invalid/"
    error_message = "model_endpoint must equal the exact private model endpoint host for the mocked subdomain."
  }

  assert {
    condition = output.app_sql_grant_statements == [
      "IF DATABASE_PRINCIPAL_ID('id-airline-app') IS NULL CREATE USER [id-airline-app] FROM EXTERNAL PROVIDER;",
      "GRANT SELECT ON OBJECT::dbo.cases TO [id-airline-app];",
      "GRANT INSERT ON OBJECT::dbo.cases TO [id-airline-app];",
      "GRANT UPDATE ON OBJECT::dbo.cases TO [id-airline-app];",
      "GRANT SELECT ON OBJECT::dbo.case_documents TO [id-airline-app];",
      "GRANT INSERT ON OBJECT::dbo.case_documents TO [id-airline-app];",
      "GRANT UPDATE ON OBJECT::dbo.case_documents TO [id-airline-app];",
      "GRANT SELECT ON OBJECT::dbo.case_decisions TO [id-airline-app];",
      "GRANT INSERT ON OBJECT::dbo.case_decisions TO [id-airline-app];",
      "GRANT UPDATE ON OBJECT::dbo.case_decisions TO [id-airline-app];",
      "GRANT SELECT ON OBJECT::dbo.case_work_items TO [id-airline-app];",
      "GRANT INSERT ON OBJECT::dbo.case_work_items TO [id-airline-app];",
      "GRANT UPDATE ON OBJECT::dbo.case_work_items TO [id-airline-app];",
      "GRANT SELECT ON OBJECT::dbo.case_pack_request_windows TO [id-airline-app];",
      "GRANT INSERT ON OBJECT::dbo.case_pack_request_windows TO [id-airline-app];",
      "GRANT UPDATE ON OBJECT::dbo.case_pack_request_windows TO [id-airline-app];",
      "GRANT SELECT ON OBJECT::dbo.investigation_traces TO [id-airline-app];",
      "GRANT INSERT ON OBJECT::dbo.investigation_traces TO [id-airline-app];",
      "GRANT SELECT ON OBJECT::dbo.investigation_trace_steps TO [id-airline-app];",
      "GRANT INSERT ON OBJECT::dbo.investigation_trace_steps TO [id-airline-app];",
      "GRANT SELECT ON OBJECT::dbo.case_assessments TO [id-airline-app];",
      "GRANT INSERT ON OBJECT::dbo.case_assessments TO [id-airline-app];",
      "GRANT SELECT ON OBJECT::dbo.case_request_receipts TO [id-airline-app];",
      "GRANT INSERT ON OBJECT::dbo.case_request_receipts TO [id-airline-app];",
      "GRANT SELECT ON OBJECT::dbo.case_journey_events TO [id-airline-app];",
      "GRANT INSERT ON OBJECT::dbo.case_journey_events TO [id-airline-app];",
      "GRANT SELECT ON OBJECT::dbo.case_pack_reset_receipts TO [id-airline-app];",
      "GRANT INSERT ON OBJECT::dbo.case_pack_reset_receipts TO [id-airline-app];",
      "GRANT SELECT ON OBJECT::dbo.source_read_windows TO [id-airline-app];",
      "GRANT INSERT ON OBJECT::dbo.source_read_windows TO [id-airline-app];",
      "GRANT UPDATE ON OBJECT::dbo.source_read_windows TO [id-airline-app];",
      "REVOKE CONTROL ON OBJECT::audit.case_events FROM [id-airline-app];",
      "GRANT SELECT ON OBJECT::audit.case_events TO [id-airline-app];",
      "GRANT INSERT ON OBJECT::audit.case_events TO [id-airline-app];",
      "DENY UPDATE ON OBJECT::audit.case_events TO [id-airline-app];",
      "DENY DELETE ON OBJECT::audit.case_events TO [id-airline-app];",
      "DENY ALTER ON OBJECT::audit.case_events TO [id-airline-app];",
      "DENY ALTER ON SCHEMA::dbo TO [id-airline-app];",
    ]
    error_message = "app_sql_grant_statements must equal the exact ordered app grant statements."
  }

  assert {
    condition = output.migration_sql_grant_statements == [
      "IF DATABASE_PRINCIPAL_ID('id-airline-migration') IS NULL CREATE USER [id-airline-migration] FROM EXTERNAL PROVIDER;",
      "GRANT ALTER ON SCHEMA::dbo TO [id-airline-migration];",
      "GRANT ALTER ON SCHEMA::audit TO [id-airline-migration];",
      "GRANT CREATE TABLE TO [id-airline-migration];",
      "GRANT SELECT, INSERT ON OBJECT::dbo.__EFMigrationsHistory TO [id-airline-migration];",
    ]
    error_message = "migration_sql_grant_statements must equal the exact ordered migration grant statements."
  }

  assert {
    condition     = !can(regex("(?i)secret|password|passwd|accountkey|connectionstring|private[ _-]?key|BEGIN [A-Z ]*KEY|sharedaccesssignature|sig=", join("\n", [for o in [output.app_sql_grant_statements, output.migration_sql_grant_statements, output.model_endpoint] : jsonencode(o)])))
    error_message = "No Terraform output may contain secret material in its evaluated value."
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
    condition     = lower(var.terraform_principal_object_id) != lower(azurerm_user_assigned_identity.build.principal_id) && lower(var.terraform_principal_object_id) != lower(azurerm_user_assigned_identity.release.principal_id)
    error_message = "The terraform_principal_object_id input must not resolve to the build or release workflow identity, or the existing Contributor grant would hand a workflow identity subscription-wide rights."
  }
}

run "contributor_precondition_rejects_case_only_workflow_match" {
  command = plan

  # The mocked build and release principal IDs are aBcDeF01-2345-6789-abcd-EF0123456789.
  # This input is the same GUID with different casing only. The case-insensitive
  # precondition must reject it; a case-sensitive comparison would not fail here.
  variables {
    terraform_principal_object_id = "ABCDEF01-2345-6789-ABCD-EF0123456789"
  }

  expect_failures = [
    azurerm_role_assignment.terraform_contributor,
  ]
}
