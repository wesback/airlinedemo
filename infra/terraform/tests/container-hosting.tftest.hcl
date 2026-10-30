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
  presenter_reviewer_cidrs        = ["203.0.113.0/24", "192.0.2.0/24"]
  egress_allowed_destinations     = ["198.51.100.0/24", "203.0.113.0/24"]
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

run "private_acr_dns_and_container_apps_wiring" {
  command = plan

  assert {
    condition     = azurerm_container_registry.acr.public_network_access_enabled == false && azurerm_container_registry.acr.admin_enabled == false && azurerm_container_registry.acr.sku == "Premium"
    error_message = "The ACR must be private, admin-disabled, and Premium."
  }
  assert {
    condition     = azurerm_private_dns_zone.demo["acr"].name == "privatelink.azurecr.io" && azurerm_private_dns_zone_virtual_network_link.demo["acr"].name == "link-acr" && azurerm_private_dns_zone_virtual_network_link.demo["acr"].private_dns_zone_name == "privatelink.azurecr.io"
    error_message = "The ACR private DNS zone and its VNet link must use the private ACR zone."
  }
  assert {
    condition     = azurerm_container_app_environment.demo.internal_load_balancer_enabled == false && can(regex("(?s)resource \"azurerm_container_app_environment\" \"demo\" \\{.*infrastructure_subnet_id\\s*=\\s*azurerm_subnet\\.app\\.id", file("container-hosting.tf"))) && can(regex("(?s)resource \"azurerm_container_app\" \"app\" \\{\\s*(count\\s*=[^\\n]*\\s*)?name\\s*=\\s*local\\.container_app_name\\s*resource_group_name\\s*=\\s*azurerm_resource_group\\.demo\\.name\\s*container_app_environment_id\\s*=\\s*azurerm_container_app_environment\\.demo\\.id", file("container-hosting.tf")))
    error_message = "The Container Apps environment must use the app subnet, and the app must reference that environment."
  }
  assert {
    condition     = azurerm_container_app.app[0].revision_mode == "Single" && can(regex("(?s)registry \\{\\s*server\\s*=\\s*azurerm_container_registry\\.acr\\.login_server", file("container-hosting.tf")))
    error_message = "The Container App registry must use the private ACR login server."
  }
}

run "https_ingress_and_ingress_deny" {
  command = plan

  assert {
    condition     = azurerm_container_app.app[0].ingress[0].external_enabled == true && azurerm_container_app.app[0].ingress[0].transport == "http" && azurerm_container_app.app[0].ingress[0].allow_insecure_connections == false
    error_message = "The app must expose external HTTP ingress while rejecting insecure connections."
  }
  assert {
    condition     = toset([for restriction in azurerm_container_app.app[0].ingress[0].ip_security_restriction : restriction.ip_address_range if restriction.action == "Allow"]) == toset(var.presenter_reviewer_cidrs) && length(azurerm_container_app.app[0].ingress[0].ip_security_restriction) == length(var.presenter_reviewer_cidrs)
    error_message = "Ingress must allow exactly the configured presenter/reviewer CIDRs and must not allow an unlisted CIDR."
  }
  assert {
    condition     = !contains([for restriction in azurerm_container_app.app[0].ingress[0].ip_security_restriction : restriction.ip_address_range if restriction.action == "Allow"], "198.51.100.0/24")
    error_message = "An unlisted ingress CIDR must not receive an Allow rule."
  }
}

run "egress_allowlist_and_egress_deny" {
  command = plan

  assert {
    condition     = toset([for rule in values(azurerm_network_security_rule.egress_allow) : rule.destination_address_prefix if rule.access == "Allow"]) == toset(var.egress_allowed_destinations) && length(azurerm_network_security_rule.egress_allow) == length(var.egress_allowed_destinations)
    error_message = "Outbound Allow rules must cover exactly the configured egress destinations."
  }
  assert {
    condition     = azurerm_network_security_rule.egress_deny_all.access == "Deny" && azurerm_network_security_rule.egress_deny_all.direction == "Outbound" && azurerm_network_security_rule.egress_deny_all.destination_address_prefix == "*" && azurerm_network_security_rule.egress_deny_all.priority == 4096
    error_message = "Outbound traffic to unlisted destinations must be denied by the catch-all rule."
  }
  assert {
    condition     = !contains([for rule in values(azurerm_network_security_rule.egress_allow) : rule.destination_address_prefix], "192.0.2.0/24")
    error_message = "An unlisted egress destination must not receive an Allow rule."
  }
}

run "private_dependency_references" {
  command = plan

  assert {
    condition = (
      local.private_endpoints.sql.subresource == "sqlServer" && local.private_endpoints.sql.zone == "sql" &&
      local.private_endpoints.blob.subresource == "blob" && local.private_endpoints.blob.zone == "blob" &&
      local.private_endpoints.model.subresource == "account" && local.private_endpoints.model.zone == "model" &&
      local.private_endpoints.kv.subresource == "vault" && local.private_endpoints.kv.zone == "kv" &&
      local.private_endpoints.acr.subresource == "registry" && local.private_endpoints.acr.zone == "acr" &&
      can(regex("(?s)sql\\s*=\\s*\\{\\s*resource_id\\s*=\\s*azurerm_mssql_server\\.sql\\.id", file("main.tf"))) &&
      can(regex("(?s)blob\\s*=\\s*\\{\\s*resource_id\\s*=\\s*azurerm_storage_account\\.blob\\.id", file("main.tf"))) &&
      can(regex("(?s)model\\s*=\\s*\\{\\s*resource_id\\s*=\\s*azurerm_cognitive_account\\.model\\.id", file("main.tf"))) &&
      can(regex("(?s)kv\\s*=\\s*\\{\\s*resource_id\\s*=\\s*azurerm_key_vault\\.kv\\.id", file("main.tf"))) &&
      can(regex("(?s)acr\\s*=\\s*\\{\\s*resource_id\\s*=\\s*azurerm_container_registry\\.acr\\.id", file("main.tf")))
    )
    error_message = "Each private endpoint must target its configured resource, subresource, and DNS zone."
  }
  assert {
    condition = (
      local.private_dns_zones == {
        sql   = "privatelink.database.windows.net"
        blob  = "privatelink.blob.core.windows.net"
        model = "privatelink.openai.azure.com"
        kv    = "privatelink.vaultcore.azure.net"
        acr   = "privatelink.azurecr.io"
      } &&
      azurerm_private_dns_zone.demo["sql"].name == "privatelink.database.windows.net" &&
      azurerm_private_dns_zone.demo["blob"].name == "privatelink.blob.core.windows.net" &&
      azurerm_private_dns_zone.demo["model"].name == "privatelink.openai.azure.com" &&
      azurerm_private_dns_zone.demo["kv"].name == "privatelink.vaultcore.azure.net" &&
      azurerm_private_dns_zone.demo["acr"].name == "privatelink.azurecr.io" &&
      azurerm_private_dns_zone_virtual_network_link.demo["sql"].name == "link-sql" &&
      azurerm_private_dns_zone_virtual_network_link.demo["blob"].name == "link-blob" &&
      azurerm_private_dns_zone_virtual_network_link.demo["model"].name == "link-model" &&
      azurerm_private_dns_zone_virtual_network_link.demo["kv"].name == "link-kv" &&
      azurerm_private_dns_zone_virtual_network_link.demo["acr"].name == "link-acr" &&
      azurerm_private_dns_zone_virtual_network_link.demo["sql"].private_dns_zone_name == "privatelink.database.windows.net" &&
      azurerm_private_dns_zone_virtual_network_link.demo["blob"].private_dns_zone_name == "privatelink.blob.core.windows.net" &&
      azurerm_private_dns_zone_virtual_network_link.demo["model"].private_dns_zone_name == "privatelink.openai.azure.com" &&
      azurerm_private_dns_zone_virtual_network_link.demo["kv"].private_dns_zone_name == "privatelink.vaultcore.azure.net" &&
      azurerm_private_dns_zone_virtual_network_link.demo["acr"].private_dns_zone_name == "privatelink.azurecr.io" &&
      can(regex("(?s)private_dns_zone_group \\{\\s*name\\s*=\\s*\"dzg-\\$\\{each\\.key\\}\"\\s*private_dns_zone_ids\\s*=\\s*\\[azurerm_private_dns_zone\\.demo\\[each\\.value\\.zone\\]\\.id\\]", file("main.tf")))
    )
    error_message = "Every private endpoint must reference the DNS zone configured for its dependency."
  }
  assert {
    condition     = azurerm_private_dns_zone_virtual_network_link.demo["acr"].private_dns_zone_name == "privatelink.azurecr.io" && can(regex("(?s)resource \"azurerm_private_endpoint\" \"demo\" \\{.*subnet_id\\s*=\\s*azurerm_subnet\\.private_endpoints\\.id.*private_connection_resource_id\\s*=\\s*each\\.value\\.resource_id", file("main.tf"))) && can(regex("(?s)resource \"azurerm_private_dns_zone_virtual_network_link\" \"demo\" \\{.*private_dns_zone_name\\s*=\\s*azurerm_private_dns_zone\\.demo\\[each\\.key\\]\\.name.*virtual_network_id\\s*=\\s*azurerm_virtual_network\\.demo\\.id", file("main.tf")))
    error_message = "Private endpoints must use the configured private endpoint subnet and DNS zone, and DNS links must use the configured VNet."
  }
}

run "valid_image_digest" {
  command = plan

  assert {
    condition     = can(regex("^sha256:[0-9a-f]{64}$", var.image_digest)) && length(var.image_digest) == 71
    error_message = "The configured image digest must contain exactly 64 lowercase hexadecimal characters after sha256:."
  }
  assert {
    condition     = azurerm_container_app.app[0].template[0].min_replicas == 1 && can(regex("(?s)image\\s*=\\s*\"\\$\\{azurerm_container_registry\\.acr\\.login_server\\}/airline-api@\\$\\{var\\.image_digest\\}\"", file("container-hosting.tf")))
    error_message = "The Container App image must use the configured immutable digest, not a tag."
  }
}

run "invalid_presenter_reviewer_cidrs" {
  command = plan

  variables {
    presenter_reviewer_cidrs = ["not-cidr"]
  }

  expect_failures = [var.presenter_reviewer_cidrs]
}

run "empty_image_digest" {
  command = plan

  variables {
    image_digest = ""
  }

  expect_failures = [var.image_digest]
}

run "demo_passcode_secret_is_referenced" {
  command = plan

  assert {
    condition     = length([for secret in azurerm_container_app.app[0].secret : secret if secret.name == "demo-passcode" && secret.key_vault_secret_id == "https://kv-demo-test.vault.azure.net/secrets/demo-passcode"]) == 1 && length(azurerm_container_app.app[0].secret) == 1
    error_message = "The Container App must reference the configured demo passcode Key Vault secret."
  }
}

run "app_seeds_demo_cases" {
  command = plan

  assert {
    condition     = length([for e in azurerm_container_app.app[0].template[0].container[0].env : e if e.name == "Demo__SeedCases"]) == 1 && length([for e in azurerm_container_app.app[0].template[0].container[0].env : e if e.name == "Demo__SeedCases" && e.value == "true"]) == 1
    error_message = "The app container must set Demo__SeedCases to true so the demo cases exist."
  }

  assert {
    condition     = length([for e in azurerm_container_app.app[0].template[0].container[0].env : e if e.name == "Demo__SeedJourney"]) == 1 && length([for e in azurerm_container_app.app[0].template[0].container[0].env : e if e.name == "Demo__SeedJourney" && e.value == "true"]) == 1
    error_message = "The app container must set Demo__SeedJourney to true so the return-readiness journey is available."
  }
}

run "app_uses_durable_sql_case_state" {
  command = plan

  assert {
    condition     = one([for e in azurerm_container_app.app[0].template[0].container[0].env : e.value if e.name == "Persistence__CaseStateProvider"]) == "Sql"
    error_message = "The app must select the SQL store rather than writing to the non-root container filesystem."
  }
}

run "evaluation_job_exists_only_with_an_evaluation_model" {
  command = plan

  assert {
    condition     = length(azurerm_container_app_job.evaluation) == 0
    error_message = "Without evaluation_model_name no evaluation job is created."
  }
}

run "evaluation_job_runs_the_scored_report_with_the_app_identity" {
  command = plan

  variables {
    evaluation_model_name    = "gpt-eval-test"
    evaluation_model_version = "1111-11-11"
  }

  assert {
    condition = (can(regex("(?s)resource \"azurerm_container_app_job\" \"evaluation\" \\{.*identity_ids\\s*=\\s*\\[azurerm_user_assigned_identity\\.app\\.id\\]", file("container-hosting.tf"))) &&
      azurerm_container_app_job.evaluation[0].replica_retry_limit == 0 &&
    azurerm_container_app_job.evaluation[0].manual_trigger_config[0].parallelism == 1)
    error_message = "The evaluation job runs once, manually, with the app's identity."
  }

  assert {
    condition = (strcontains(azurerm_container_app_job.evaluation[0].template[0].container[0].command[2], "--evaluate-v2") &&
      strcontains(azurerm_container_app_job.evaluation[0].template[0].container[0].command[2], "--report \"synthetic demonstration result\"") &&
      strcontains(azurerm_container_app_job.evaluation[0].template[0].container[0].command[2], "--arm-c-manifest /app/config/model-manifest-arm-c.json") &&
    strcontains(azurerm_container_app_job.evaluation[0].template[0].container[0].command[2], "EVALUATION_REPORT_END"))
    error_message = "The job runs the labelled scored report and prints it between markers."
  }

  assert {
    condition = (toset([for e in azurerm_container_app_job.evaluation[0].template[0].container[0].env : e.name]) == toset([
      "AZURE_CLIENT_ID", "ModelGateway__Endpoint", "ModelGateway__Audience", "ModelGateway__DeploymentName", "ModelGateway__Model",
      "ModelGateway__ModelVersion", "Evaluation__Rates__B__InputPerMillion", "Evaluation__Rates__B__OutputPerMillion",
      "Evaluation__Rates__C__InputPerMillion", "Evaluation__Rates__C__OutputPerMillion", "Evaluation__Rates__D__InputPerMillion",
    "Evaluation__Rates__D__OutputPerMillion"]))
    error_message = "The job gets only gateway settings and token rates: no SQL connection, no passcode and no seeding flags."
  }
}
