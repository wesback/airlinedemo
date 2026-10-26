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
  location                        = "swedencentral"
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

# Fixes for settings Azure rejects at apply time; plan-only with the mocked provider.
run "gateway_has_dedicated_subnet_in_vnet_region" {
  command = plan

  assert {
    condition     = azurerm_subnet.gateway.address_prefixes == tolist(["10.60.3.0/24"]) && azurerm_api_management.gateway.location == "swedencentral" && can(regex("(?s)virtual_network_configuration \\{\\s*subnet_id\\s*=\\s*azurerm_subnet\\.gateway\\.id", file("main.tf")))
    error_message = "API Management must join its own subnet in the virtual network's region."
  }
  assert {
    condition     = length([for r in azurerm_network_security_group.gateway.security_rule : r if r.direction == "Inbound" && r.access == "Allow" && r.source_address_prefix == "ApiManagement" && r.destination_port_range == "3443"]) == 1
    error_message = "The gateway NSG must admit the API Management control plane on 3443."
  }
}

run "gateway_region_must_match_location" {
  command = plan

  variables {
    gateway_region = "uksouth"
  }

  expect_failures = [azurerm_api_management.gateway]
}

run "gateway_routes_to_created_deployment_without_duplicate_path" {
  command = plan

  assert {
    condition     = azurerm_api_management_api_operation.gateway_chat_completions.url_template == "v1/chat/completions" && azurerm_api_management_api.gateway_model.service_url == "https://aoai-demo-test.openai.azure.com/openai"
    error_message = "The gateway must call the deployment this configuration creates on the model host name that matches its certificate."
  }
  assert {
    condition     = azurerm_api_management_api_operation_policy.gateway_chat_completions.operation_id == "chat-completions" && strcontains(file("main.tf"), "rewrite-uri template=\"/v1/chat/completions\"") && !strcontains(file("main.tf"), "api-version=")
    error_message = "rewrite-uri is relative to service_url, which already ends in /openai."
  }
}

run "container_apps_environment_uses_workload_profiles" {
  command = plan

  assert {
    condition     = one(azurerm_container_app_environment.demo.workload_profile).workload_profile_type == "Consumption" && azurerm_container_app.app[0].workload_profile_name == "Consumption"
    error_message = "A delegated /24 subnet requires a workload-profiles environment."
  }
}

run "app_subnet_allows_sql_and_platform_traffic_only" {
  command = plan

  assert {
    condition     = toset(azurerm_network_security_rule.egress_private_endpoints_sql.destination_port_ranges) == toset(["1433", "11000-11999"]) && azurerm_network_security_rule.egress_private_endpoints_sql.destination_address_prefix == "10.60.2.0/24" && azurerm_network_security_rule.egress_private_endpoints_sql.priority < azurerm_network_security_rule.egress_deny_all.priority
    error_message = "The app must reach SQL through its private endpoint ahead of the deny-all."
  }
  assert {
    condition     = toset([for r in values(azurerm_network_security_rule.egress_container_apps_platform) : r.destination_address_prefix]) == toset(["AzureActiveDirectory", "AzureMonitor", "MicrosoftContainerRegistry", "AzureFrontDoor.FirstParty", "168.63.129.16"])
    error_message = "Platform egress must be exactly the Container Apps service tags and Azure DNS."
  }
  assert {
    condition     = azurerm_network_security_rule.app_load_balancer_inbound.source_address_prefix == "AzureLoadBalancer" && azurerm_network_security_rule.app_load_balancer_inbound.priority < azurerm_network_security_rule.ingress_deny_all.priority
    error_message = "Load balancer health probes must be allowed ahead of the inbound deny-all."
  }
}

run "key_vault_stays_private_without_deployer_cidrs" {
  command = plan

  assert {
    condition     = azurerm_key_vault.kv.public_network_access_enabled == false && length(azurerm_key_vault.kv.network_acls[0].ip_rules) == 0 && azurerm_role_assignment.terraform_kv_certificates.role_definition_name == "Key Vault Certificates Officer" && length(azurerm_network_security_perimeter.deployer) == 0
    error_message = "Without deployer CIDRs the vault must stay private with no perimeter; the Terraform principal still needs certificate rights."
  }
}

run "key_vault_admits_only_deployer_cidrs" {
  command = plan

  variables {
    deployer_ip_cidrs = ["198.51.100.7/32"]
  }

  assert {
    condition     = azurerm_key_vault.kv.public_network_access_enabled == false && length(azurerm_key_vault.kv.network_acls[0].ip_rules) == 0 && azurerm_network_security_perimeter_access_rule.deployer[0].direction == "Inbound" && toset(azurerm_network_security_perimeter_access_rule.deployer[0].address_prefixes) == toset(["198.51.100.7/32"]) && azurerm_network_security_perimeter_association.key_vault[0].access_mode == "Enforced"
    error_message = "Deployer CIDRs must reach the vault only through an enforced perimeter inbound rule; public access stays disabled."
  }
}

run "rejects_invalid_deployer_cidr" {
  command = plan

  variables {
    deployer_ip_cidrs = ["not-a-cidr"]
  }

  expect_failures = [var.deployer_ip_cidrs]
}

run "app_reads_passcode_from_this_vault" {
  command = plan

  assert {
    condition     = length(azurerm_role_assignment.app_passcode_secret) == 1 && azurerm_role_assignment.app_passcode_secret[0].role_definition_name == "Key Vault Secrets User"
    error_message = "A passcode in this deployment's vault must be readable by the app identity."
  }
}

run "no_grant_for_passcode_in_another_vault" {
  command = plan

  variables {
    demo_passcode_secret_uri = "https://kv-other.vault.azure.net/secrets/demo-passcode"
  }

  assert {
    condition     = length(azurerm_role_assignment.app_passcode_secret) == 0
    error_message = "Terraform must not grant on a vault it does not manage."
  }
}

run "telemetry_is_reachable_only_through_private_link_scope" {
  command = plan

  assert {
    condition     = azurerm_monitor_private_link_scope.telemetry.ingestion_access_mode == "PrivateOnly" && azurerm_monitor_private_link_scope.telemetry.query_access_mode == "PrivateOnly"
    error_message = "The Azure Monitor Private Link Scope must accept ingestion and queries only from its private endpoint."
  }

  assert {
    condition     = azurerm_log_analytics_workspace.telemetry.internet_ingestion_enabled == false && azurerm_application_insights.app.internet_ingestion_enabled == false && azurerm_application_insights.app.internet_query_enabled == false
    error_message = "Telemetry must stay closed to the public internet."
  }

  assert {
    condition     = azurerm_monitor_private_link_scoped_service.workspace.scope_name == azurerm_monitor_private_link_scope.telemetry.name && azurerm_monitor_private_link_scoped_service.app_insights.scope_name == azurerm_monitor_private_link_scope.telemetry.name && can(regex("linked_resource_id\\s*=\\s*azurerm_log_analytics_workspace\\.telemetry\\.id", file("identity-and-telemetry.tf"))) && can(regex("linked_resource_id\\s*=\\s*azurerm_application_insights\\.app\\.id", file("identity-and-telemetry.tf")))
    error_message = "Both the workspace and Application Insights must be linked to the scope."
  }

  assert {
    condition     = azurerm_private_endpoint.monitor.private_service_connection[0].subresource_names == tolist(["azuremonitor"])
    error_message = "The scope's private endpoint must be the azuremonitor sub-resource in the private endpoint subnet."
  }

  assert {
    condition = (
      toset([for z in azurerm_private_dns_zone.monitor : z.name]) == toset([
        "privatelink.monitor.azure.com",
        "privatelink.oms.opinsights.azure.com",
        "privatelink.ods.opinsights.azure.com",
        "privatelink.agentsvc.azure-automation.net",
      ]) &&
      length(azurerm_private_dns_zone_virtual_network_link.monitor) == 4 &&
      length(one(azurerm_private_endpoint.monitor.private_dns_zone_group).private_dns_zone_ids) == 5
    )
    error_message = "All Azure Monitor private DNS zones, plus the blob zone, must be linked and registered by the endpoint."
  }
}

run "build_runner_is_off_by_default" {
  command = plan

  assert {
    condition     = length(azurerm_linux_virtual_machine.build_runner) == 0 && length(azurerm_subnet.build_runner) == 0 && length(azurerm_nat_gateway.build_runner) == 0 && length(azurerm_role_assignment.build_runner_token_secret) == 0
    error_message = "No runner resources may be created unless enable_build_runner is true."
  }
}

run "build_runner_is_private_and_reads_only_its_token" {
  command = plan

  override_resource {
    target          = azurerm_key_vault.kv
    override_during = plan
    values = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test"
    }
  }

  override_resource {
    target          = azurerm_user_assigned_identity.build_runner[0]
    override_during = plan
    values = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-demo-runner"
      client_id    = "66666666-6666-6666-6666-666666666666"
      principal_id = "77777777-7777-7777-7777-777777777777"
    }
  }

  variables {
    enable_build_runner               = true
    build_runner_github_repository    = "wesback/airlinedemo"
    build_runner_admin_ssh_public_key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDAPD8CazJZiMuT896VEPnnF3hpgv2WYm5euSS6lqUNy plan-only-test"
  }

  assert {
    condition     = azurerm_subnet.build_runner[0].address_prefixes == tolist(["10.60.4.0/24"]) && azurerm_subnet.build_runner[0].default_outbound_access_enabled == false
    error_message = "The runner subnet must default to the fifth /24 and have no implicit outbound access."
  }

  assert {
    condition     = one([for r in azurerm_network_security_group.build_runner[0].security_rule : r if r.direction == "Inbound"]).access == "Deny" && length(azurerm_network_interface.build_runner[0].ip_configuration[0].public_ip_address_id == null ? [] : [1]) == 0
    error_message = "The runner must accept no inbound traffic and have no public address."
  }

  assert {
    condition     = azurerm_role_assignment.build_runner_token_secret[0].role_definition_name == "Key Vault Secrets User" && endswith(azurerm_role_assignment.build_runner_token_secret[0].scope, "/secrets/github-runner-token")
    error_message = "The runner identity may read only the runner token secret."
  }

  assert {
    condition     = azurerm_key_vault_secret.build_runner_token[0].name == "github-runner-token" && azurerm_key_vault_secret.build_runner_token[0].value == null && azurerm_key_vault_secret.build_runner_token[0].value_wo_version == 1 && can(regex("(?s)resource \"azurerm_key_vault_secret\" \"build_runner_token\".*value_wo\\s*=\\s*\"unset\".*ignore_changes\\s*=\\s*\\[value, value_wo_version\\]", file("build-runner.tf")))
    error_message = "Terraform creates the token secret as unset and never overwrites the token the deployer stores."
  }

  assert {
    condition     = strcontains(base64decode(azurerm_linux_virtual_machine.build_runner[0].custom_data), "[ \"$github_token\" != unset ]")
    error_message = "The runner must not register until the deployer has replaced the unset token."
  }

  assert {
    condition     = strcontains(base64decode(azurerm_linux_virtual_machine.build_runner[0].custom_data), "--labels private-acr") && strcontains(base64decode(azurerm_linux_virtual_machine.build_runner[0].custom_data), "https://github.com/wesback/airlinedemo")
    error_message = "The runner must register with the repository under the private-acr label the workflows target."
  }
}

run "build_runner_needs_repository_and_key" {
  command = plan

  variables {
    enable_build_runner = true
  }

  expect_failures = [azurerm_linux_virtual_machine.build_runner]
}

run "gateway_hostnames_resolve_privately_in_vnet" {
  command = plan

  assert {
    condition     = azurerm_private_dns_zone.gateway.name == "azure-api.net" && azurerm_private_dns_zone_virtual_network_link.gateway.private_dns_zone_name == "azure-api.net"
    error_message = "The VNet must resolve azure-api.net through a linked private zone."
  }

  assert {
    condition = toset([for r in azurerm_private_dns_a_record.gateway : r.name]) == toset([
      local.gateway_name,
      "${local.gateway_name}.portal",
      "${local.gateway_name}.developer",
      "${local.gateway_name}.management",
      "${local.gateway_name}.scm",
    ])
    error_message = "Every default gateway hostname needs a private A record."
  }

  assert {
    condition     = azurerm_network_security_rule.egress_gateway.access == "Allow" && azurerm_network_security_rule.egress_gateway.destination_address_prefix == "10.60.3.0/24" && azurerm_network_security_rule.egress_gateway.destination_port_range == "443" && azurerm_network_security_rule.egress_gateway.priority < azurerm_network_security_rule.egress_deny_all.priority
    error_message = "The app subnet must reach the gateway subnet on 443 before the deny-all rule."
  }

  assert {
    condition     = can(regex("records\\s*=\\s*azurerm_api_management\\.gateway\\.private_ip_addresses", file("main.tf"))) && azurerm_private_dns_a_record.gateway[""].zone_name == "azure-api.net"
    error_message = "The records must point at the gateway's private addresses."
  }
}

run "platform_phase_creates_no_application" {
  command = plan

  variables {
    image_digest             = null
    demo_passcode_secret_uri = null
  }

  assert {
    condition     = length(azurerm_container_app.app) == 0 && length(azurerm_role_assignment.app_passcode_secret) == 0 && length(azurerm_role_assignment.release_container_app) == 0
    error_message = "Without an image digest and passcode URI, Terraform must create the platform only."
  }

  assert {
    condition     = azurerm_container_registry.acr.public_network_access_enabled == false && azurerm_key_vault.kv.public_network_access_enabled == false
    error_message = "The platform phase must still create the private registry and vault the second phase needs."
  }
}

run "digest_without_passcode_uri_is_rejected" {
  command = plan

  variables {
    demo_passcode_secret_uri = null
  }

  expect_failures = [var.demo_passcode_secret_uri]
}

run "release_workflow_owns_the_running_image" {
  command = plan

  assert {
    condition     = length(azurerm_container_app.app) == 1 && can(regex("(?s)ignore_changes\\s*=\\s*\\[template\\[0\\]\\.container\\[0\\]\\.image\\]", file("container-hosting.tf")))
    error_message = "Terraform must set the first image only; the release workflow rolls out later digests."
  }
}

run "redeploy_adopts_a_recovered_key_vault" {
  command = plan

  variables {
    enable_build_runner               = true
    build_runner_github_repository    = "contoso/airlinedemo"
    build_runner_admin_ssh_public_key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDAPD8CazJZiMuT896VEPnnF3hpgv2WYm5euSS6lqUNy plan-only-test"
    adopt_key_vault = {
      certificates_role_assignment_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test/providers/Microsoft.Authorization/roleAssignments/55555555-5555-5555-5555-555555555555"
      secrets_role_assignment_id      = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test/providers/Microsoft.Authorization/roleAssignments/66666666-6666-6666-6666-666666666666"
      signing_certificate_version     = "0123456789abcdef0123456789abcdef"
      build_runner_token_version      = "fedcba9876543210fedcba9876543210" #gitleaks:allow
    }
  }

  override_resource {
    target          = azurerm_resource_group.demo
    override_during = plan
    values          = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test" }
  }
  override_resource {
    target          = azurerm_key_vault.kv
    override_during = plan
    values          = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test" }
  }
  override_resource {
    target          = azurerm_role_assignment.terraform_kv_certificates
    override_during = plan
    values          = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test/providers/Microsoft.Authorization/roleAssignments/55555555-5555-5555-5555-555555555555" }
  }
  override_resource {
    target          = azurerm_role_assignment.terraform_kv_secrets
    override_during = plan
    values          = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test/providers/Microsoft.Authorization/roleAssignments/66666666-6666-6666-6666-666666666666" }
  }
  override_resource {
    target          = azurerm_key_vault_certificate.signing
    override_during = plan
    values = {
      id                  = "https://kv-demo-test.vault.azure.net/certificates/release-signing/0123456789abcdef0123456789abcdef"
      resource_manager_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test/certificates/release-signing"
    }
  }
  override_resource {
    target          = azurerm_key_vault_secret.build_runner_token[0]
    override_during = plan
    values          = { id = "https://kv-demo-test.vault.azure.net/secrets/github-runner-token/fedcba9876543210fedcba9876543210" }
  }

  assert {
    condition = (
      local.key_vault_id == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test"
      && length(local.adopt_key_vault) == 1
      && can(regex("to\\s*=\\s*azurerm_key_vault_certificate\\.signing\\s+id\\s*=\\s*\"\\$\\{local\\.key_vault_data_plane\\}/certificates/\\$\\{var\\.signing_certificate_name\\}/", file("key-vault-adoption.tf")))
      && can(regex("to\\s*=\\s*azurerm_key_vault_secret\\.build_runner_token\\[0\\]", file("key-vault-adoption.tf")))
    )
    error_message = "A recovered vault, its deployer roles, certificate and runner token are imported instead of created."
  }
}

run "adoption_rejects_role_outside_the_vault" {
  command = plan

  variables {
    adopt_key_vault = {
      certificates_role_assignment_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Authorization/roleAssignments/55555555-5555-5555-5555-555555555555"
      secrets_role_assignment_id      = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test/providers/Microsoft.Authorization/roleAssignments/66666666-6666-6666-6666-666666666666"
    }
  }

  expect_failures = [var.adopt_key_vault]
}

run "gateway_audience_is_an_entra_application_the_app_identity_may_call" {
  command = plan

  override_resource {
    target          = azuread_application.gateway
    override_during = plan
    values = {
      id        = "/applications/00000000-0000-0000-0000-0000000000a1"
      client_id = "00000000-0000-0000-0000-0000000000c1"
    }
  }
  override_resource {
    target          = azuread_service_principal.gateway
    override_during = plan
    values = {
      object_id = "00000000-0000-0000-0000-0000000000b1"
    }
  }
  override_resource {
    target          = azurerm_user_assigned_identity.app
    override_during = plan
    values = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-app"
      client_id    = "00000000-0000-0000-0000-0000000000e2"
      principal_id = "00000000-0000-0000-0000-0000000000e1"
    }
  }

  assert {
    condition     = output.gateway_audience == "api://00000000-0000-0000-0000-0000000000c1" && azuread_application_identifier_uri.gateway.identifier_uri == output.gateway_audience
    error_message = "With no gateway_audience the identifier URI and the audience must be api://<client id> of the created application."
  }
  assert {
    condition     = azuread_application.gateway.sign_in_audience == "AzureADMyOrg" && one(azuread_application.gateway.api).requested_access_token_version == 2 && [for r in azuread_application.gateway.app_role : r.value] == ["Gateway.Invoke"] && [for r in azuread_application.gateway.app_role : r.allowed_member_types] == [toset(["Application"])]
    error_message = "The gateway application must be single-tenant, issue v2 tokens and define only the application role Gateway.Invoke."
  }
  assert {
    condition     = azuread_service_principal.gateway.app_role_assignment_required == true && azuread_app_role_assignment.gateway_invoke.principal_object_id == "00000000-0000-0000-0000-0000000000e1" && azuread_app_role_assignment.gateway_invoke.resource_object_id == "00000000-0000-0000-0000-0000000000b1" && azuread_app_role_assignment.gateway_invoke.app_role_id == one(azuread_application.gateway.app_role).id
    error_message = "Only principals assigned Gateway.Invoke may get a token, and the application identity must hold it."
  }
  assert {
    condition     = strcontains(azurerm_api_management_api_operation_policy.gateway_chat_completions.xml_content, "<audience>api://00000000-0000-0000-0000-0000000000c1</audience>\n        <audience>00000000-0000-0000-0000-0000000000c1</audience>") && can(regex("<claim name=\"roles\" match=\"any\">\\s*<value>Gateway.Invoke</value>", azurerm_api_management_api_operation_policy.gateway_chat_completions.xml_content))
    error_message = "The policy must accept the identifier URI and the client ID (v2 aud) and require the Gateway.Invoke role."
  }
}

run "gateway_custom_audience_is_used_verbatim" {
  command = plan

  override_resource {
    target          = azuread_application.gateway
    override_during = plan
    values = {
      id        = "/applications/00000000-0000-0000-0000-0000000000a1"
      client_id = "00000000-0000-0000-0000-0000000000c1"
    }
  }
  override_resource {
    target          = azurerm_user_assigned_identity.app
    override_during = plan
    values = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-app"
      client_id    = "00000000-0000-0000-0000-0000000000e2"
      principal_id = "00000000-0000-0000-0000-0000000000e1"
    }
  }

  variables {
    gateway_audience = "api://airline-demo-model-gateway"
  }

  assert {
    condition     = output.gateway_audience == "api://airline-demo-model-gateway" && azuread_application_identifier_uri.gateway.identifier_uri == "api://airline-demo-model-gateway" && strcontains(azurerm_api_management_api_operation_policy.gateway_chat_completions.xml_content, "<audience>api://airline-demo-model-gateway</audience>")
    error_message = "A set gateway_audience must become the identifier URI and the policy audience."
  }
}

run "gateway_audience_with_trailing_slash_is_rejected" {
  command = plan

  variables {
    gateway_audience = "api://airline-demo-model-gateway/"
  }

  expect_failures = [var.gateway_audience]
}

run "app_is_pointed_at_the_gateway" {
  command = plan

  variables {
    image_digest             = "sha256:0000000000000000000000000000000000000000000000000000000000000000"
    demo_passcode_secret_uri = "https://kv-demo.vault.azure.net/secrets/demo-passcode"
  }

  override_resource {
    target          = azurerm_api_management.gateway
    override_during = plan
    values = {
      gateway_url = "https://apim-demo.azure-api.net"
    }
  }
  override_resource {
    target          = azuread_application.gateway
    override_during = plan
    values = {
      id        = "/applications/00000000-0000-0000-0000-0000000000a1"
      client_id = "00000000-0000-0000-0000-0000000000c1"
    }
  }

  assert {
    condition = {
      for e in azurerm_container_app.app[0].template[0].container[0].env : e.name => e.value if startswith(e.name, "ModelGateway__")
      } == {
      ModelGateway__Endpoint       = "https://apim-demo.azure-api.net"
      ModelGateway__Audience       = "api://00000000-0000-0000-0000-0000000000c1"
      ModelGateway__DeploymentName = "gpt-demo-test"
      ModelGateway__Model          = "gpt-demo-model"
      ModelGateway__ModelVersion   = "0000-00-00"
    } && output.model_endpoint == "https://apim-demo.azure-api.net" && output.gateway_audience == "api://00000000-0000-0000-0000-0000000000c1"
    error_message = "The app must call the gateway URL (as output model_endpoint) and request a token for the gateway audience."
  }
}

run "app_follows_an_explicit_gateway_endpoint" {
  command = plan

  variables {
    image_digest             = "sha256:0000000000000000000000000000000000000000000000000000000000000000"
    demo_passcode_secret_uri = "https://kv-demo.vault.azure.net/secrets/demo-passcode"
    gateway_endpoint         = "https://gateway.internal.example"
    gateway_audience         = "api://airline-demo-model-gateway"
  }

  assert {
    condition = {
      for e in azurerm_container_app.app[0].template[0].container[0].env : e.name => e.value if startswith(e.name, "ModelGateway__")
      } == {
      ModelGateway__Endpoint       = "https://gateway.internal.example"
      ModelGateway__Audience       = "api://airline-demo-model-gateway"
      ModelGateway__DeploymentName = "gpt-demo-test"
      ModelGateway__Model          = "gpt-demo-model"
      ModelGateway__ModelVersion   = "0000-00-00"
    }
    error_message = "Set gateway_endpoint and gateway_audience must reach the app unchanged."
  }
}
