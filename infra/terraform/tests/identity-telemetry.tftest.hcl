mock_provider "azurerm" {
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

  mock_resource "azurerm_user_assigned_identity" {
    defaults = {
      principal_id = "55555555-5555-5555-5555-555555555555"
      client_id    = "77777777-7777-7777-7777-777777777777"
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-airline-demo/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-mock"
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

  mock_resource "azurerm_key_vault" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test"
    }
  }
}

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
  telemetry_reader_group_id     = "44444444-4444-4444-4444-444444444444"
  security_response_owner       = { name = "Response Owner", email = "owner@example.invalid" }
  security_alert_settings       = { enabled = true, failure_threshold = 5, window_minutes = 15, evaluation_frequency_minutes = 5 }
  terraform_state_backend = {
    storage_account_name = "stdemotfstate"
    container_name       = "tfstate"
    key                  = "demo.tfstate"
    use_azuread_auth     = true
    locking_enabled      = true
  }
}

run "M-14_app_identity_grants_are_exact" {
  command = apply

  assert {
    condition     = azurerm_role_assignment.app_openai_user.role_definition_name == "Cognitive Services OpenAI User"
    error_message = "App identity must hold only the Azure OpenAI User role on the model account."
  }

  assert {
    condition     = azurerm_role_assignment.app_evidence.role_definition_name == "Storage Blob Data Contributor" && azurerm_storage_container.evidence.name == "evidence"
    error_message = "App identity storage grant must be limited to the evidence container."
  }

  assert {
    condition     = length([for s in output.app_sql_grant_statements : s if strcontains(s, "CREATE USER [id-airline-app] FROM EXTERNAL PROVIDER")]) == 1
    error_message = "The app database principal must be created from the Entra identity before grants run."
  }

  assert {
    condition     = azurerm_role_assignment.app_telemetry.role_definition_name == "Monitoring Metrics Publisher"
    error_message = "App identity telemetry grant must be publish-only."
  }

  assert {
    condition     = length([for s in output.app_sql_grant_statements : s if startswith(s, "GRANT") && strcontains(s, "OBJECT::audit.case_events") && !(strcontains(s, "SELECT") || strcontains(s, "INSERT"))]) == 0 && length([for s in output.app_sql_grant_statements : s if startswith(s, "GRANT") && strcontains(s, "OBJECT::audit.case_events")]) == 2
    error_message = "The audit table audit.case_events may only be granted SELECT and INSERT to the app identity, never the whole schema."
  }

  assert {
    condition     = length([for s in output.app_sql_grant_statements : s if startswith(s, "GRANT") && (strcontains(s, "ALTER") || strcontains(s, "CONTROL"))]) == 0
    error_message = "App identity must never be granted ALTER or CONTROL."
  }

  assert {
    condition     = length([for s in output.app_sql_grant_statements : s if startswith(s, "DENY") && strcontains(s, "OBJECT::audit.case_events")]) == 4
    error_message = "Audit UPDATE, DELETE, ALTER and CONTROL must stay explicitly denied to the app identity."
  }


  assert {
    condition     = azurerm_storage_container.evidence.name == "evidence" && azurerm_storage_container.evidence.container_access_type == "private"
    error_message = "Evidence grant must stay scoped to the private evidence container."
  }

  assert {
    condition     = azurerm_storage_container.evidence.name == "evidence"
    error_message = "Evidence container name must stay evidence."
  }


  assert {
    condition     = !contains(["Owner", "Contributor", "User Access Administrator", "SQL DB Contributor", "Storage Blob Data Owner"], azurerm_role_assignment.app_evidence.role_definition_name) && !contains(["Owner", "Contributor", "User Access Administrator"], azurerm_role_assignment.app_openai_user.role_definition_name) && !contains(["Owner", "Contributor", "User Access Administrator"], azurerm_role_assignment.app_telemetry.role_definition_name)
    error_message = "App identity must not hold broad management or ownership roles."
  }
}

run "M-14_evidence_scope_is_exact_container" {
  command = apply

  assert {
    condition     = azurerm_role_assignment.app_evidence.scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stdemotest/blobServices/default/containers/evidence"
    error_message = "Evidence role assignment scope must equal the evidence container resource id exactly, not the account or a parent scope."
  }
}

run "M-14_app_role_scopes_are_exact_literals" {
  command = apply

  assert {
    condition     = azurerm_role_assignment.app_openai_user.scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.CognitiveServices/accounts/aoai-demo-test"
    error_message = "Azure OpenAI User must be scoped to exactly the model account, not a parent scope."
  }

  assert {
    condition     = azurerm_role_assignment.app_telemetry.scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Insights/components/appi-airline-telemetry"
    error_message = "Monitoring Metrics Publisher must be scoped to exactly the Application Insights component."
  }

  assert {
    condition     = length(setintersection([azurerm_role_assignment.app_openai_user.scope, azurerm_role_assignment.app_evidence.scope, azurerm_role_assignment.app_telemetry.scope], ["/subscriptions/00000000-0000-0000-0000-000000000000", "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test", "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Storage/storageAccounts/stdemotest"])) == 0
    error_message = "No app identity role assignment may sit at subscription, resource group or storage account scope."
  }

  assert {
    condition     = length([for s in output.app_sql_grant_statements : s if startswith(s, "GRANT") && (strcontains(s, "DELETE") || strcontains(s, "EXECUTE"))]) == 0
    error_message = "App SQL grants must not include DELETE or EXECUTE."
  }

  assert {
    condition     = length([for s in output.app_sql_grant_statements : s if startswith(s, "GRANT") && strcontains(s, "SCHEMA::dbo")]) == 0 && length([for s in output.app_sql_grant_statements : s if startswith(s, "GRANT") && strcontains(s, "OBJECT::dbo.")]) == 9 && contains(output.app_sql_grant_statements, "GRANT SELECT ON OBJECT::dbo.cases TO [id-airline-app];") && contains(output.app_sql_grant_statements, "GRANT UPDATE ON OBJECT::dbo.case_decisions TO [id-airline-app];")
    error_message = "App SQL grants on dbo must be exactly SELECT, INSERT and UPDATE on the three case tables, never the whole schema."
  }
}

run "M-14_grants_bind_to_app_identity_as_service_principal" {
  command = apply

  assert {
    condition     = azurerm_role_assignment.app_openai_user.principal_type == "ServicePrincipal" && azurerm_role_assignment.app_evidence.principal_type == "ServicePrincipal" && azurerm_role_assignment.app_telemetry.principal_type == "ServicePrincipal"
    error_message = "Every app grant must be a service-principal grant to the app workload identity."
  }

  assert {
    condition     = azurerm_role_assignment.migration_sql.principal_type == "ServicePrincipal" && azurerm_role_assignment.migration_sql.role_definition_name == "SQL DB Contributor"
    error_message = "Migration SQL grant must be a service-principal SQL management grant only."
  }

  assert {
    condition     = contains(output.app_sql_grant_statements, "GRANT SELECT ON OBJECT::audit.case_events TO [id-airline-app];") && !contains(output.app_sql_grant_statements, "GRANT UPDATE ON OBJECT::audit.case_events TO [id-airline-app];")
    error_message = "App SQL grants must be attached to the app identity name and never widen audit UPDATE."
  }

  assert {
    condition     = length([for s in output.app_sql_grant_statements : s if strcontains(s, "SCHEMA::audit")]) == 0
    error_message = "No app SQL grant or deny may target the whole audit schema; only audit.case_events."
  }
}

run "M-14_app_grants_bind_to_app_principal_not_migration" {
  command   = apply
  state_key = "m14-principal-binding"

  override_resource {
    target = azurerm_user_assigned_identity.app
    values = {
      principal_id = "55555555-5555-5555-5555-555555555555"
    }
  }

  override_resource {
    target = azurerm_user_assigned_identity.migration
    values = {
      principal_id = "66666666-6666-6666-6666-666666666666"
    }
  }

  assert {
    condition     = azurerm_user_assigned_identity.app.principal_id != azurerm_user_assigned_identity.migration.principal_id
    error_message = "App and migration identities must have distinct principals for the grant binding test to be meaningful."
  }

  assert {
    condition     = azurerm_role_assignment.app_openai_user.principal_id == azurerm_user_assigned_identity.app.principal_id && azurerm_role_assignment.app_evidence.principal_id == azurerm_user_assigned_identity.app.principal_id && azurerm_role_assignment.app_telemetry.principal_id == azurerm_user_assigned_identity.app.principal_id
    error_message = "Every app role assignment must be bound to the app workload identity principal."
  }

  assert {
    condition     = azurerm_role_assignment.migration_sql.principal_id == azurerm_user_assigned_identity.migration.principal_id
    error_message = "The migration identity grant must be bound to the migration identity principal."
  }
}

run "M-14_app_sql_statements_deny_ddl_and_audit_mutation" {
  command = plan

  assert {
    condition     = contains(output.app_sql_grant_statements, "DENY UPDATE ON OBJECT::audit.case_events TO [id-airline-app];") && contains(output.app_sql_grant_statements, "DENY DELETE ON OBJECT::audit.case_events TO [id-airline-app];") && contains(output.app_sql_grant_statements, "DENY ALTER ON SCHEMA::dbo TO [id-airline-app];")
    error_message = "App SQL statements must deny audit UPDATE/DELETE and DDL to the app identity."
  }

  assert {
    condition     = length([for s in output.app_sql_grant_statements : s if strcontains(s, "GRANT") && strcontains(s, "OBJECT::audit.case_events") && (strcontains(s, "UPDATE") || strcontains(s, "DELETE") || strcontains(s, "ALTER") || strcontains(s, "CONTROL"))]) == 0
    error_message = "No grant may widen audit tables to UPDATE, DELETE, ALTER or CONTROL."
  }

  assert {
    condition     = length([for s in output.app_sql_grant_statements : s if strcontains(s, "db_owner") || strcontains(s, "db_ddladmin") || strcontains(s, "CONTROL SERVER")]) == 0
    error_message = "App SQL statements must not grant privileged database roles."
  }
}

run "M-14_app_sql_contract_excludes_ddl_and_audit_mutation" {
  command = plan

  assert {
    condition     = alltrue([for p in local.app_sql_grants.audit : contains(["SELECT", "INSERT"], p)]) && length(local.app_sql_grants.audit) == 2
    error_message = "App identity may only SELECT and INSERT audit rows: no UPDATE, DELETE or DDL."
  }

  assert {
    condition     = !contains(local.app_sql_grants.audit, "UPDATE") && !contains(local.app_sql_grants.audit, "DELETE")
    error_message = "Audit tables must not grant UPDATE or DELETE to the app identity."
  }
}

run "M-14_migration_identity_has_only_sql_management_scope" {
  command = apply

  assert {
    condition     = azurerm_role_assignment.migration_sql.role_definition_name == "SQL DB Contributor" && azurerm_mssql_server.sql.name == "sql-demo-test"
    error_message = "Migration identity must be scoped to the SQL server management plane only."
  }

  assert {
    condition     = azurerm_role_assignment.migration_sql.scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Sql/servers/sql-demo-test" && azurerm_role_assignment.migration_sql.principal_type == "ServicePrincipal"
    error_message = "Migration SQL grant scope must equal the SQL server resource id exactly; a widened or different scope must fail."
  }
}

run "M-14_terraform_identity_scope_is_exact_resource_group" {
  command = apply

  assert {
    condition     = azurerm_role_assignment.terraform_contributor.scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test" && azurerm_role_assignment.terraform_contributor.role_definition_name == "Contributor"
    error_message = "Terraform identity must hold Contributor on exactly the demo resource group; subscription or other scopes must fail."
  }

  assert {
    condition     = azurerm_role_assignment.terraform_contributor.principal_id == "33333333-3333-3333-3333-333333333333"
    error_message = "Terraform Contributor grant must be bound to the terraform_principal_object_id variable."
  }
}

run "M-07_telemetry_retention_and_no_public_access" {
  command = plan

  assert {
    condition     = azurerm_log_analytics_workspace.telemetry.retention_in_days == 30 && azurerm_application_insights.app.retention_in_days == 30
    error_message = "Telemetry retention must be 30 days on the workspace and Application Insights."
  }

  assert {
    condition     = azurerm_log_analytics_workspace.telemetry.internet_ingestion_enabled == false && azurerm_log_analytics_workspace.telemetry.internet_query_enabled == false && azurerm_application_insights.app.internet_query_enabled == false
    error_message = "Telemetry must not accept or serve public traffic."
  }
}

run "M-07_telemetry_reader_is_read_only_group" {
  command = apply

  assert {
    condition     = azurerm_role_assignment.telemetry_reader.role_definition_name == "Log Analytics Reader" && azurerm_role_assignment.telemetry_reader.principal_type == "Group"
    error_message = "Telemetry reader must be a named group with read-only Log Analytics access."
  }

  assert {
    condition     = azurerm_role_assignment.telemetry_reader.scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.OperationalInsights/workspaces/log-airline-telemetry"
    error_message = "Telemetry reader scope must equal the telemetry workspace id exactly; a widened or different scope must fail."
  }

  assert {
    condition     = azurerm_role_assignment.telemetry_reader.principal_id == var.telemetry_reader_group_id
    error_message = "Telemetry reader must be bound to the configured reader group id; a different group must fail."
  }
}

run "M-15_security_alerts_cover_required_signals_and_route_to_owner" {
  command = apply

  assert {
    condition     = length(azurerm_monitor_scheduled_query_rules_alert_v2.security) == 3 && alltrue([for a in azurerm_monitor_scheduled_query_rules_alert_v2.security : a.severity == 1])
    error_message = "Auth/scope, integrity/signature and budget alerts must each exist at severity 1."
  }

  assert {
    condition     = azurerm_monitor_action_group.security_owner.email_receiver[0].email_address == "owner@example.invalid"
    error_message = "Security alerts must reach the named response owner."
  }

  assert {
    condition     = alltrue([for a in azurerm_monitor_scheduled_query_rules_alert_v2.security : !strcontains(a.criteria[0].query, "summarize") && a.criteria[0].time_aggregation_method == "Count" && a.criteria[0].threshold == 5])
    error_message = "Alert queries must return one row per event so the Count threshold detects repeats."
  }

  assert {
    condition     = strcontains(azurerm_monitor_scheduled_query_rules_alert_v2.security["auth_scope_failures"].criteria[0].query, "security.auth_scope_failure") && strcontains(azurerm_monitor_scheduled_query_rules_alert_v2.security["integrity_signature_failures"].criteria[0].query, "security.integrity_signature_failure") && strcontains(azurerm_monitor_scheduled_query_rules_alert_v2.security["budget_exhausted"].criteria[0].query, "security.budget_exhausted")
    error_message = "Each alert must watch its required security event name."
  }

  assert {
    condition     = alltrue([for a in azurerm_monitor_scheduled_query_rules_alert_v2.security : length(a.action) == 1 && length(a.action[0].action_groups) == 1])
    error_message = "Every security alert must route through exactly one action group, the named owner group."
  }

  assert {
    condition     = azurerm_monitor_action_group.security_owner.name == "ag-security-owner" && alltrue([for a in azurerm_monitor_scheduled_query_rules_alert_v2.security : a.action[0].action_groups[0] == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Insights/actionGroups/ag-security-owner"])
    error_message = "Each security alert must reference the single owner action group built from the owner variable."
  }

  assert {
    condition     = azurerm_monitor_action_group.security_owner.email_receiver[0].email_address == var.security_response_owner.email && azurerm_monitor_action_group.security_owner.email_receiver[0].name == "response-owner"
    error_message = "Security alerts must route to the action group whose receiver is the configured named response owner."
  }

  assert {
    condition     = sort(keys(azurerm_monitor_scheduled_query_rules_alert_v2.security)) == sort(["auth_scope_failures", "integrity_signature_failures", "budget_exhausted"])
    error_message = "Security alerts must cover auth/scope failures, integrity failures and exhausted budgets, and nothing else is required."
  }

}

run "M-15_rejects_missing_owner" {
  command = plan

  variables {
    security_response_owner = { name = "", email = "owner@example.invalid" }
  }

  expect_failures = [var.security_response_owner]
}

run "M-15_rejects_disabled_alerts" {
  command = plan

  variables {
    security_alert_settings = { enabled = false, failure_threshold = 5, window_minutes = 15, evaluation_frequency_minutes = 5 }
  }

  expect_failures = [var.security_alert_settings]
}

run "M-15_rejects_invalid_reader_group" {
  command = plan

  variables {
    telemetry_reader_group_id = "not-a-guid"
  }

  expect_failures = [var.telemetry_reader_group_id]
}
