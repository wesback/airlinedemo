# Private Azure Container Apps hosting (FR-011, NFR-001, M-13, M-14, M-18).
# The image is pulled by digest from a private registry. The app reaches SQL, Blob and the model
# only through its user-assigned identity and private endpoints.

resource "azurerm_container_registry" "acr" {
  name                          = var.acr_name
  resource_group_name           = azurerm_resource_group.demo.name
  location                      = azurerm_resource_group.demo.location
  sku                           = "Premium"
  admin_enabled                 = false
  public_network_access_enabled = false
}

resource "azurerm_role_assignment" "app_acr_pull" {
  scope                = azurerm_container_registry.acr.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "app_key_vault_secrets" {
  scope                = azurerm_key_vault.kv.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_container_app_environment" "demo" {
  name                           = var.container_apps_environment_name
  location                       = azurerm_resource_group.demo.location
  resource_group_name            = azurerm_resource_group.demo.name
  infrastructure_subnet_id       = azurerm_subnet.app.id
  internal_load_balancer_enabled = true
  log_analytics_workspace_id     = azurerm_log_analytics_workspace.telemetry.id
}

resource "azurerm_container_app" "app" {
  name                         = var.container_app_name
  container_app_environment_id = azurerm_container_app_environment.demo.id
  resource_group_name          = azurerm_resource_group.demo.name
  revision_mode                = "Single"

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app.id]
  }

  registry {
    server   = azurerm_container_registry.acr.login_server
    identity = azurerm_user_assigned_identity.app.id
  }

  # The passcode is held only as a Container Apps secret that points at Key Vault.
  secret {
    name                = "demo-passcode"
    key_vault_secret_id = var.demo_passcode_secret_uri
    identity            = azurerm_user_assigned_identity.app.id
  }

  ingress {
    external_enabled           = true
    target_port                = 8080
    transport                  = "auto"
    allow_insecure_connections = false

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }

    dynamic "ip_security_restriction" {
      for_each = local.presenter_reviewer_rules
      content {
        name             = "allow-presenter-reviewer-${ip_security_restriction.key}"
        action           = "Allow"
        ip_address_range = ip_security_restriction.value
      }
    }

    # An empty presenter list denies all inbound app traffic.
    dynamic "ip_security_restriction" {
      for_each = length(var.presenter_reviewer_cidrs) == 0 ? { deny = "0.0.0.0/0" } : {}
      content {
        name             = "deny-all-inbound"
        action           = "Deny"
        ip_address_range = ip_security_restriction.value
      }
    }
  }

  template {
    min_replicas = 1
    max_replicas = 2

    container {
      name   = "airline-api"
      image  = "${azurerm_container_registry.acr.login_server}/airline-api@${var.image_digest}"
      cpu    = 0.5
      memory = "1Gi"

      env {
        name  = "ConnectionStrings__CaseState"
        value = "Server=tcp:${var.sql_server_name}.database.windows.net,1433;Initial Catalog=${var.sql_database_name};Authentication=Active Directory Managed Identity;User Id=${azurerm_user_assigned_identity.app.client_id};Encrypt=True;TrustServerCertificate=False;"
      }

      env {
        name        = "Demo__Passcode"
        secret_name = "demo-passcode"
      }
    }
  }

  depends_on = [
    azurerm_role_assignment.app_acr_pull,
    azurerm_role_assignment.app_key_vault_secrets,
    azurerm_private_endpoint.demo,
  ]
}
