# Private hosting for the application image. Ingress and egress are enforced by the app subnet NSG in main.tf.
resource "azurerm_container_registry" "acr" {
  name                          = local.acr_name
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
  principal_type       = "ServicePrincipal"
}

# When the passcode lives in this deployment's vault, the app identity needs to read it.
# A passcode in another vault needs the same grant there, made by that vault's owner.
resource "azurerm_role_assignment" "app_passcode_secret" {
  count                = local.deploy_application && startswith(lower(coalesce(var.demo_passcode_secret_uri, "-")), "https://${lower(local.key_vault_name)}.vault.azure.net/") ? 1 : 0
  scope                = "${azurerm_key_vault.kv.id}/secrets/${split("/", coalesce(var.demo_passcode_secret_uri, "-/-/-/-/-"))[4]}"
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
  principal_type       = "ServicePrincipal"
}

# A workload-profiles environment: the delegated app subnet and its NSG only support this environment type.
resource "azurerm_container_app_environment" "demo" {
  name                           = local.container_apps_environment_name
  resource_group_name            = azurerm_resource_group.demo.name
  location                       = azurerm_resource_group.demo.location
  infrastructure_subnet_id       = azurerm_subnet.app.id
  internal_load_balancer_enabled = false

  workload_profile {
    name                  = "Consumption"
    workload_profile_type = "Consumption"
  }

  depends_on = [azurerm_subnet_network_security_group_association.app]
}

# Two-phase first deploy: the app is created only once its image is in this registry and its passcode is in this vault.
locals {
  deploy_application = var.image_digest != null && var.demo_passcode_secret_uri != null
}

resource "azurerm_container_app" "app" {
  count                        = local.deploy_application ? 1 : 0
  name                         = local.container_app_name
  resource_group_name          = azurerm_resource_group.demo.name
  container_app_environment_id = azurerm_container_app_environment.demo.id
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"

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
        name  = "Persistence__CaseStateProvider"
        value = "Sql"
      }

      env {
        name  = "ConnectionStrings__CaseState"
        value = "Server=tcp:${local.sql_server_name}.database.windows.net,1433;Database=${local.sql_database_name};Authentication=Active Directory Managed Identity;User Id=${azurerm_user_assigned_identity.app.client_id};Encrypt=True;TrustServerCertificate=False;"
      }

      env {
        name  = "ModelGateway__Endpoint"
        value = local.model_endpoint
      }

      env {
        name  = "ModelGateway__Audience"
        value = local.gateway_audience
      }

      env {
        name  = "ModelGateway__DeploymentName"
        value = local.gateway_model_deployment
      }

      env {
        name  = "ModelGateway__Model"
        value = var.model_name
      }

      env {
        name  = "ModelGateway__ModelVersion"
        value = var.model_version
      }

      env {
        name  = "Demo__SeedCases"
        value = "true"
      }

      env {
        name  = "Demo__SeedJourney"
        value = "true"
      }

      env {
        name        = "Demo__Passcode"
        secret_name = "demo-passcode"
      }
    }
  }

  # The release workflow rolls out new signed digests with az containerapp update; Terraform sets only the first one.
  lifecycle {
    ignore_changes = [template[0].container[0].image]
  }

  # The image pull and the passcode lookup go through the private endpoints, so their DNS must resolve first.
  depends_on = [
    azurerm_role_assignment.app_acr_pull,
    azurerm_role_assignment.app_passcode_secret,
    azurerm_private_endpoint.demo,
    azurerm_private_dns_zone_virtual_network_link.demo,
  ]
}

# The frozen four-arm evaluation (DD-044) runs on demand with the app's image and identity, never inside the web app.
# The environment keeps no console logs, so the job prints the report as gzip+base64 lines between markers and
# then stays alive for an hour so an operator can read it from the log stream. Start it with the released digest:
#   az containerapp job start -g <rg> -n <job> --image <registry>/airline-api@<digest> --container-name evaluation
locals {
  evaluation_job_name = "caj-${local.name_prefix}-eval"
  evaluation_command  = <<-SH
    dotnet AirlineDemo.Api.dll --evaluate-v2 \
      --suite-manifest /app/evaluation/v2/manifest.json --cases /app/evaluation/v2/cases.jsonl \
      --arm-b-manifest /app/config/model-manifest.json --arm-c-manifest /app/config/model-manifest-arm-c.json \
      --report "synthetic demonstration result" > /tmp/evaluation-report.json
    echo "EVALUATION_EXIT=$?"
    echo "EVALUATION_REPORT_BEGIN"
    gzip -c /tmp/evaluation-report.json | base64 | tr -d '\n' | fold -w 1000
    echo
    echo "EVALUATION_REPORT_END"
    sleep 3600
  SH
}

resource "azurerm_container_app_job" "evaluation" {
  count                        = local.deploy_application && var.evaluation_model_name != null ? 1 : 0
  name                         = local.evaluation_job_name
  location                     = azurerm_resource_group.demo.location
  resource_group_name          = azurerm_resource_group.demo.name
  container_app_environment_id = azurerm_container_app_environment.demo.id
  workload_profile_name        = "Consumption"
  replica_timeout_in_seconds   = 14400
  replica_retry_limit          = 0

  manual_trigger_config {
    parallelism              = 1
    replica_completion_count = 1
  }

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app.id]
  }

  registry {
    server   = azurerm_container_registry.acr.login_server
    identity = azurerm_user_assigned_identity.app.id
  }

  template {
    container {
      name    = "evaluation"
      image   = "${azurerm_container_registry.acr.login_server}/airline-api@${var.image_digest}"
      cpu     = 0.5
      memory  = "1Gi"
      command = ["/bin/sh", "-c", local.evaluation_command]

      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.app.client_id
      }

      env {
        name  = "ModelGateway__Endpoint"
        value = local.model_endpoint
      }

      env {
        name  = "ModelGateway__Audience"
        value = local.gateway_audience
      }

      env {
        name  = "ModelGateway__DeploymentName"
        value = local.gateway_model_deployment
      }

      env {
        name  = "ModelGateway__Model"
        value = var.model_name
      }

      env {
        name  = "ModelGateway__ModelVersion"
        value = var.model_version
      }

      # Azure retail prices, USD per million uncached tokens, GlobalStandard in swedencentral (gpt-6-luna for B and D,
      # gpt-6-sol for C). Cached-input discounts are not applied, so reported costs are an upper bound.
      env {
        name  = "Evaluation__Rates__B__InputPerMillion"
        value = "0.10"
      }

      env {
        name  = "Evaluation__Rates__B__OutputPerMillion"
        value = "0.50"
      }

      env {
        name  = "Evaluation__Rates__C__InputPerMillion"
        value = "2.00"
      }

      env {
        name  = "Evaluation__Rates__C__OutputPerMillion"
        value = "10.00"
      }

      env {
        name  = "Evaluation__Rates__D__InputPerMillion"
        value = "0.10"
      }

      env {
        name  = "Evaluation__Rates__D__OutputPerMillion"
        value = "0.50"
      }
    }
  }

  # Each run is started with the released digest (az containerapp job start --image); Terraform sets only the first one.
  lifecycle {
    ignore_changes = [template[0].container[0].image]
  }

  depends_on = [
    azurerm_role_assignment.app_acr_pull,
    azurerm_private_endpoint.demo,
    azurerm_private_dns_zone_virtual_network_link.demo,
    azurerm_cognitive_deployment.evaluation,
  ]
}
