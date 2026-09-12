resource "azurerm_container_registry" "application" {
  name                = "acr${replace(lower(var.deployment_name), "-", "")}"
  resource_group_name = var.resource_group_name
  location            = var.region
  sku                 = "Basic"
  admin_enabled       = false
  tags                = var.tags
}

resource "azurerm_container_app_environment" "this" {
  name                       = "cae-${var.deployment_name}"
  resource_group_name        = var.resource_group_name
  location                   = var.region
  log_analytics_workspace_id = var.log_analytics_workspace_id
  tags                       = var.tags
}

resource "azurerm_container_app" "this" {
  name                         = "ca-${var.deployment_name}"
  container_app_environment_id = azurerm_container_app_environment.this.id
  resource_group_name          = var.resource_group_name
  revision_mode                = "Single"

  identity {
    type = "SystemAssigned"
  }

  ingress {
    external_enabled = true
    target_port      = var.container_port
    transport        = "auto"

    traffic_weight {
      percentage      = 100
      latest_revision = true
    }

    dynamic "ip_security_restriction" {
      for_each = var.allowed_source_ranges

      content {
        name             = format("approved-demo-%03d", ip_security_restriction.key + 1)
        action           = "Allow"
        ip_address_range = ip_security_restriction.value
        description      = "Approved disposable synthetic-data demo source"
      }
    }
  }

  template {
    min_replicas = var.min_replicas
    max_replicas = var.max_replicas

    container {
      name   = "airlinedemo"
      image  = var.container_image
      cpu    = 0.5
      memory = "1Gi"
    }
  }

  tags = var.tags
}

resource "azurerm_user_assigned_identity" "migration" {
  name                = "id-${var.deployment_name}-migration"
  resource_group_name = var.resource_group_name
  location            = var.region
  tags                = merge(var.tags, { purpose = "versioned-sql-migrations" })
}

resource "azurerm_role_assignment" "runtime_evidence_reader" {
  scope                = var.evidence_storage_account_id
  role_definition_name = "Storage Blob Data Reader"
  principal_id         = azurerm_container_app.this.identity[0].principal_id
}

resource "azurerm_role_assignment" "runtime_document_intelligence_user" {
  scope                = var.document_intelligence_id
  role_definition_name = "Cognitive Services User"
  principal_id         = azurerm_container_app.this.identity[0].principal_id
}

resource "azurerm_role_assignment" "runtime_azure_openai_user" {
  scope                = var.azure_openai_id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_container_app.this.identity[0].principal_id
}

check "runtime_and_migration_principal_ids_are_distinct" {
  assert {
    condition     = azurerm_container_app.this.identity[0].principal_id != azurerm_user_assigned_identity.migration.principal_id
    error_message = "The Container Apps runtime and migration identities must have distinct principal IDs."
  }
}
