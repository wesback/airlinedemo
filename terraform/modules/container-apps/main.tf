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
