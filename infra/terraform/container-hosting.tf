# Private hosting for the application image. Ingress and egress are enforced by the app subnet NSG in main.tf.
resource "azurerm_container_registry" "acr" {
  name                          = var.acr_name
  resource_group_name           = azurerm_resource_group.demo.name
  location                      = azurerm_resource_group.demo.location
  sku                           = "Premium"
  admin_enabled                 = false
  public_network_access_enabled = false
  network_rule_bypass_option    = "AzureServices"
}

resource "azurerm_role_assignment" "app_acr_pull" {
  scope                = azurerm_container_registry.acr.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

resource "azurerm_container_app_environment" "demo" {
  name                           = var.container_apps_environment_name
  resource_group_name            = azurerm_resource_group.demo.name
  location                       = azurerm_resource_group.demo.location
  infrastructure_subnet_id       = azurerm_subnet.app.id
  internal_load_balancer_enabled = false
}

resource "azurerm_container_app" "app" {
  name                         = var.container_app_name
  resource_group_name          = azurerm_resource_group.demo.name
  container_app_environment_id = azurerm_container_app_environment.demo.id
  revision_mode                = "Single"

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app.id]
  }

  registry {
    server   = azurerm_container_registry.acr.login_server
    identity = azurerm_user_assigned_identity.app.id
  }

  # The passcode is a Key Vault secret reference; its value never appears in Terraform or app environment values.
  secret {
    name                = "demo-passcode"
    key_vault_secret_id = var.demo_passcode_secret_uri
    identity            = azurerm_user_assigned_identity.app.id
  }

  ingress {
    external_enabled           = true
    target_port                = 8080
    transport                  = "http"
    allow_insecure_connections = false

    # Container Apps ingress is outside the app subnet NSG, so callers are limited here.
    # One Allow per configured CIDR; with an empty list a single Deny-all rule denies every caller.
    dynamic "ip_security_restriction" {
      for_each = local.presenter_reviewer_rules
      content {
        name             = "presenter-reviewer-${ip_security_restriction.key}"
        description      = "Allowed presenter or reviewer CIDR"
        action           = "Allow"
        ip_address_range = ip_security_restriction.value
      }
    }

    dynamic "ip_security_restriction" {
      for_each = length(var.presenter_reviewer_cidrs) == 0 ? toset(["0.0.0.0/0"]) : toset([])
      content {
        name             = "deny-all-empty-allowlist"
        description      = "No presenter or reviewer CIDRs configured"
        action           = "Deny"
        ip_address_range = ip_security_restriction.value
      }
    }

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "airline-api"
      image  = "${azurerm_container_registry.acr.login_server}/airline-api@${var.image_digest}"
      cpu    = 0.5
      memory = "1Gi"

      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.app.client_id
      }

      env {
        name  = "ConnectionStrings__CaseState"
        value = "Server=tcp:${var.sql_server_name}.database.windows.net,1433;Database=${var.sql_database_name};Authentication=Active Directory Managed Identity;User Id=${azurerm_user_assigned_identity.app.client_id};Encrypt=True;TrustServerCertificate=False;"
      }

      env {
        name        = "Demo__Passcode"
        secret_name = "demo-passcode"
      }
    }
  }

  depends_on = [azurerm_role_assignment.app_acr_pull]
}
