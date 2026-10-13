# Workload identities, diagnostics and security alert routing (M-07, M-08, M-14, M-15).
# Business audit stays in the SQL audit tables; diagnostics carry no business content.

resource "azurerm_user_assigned_identity" "app" {
  name                = "id-airline-app"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name
}

resource "azurerm_user_assigned_identity" "migration" {
  name                = "id-airline-migration"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name
}

resource "azurerm_storage_container" "evidence" {
  name                  = "evidence"
  storage_account_id    = azurerm_storage_account.blob.id
  container_access_type = "private"
}

# App identity: model invocation, evidence container, and telemetry publishing.
# SQL data-plane grants (no DDL, no audit UPDATE/DELETE) are not managed by ARM.
resource "azurerm_role_assignment" "app_openai_user" {
  scope                = azurerm_cognitive_account.model.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "app_evidence" {
  scope                = "${azurerm_storage_account.blob.id}/blobServices/default/containers/${azurerm_storage_container.evidence.name}"
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "app_telemetry" {
  scope                = azurerm_application_insights.app.id
  role_definition_name = "Monitoring Metrics Publisher"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
  principal_type       = "ServicePrincipal"
}

# Migration identity: management-plane SQL scope only.
resource "azurerm_role_assignment" "migration_sql" {
  scope                = azurerm_mssql_server.sql.id
  role_definition_name = "SQL DB Contributor"
  principal_id         = azurerm_user_assigned_identity.migration.principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_log_analytics_workspace" "telemetry" {
  name                          = "log-airline-telemetry"
  location                      = azurerm_resource_group.demo.location
  resource_group_name           = azurerm_resource_group.demo.name
  sku                           = "PerGB2018"
  retention_in_days             = 30
  internet_ingestion_enabled    = false
  internet_query_enabled        = false
  local_authentication_disabled = true
}

resource "azurerm_application_insights" "app" {
  name                          = "appi-airline-telemetry"
  location                      = azurerm_resource_group.demo.location
  resource_group_name           = azurerm_resource_group.demo.name
  application_type              = "web"
  workspace_id                  = azurerm_log_analytics_workspace.telemetry.id
  retention_in_days             = 30
  internet_ingestion_enabled    = false
  internet_query_enabled        = false
  local_authentication_disabled = true
}

resource "azurerm_role_assignment" "telemetry_reader" {
  scope                = azurerm_log_analytics_workspace.telemetry.id
  role_definition_name = "Log Analytics Reader"
  principal_id         = var.telemetry_reader_group_id
  principal_type       = "Group"
}

resource "azurerm_monitor_action_group" "security_owner" {
  name                = "ag-security-owner"
  short_name          = "secowner"
  resource_group_name = azurerm_resource_group.demo.name

  email_receiver {
    name                    = "response-owner"
    email_address           = var.security_response_owner.email
    use_common_alert_schema = true
  }
}

locals {
  security_alerts = {
    auth_scope_failures          = { event = "security.auth_scope_failure", description = "Repeated authentication or scope failures" }
    integrity_signature_failures = { event = "security.integrity_signature_failure", description = "Integrity or signature verification failures" }
    budget_exhausted             = { event = "security.budget_exhausted", description = "Exhausted investigation or model budget" }
  }
}

resource "azurerm_monitor_scheduled_query_rules_alert_v2" "security" {
  for_each             = local.security_alerts
  name                 = "alert-security-${replace(each.key, "_", "-")}"
  resource_group_name  = azurerm_resource_group.demo.name
  location             = azurerm_resource_group.demo.location
  description          = each.value.description
  scopes               = [azurerm_log_analytics_workspace.telemetry.id]
  severity             = 1
  evaluation_frequency = "PT${var.security_alert_settings.evaluation_frequency_minutes}M"
  window_duration      = "PT${var.security_alert_settings.window_minutes}M"

  criteria {
    query                   = "AppTraces | where TimeGenerated > ago(${var.security_alert_settings.window_minutes}m) | where tostring(Properties[\"event.name\"]) == \"${each.value.event}\" | summarize Count = count()"
    time_aggregation_method = "Count"
    operator                = "GreaterThanOrEqual"
    threshold               = var.security_alert_settings.failure_threshold

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [azurerm_monitor_action_group.security_owner.id]
  }
}
