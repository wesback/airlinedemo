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
mock_provider "azurerm" {}

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

run "evidence_versions_and_soft_delete_are_bounded" {
  command = plan

  assert {
    condition     = azurerm_storage_account.blob.blob_properties[0].versioning_enabled == true
    error_message = "Blob versioning must be enabled on the evidence storage account."
  }

  assert {
    condition     = azurerm_storage_account.blob.blob_properties[0].delete_retention_policy[0].days == 14
    error_message = "Blob soft delete must retain deleted data for exactly 14 days."
  }

  assert {
    condition     = azurerm_storage_account.blob.blob_properties[0].container_delete_retention_policy[0].days == 14
    error_message = "Container soft delete must retain deleted containers for exactly 14 days."
  }

  assert {
    condition     = azurerm_storage_container.evidence.container_access_type == "private"
    error_message = "The evidence container must stay private."
  }
}

run "sql_restore_and_audit_lifetime_are_configured" {
  command = plan

  assert {
    condition     = azurerm_mssql_database.airline.short_term_retention_policy[0].retention_days == 35
    error_message = "SQL point-in-time restore retention must be exactly 35 days."
  }

  assert {
    condition     = contains(output.app_sql_grant_statements, "GRANT SELECT ON OBJECT::audit.case_events TO [id-airline-app];") && contains(output.app_sql_grant_statements, "GRANT INSERT ON OBJECT::audit.case_events TO [id-airline-app];")
    error_message = "The app identity must be granted exactly SELECT and INSERT on audit.case_events."
  }

  assert {
    condition     = contains(output.app_sql_grant_statements, "DENY UPDATE ON OBJECT::audit.case_events TO [id-airline-app];") && contains(output.app_sql_grant_statements, "DENY DELETE ON OBJECT::audit.case_events TO [id-airline-app];") && contains(output.app_sql_grant_statements, "DENY ALTER ON OBJECT::audit.case_events TO [id-airline-app];") && !contains(output.app_sql_grant_statements, "DENY CONTROL ON OBJECT::audit.case_events TO [id-airline-app];")
    error_message = "Deny audit UPDATE, DELETE and ALTER without a CONTROL denial that also blocks append/read."
  }

  assert {
    condition     = length([for s in output.app_sql_grant_statements : s if startswith(s, "GRANT") && strcontains(s, "OBJECT::audit.case_events") && (strcontains(s, "UPDATE") || strcontains(s, "DELETE") || strcontains(s, "ALTER") || strcontains(s, "CONTROL"))]) == 0
    error_message = "No audit mutation grant may be present for the app identity."
  }
}

run "audit_grants_remain_append_only" {
  command = plan

  assert {
    condition     = contains(output.migration_sql_grant_statements, "GRANT ALTER ON SCHEMA::dbo TO [id-airline-migration];") && contains(output.migration_sql_grant_statements, "GRANT ALTER ON SCHEMA::audit TO [id-airline-migration];")
    error_message = "Only the migration identity may receive SQL schema-change permission, on dbo and audit."
  }

  assert {
    condition = output.migration_sql_grant_statements == [
      "IF DATABASE_PRINCIPAL_ID('id-airline-migration') IS NULL CREATE USER [id-airline-migration] FROM EXTERNAL PROVIDER;",
      "GRANT ALTER ON SCHEMA::dbo TO [id-airline-migration];",
      "GRANT ALTER ON SCHEMA::audit TO [id-airline-migration];",
      "GRANT CREATE TABLE TO [id-airline-migration];",
      "GRANT SELECT, INSERT ON OBJECT::dbo.__EFMigrationsHistory TO [id-airline-migration];",
    ]
    error_message = "The migration grant statements must be exactly the expected set: no schema-change grant may target any other identity."
  }

  assert {
    condition     = length([for s in output.migration_sql_grant_statements : s if(strcontains(s, "ALTER") || strcontains(s, "CONTROL")) && !strcontains(s, "[id-airline-migration]")]) == 0
    error_message = "Every schema-change grant in the migration statements must target the migration identity."
  }

  assert {
    condition     = length([for s in output.app_sql_grant_statements : s if startswith(s, "GRANT") && (strcontains(s, "ALTER") || strcontains(s, "CONTROL"))]) == 0
    error_message = "The app identity must receive no schema-change permission."
  }

  assert {
    condition     = length([for s in output.migration_sql_grant_statements : s if strcontains(s, "app")]) == 0
    error_message = "The migration grants must not target the app identity."
  }

  assert {
    condition     = !contains(output.app_sql_grant_statements, "GRANT UPDATE ON OBJECT::audit.case_events TO [id-airline-app];") && !contains(output.app_sql_grant_statements, "GRANT DELETE ON OBJECT::audit.case_events TO [id-airline-app];")
    error_message = "The app identity must never receive UPDATE or DELETE on audit.case_events."
  }
}
