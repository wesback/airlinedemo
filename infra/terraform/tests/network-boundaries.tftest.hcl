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

  mock_resource "azurerm_cognitive_account" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.CognitiveServices/accounts/aoai-demo-test"
    }
  }

  mock_resource "azurerm_key_vault" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.KeyVault/vaults/kv-demo-test"
    }
  }

  mock_resource "azurerm_virtual_network" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Network/virtualNetworks/vnet-demo"
    }
  }

  mock_resource "azurerm_subnet" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Network/virtualNetworks/vnet-demo/subnets/snet-private-endpoints"
    }
  }

  mock_resource "azurerm_private_dns_zone" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test/providers/Microsoft.Network/privateDnsZones/privatelink.openai.azure.com"
    }
  }
}

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
  gateway_endpoint                = "https://apim-demo-test.private.invalid/"
  gateway_region                  = "swedencentral"
  gateway_api_version             = "2024-10-21"
  gateway_api_management_tier     = "Developer"
  gateway_model_deployment        = "gpt-demo-test"
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

run "private_gateway_requires_complete_configuration_and_blocks_public_or_direct_model_access" {
  command = plan

  assert {
    condition     = azurerm_api_management.gateway.public_network_access_enabled == false && azurerm_api_management.gateway.virtual_network_type == "Internal" && azurerm_api_management.gateway.identity[0].type == "SystemAssigned"
    error_message = "The gateway must be internal, private, and use its managed identity."
  }
  assert {
    condition     = azurerm_api_management_api.gateway_model.subscription_required == false && azurerm_cognitive_account.model.public_network_access_enabled == false && azurerm_cognitive_account.model.local_auth_enabled == false
    error_message = "Gateway calls must require no subscription key and direct model public or key access must be disabled."
  }
  assert {
    condition     = azurerm_api_management_api_operation.gateway_chat_completions.url_template == "deployments/${var.gateway_model_deployment}/chat/completions?api-version=${var.gateway_api_version}" && can(regex("validate-jwt", azurerm_api_management_api_operation_policy.gateway_chat_completions.xml_content)) && can(regex(var.gateway_approved_application_identity, azurerm_api_management_api_operation_policy.gateway_chat_completions.xml_content)) && can(regex(var.gateway_audience, azurerm_api_management_api_operation_policy.gateway_chat_completions.xml_content)) && can(regex("authentication-managed-identity", azurerm_api_management_api_operation_policy.gateway_chat_completions.xml_content))
    error_message = "The operation and policy must pin deployment/API version and validate the approved caller before managed-identity backend forwarding."
  }
  assert {
    condition     = azurerm_api_management_api_operation.gateway_chat_completions.method == "POST" && !can(regex("(?i)(subscription-key|subscription_key)", azurerm_api_management_api_operation_policy.gateway_chat_completions.xml_content)) && output.model_endpoint == var.gateway_endpoint
    error_message = "The route must accept POST client paths without subscription authentication or secret output."
  }
}

run "M-13_ingress_and_egress_allowlists" {
  command = plan

  assert {
    condition     = toset([for r in values(azurerm_network_security_rule.ingress_allow) : r.source_address_prefix]) == toset(["203.0.113.0/24"]) && length(azurerm_network_security_rule.ingress_allow) == 1 && azurerm_network_security_rule.ingress_deny_all.access == "Deny"
    error_message = "The complete inbound allow-rule set must equal the approved presenter/reviewer CIDRs, and inbound must end in deny-all."
  }
  assert {
    condition     = alltrue([for r in values(azurerm_network_security_rule.ingress_allow) : r.access == "Allow" && r.direction == "Inbound" && r.destination_port_range == "443" && r.destination_address_prefix == "10.60.1.0/24"])
    error_message = "Every inbound allow rule must be Allow on TCP 443 into the app subnet only."
  }
  assert {
    condition     = toset([for r in values(azurerm_network_security_rule.egress_allow) : r.destination_address_prefix]) == toset(["198.51.100.0/24"]) && length(azurerm_network_security_rule.egress_allow) == 1 && azurerm_network_security_rule.egress_deny_all.access == "Deny"
    error_message = "Only the approved egress destination may be allowed and outbound must end in deny-all."
  }
  # Azure's default AllowInternetOutBound sits at priority 65000; the deny-all must be
  # outbound, match every destination, and rank above that default (lower number wins).
  assert {
    condition     = azurerm_network_security_rule.egress_deny_all.direction == "Outbound" && azurerm_network_security_rule.egress_deny_all.destination_address_prefix == "*" && azurerm_network_security_rule.egress_deny_all.priority == 4096 && azurerm_network_security_rule.egress_deny_all.priority < 65000
    error_message = "Outbound deny-all must match all destinations at priority 4096, ahead of Azure's default outbound allow (65000)."
  }
  assert {
    condition     = alltrue([for r in values(azurerm_network_security_rule.egress_allow) : r.priority < azurerm_network_security_rule.egress_deny_all.priority])
    error_message = "Every explicit egress allow rule must have higher precedence than the deny-all."
  }
  # M-13/M-18: app-to-private-endpoint traffic needs this outbound allow, ahead of the deny-all.
  assert {
    condition     = azurerm_network_security_rule.egress_private_endpoints.direction == "Outbound" && azurerm_network_security_rule.egress_private_endpoints.access == "Allow" && azurerm_network_security_rule.egress_private_endpoints.protocol == "Tcp" && azurerm_network_security_rule.egress_private_endpoints.destination_port_range == "443" && azurerm_network_security_rule.egress_private_endpoints.source_address_prefix == "10.60.1.0/24" && azurerm_network_security_rule.egress_private_endpoints.destination_address_prefix == "10.60.2.0/24" && azurerm_network_security_rule.egress_private_endpoints.priority < azurerm_network_security_rule.egress_deny_all.priority
    error_message = "App subnet must have an outbound Allow on TCP 443 to the private endpoint subnet, with higher precedence than the outbound deny-all."
  }
}

run "M-13_ingress_allows_exactly_configured_cidrs" {
  command = plan

  variables {
    presenter_reviewer_cidrs = ["203.0.113.0/24", "192.0.2.0/24"]
  }

  assert {
    condition     = toset([for r in values(azurerm_network_security_rule.ingress_allow) : r.source_address_prefix]) == toset(["203.0.113.0/24", "192.0.2.0/24"]) && length(azurerm_network_security_rule.ingress_allow) == 2
    error_message = "Inbound allow rules must be exactly the configured presenter/reviewer CIDRs; any other source must be denied."
  }
}

run "M-13_app_subnet_uses_the_nsg" {
  command = plan

  assert {
    condition     = azurerm_subnet_network_security_group_association.app != null && can(regex("(?s)resource \"azurerm_subnet_network_security_group_association\" \"app\" \\{\\s*subnet_id\\s*=\\s*azurerm_subnet\\.app\\.id\\s*network_security_group_id\\s*=\\s*azurerm_network_security_group\\.app\\.id\\s*\\}", file("main.tf")))
    error_message = "The app subnet must be associated with the app NSG (subnet_id = azurerm_subnet.app.id, network_security_group_id = azurerm_network_security_group.app.id)."
  }
}

run "M-13_empty_allowlists_deny_everything" {
  command = plan

  variables {
    presenter_reviewer_cidrs    = []
    egress_allowed_destinations = []
  }

  assert {
    condition     = length(azurerm_network_security_rule.ingress_allow) == 0 && length(azurerm_network_security_rule.egress_allow) == 0
    error_message = "Empty allowlists must create no inbound or outbound allow rules."
  }
  assert {
    condition     = azurerm_network_security_rule.ingress_deny_all.access == "Deny" && azurerm_network_security_rule.ingress_deny_all.direction == "Inbound" && azurerm_network_security_rule.ingress_deny_all.priority == 4096
    error_message = "Empty presenter/reviewer allowlist must still leave the inbound deny-all rule in place."
  }
  assert {
    condition     = azurerm_network_security_rule.egress_deny_all.access == "Deny" && azurerm_network_security_rule.egress_deny_all.direction == "Outbound" && azurerm_network_security_rule.egress_deny_all.priority == 4096
    error_message = "Empty egress allowlist must still leave the outbound deny-all rule in place."
  }
}

run "M-13_data_endpoints_are_private" {
  command = plan

  assert {
    condition     = azurerm_mssql_server.sql.public_network_access_enabled == false && azurerm_storage_account.blob.public_network_access_enabled == false && azurerm_cognitive_account.model.public_network_access_enabled == false && azurerm_cognitive_account.model.local_auth_enabled == false
    error_message = "SQL, Blob and model public access must be disabled, and model key auth off."
  }
  assert {
    condition     = alltrue([for k in ["sql", "blob", "model"] : contains(keys(azurerm_private_endpoint.demo), k)])
    error_message = "SQL, Blob and model each need a private endpoint."
  }
  assert {
    condition     = azurerm_private_endpoint.demo["sql"].private_service_connection[0].subresource_names[0] == "sqlServer"
    error_message = "SQL private endpoint must connect through the sqlServer subresource."
  }
  assert {
    condition     = azurerm_private_endpoint.demo["blob"].private_service_connection[0].subresource_names[0] == "blob"
    error_message = "Blob private endpoint must connect through the blob subresource."
  }
  assert {
    condition     = azurerm_private_endpoint.demo["model"].private_service_connection[0].subresource_names[0] == "account"
    error_message = "Model private endpoint must connect through the account subresource."
  }
  assert {
    condition     = azurerm_subnet.private_endpoints.address_prefixes[0] == "10.60.2.0/24" && length(azurerm_private_endpoint.demo) == 5
    error_message = "Private endpoints must be created for SQL, Blob, model, Key Vault and ACR in the configured private endpoint subnet."
  }
  assert {
    condition     = azurerm_private_dns_zone.demo["model"].name == "privatelink.openai.azure.com" && azurerm_private_dns_zone_virtual_network_link.demo["model"].private_dns_zone_name == "privatelink.openai.azure.com" && azurerm_private_dns_zone_virtual_network_link.demo["model"].name == "link-model"
    error_message = "The model private DNS zone must be privatelink.openai.azure.com with a VNet link named link-model."
  }
  assert {
    condition     = azurerm_private_endpoint.demo["model"].private_dns_zone_group[0].name == "dzg-model" && length(azurerm_private_endpoint.demo["model"].private_dns_zone_group[0].private_dns_zone_ids) == 1
    error_message = "The model endpoint must register in its DNS zone group with exactly one private DNS zone."
  }
  assert {
    condition     = local.private_endpoints.model.zone == "model" && can(regex("(?s)private_dns_zone_ids\\s*=\\s*\\[azurerm_private_dns_zone\\.demo\\[each\\.value\\.zone\\]\\.id\\]", file("main.tf")))
    error_message = "The model endpoint's DNS zone group must reference the model privatelink.openai.azure.com zone."
  }
  assert {
    condition     = local.private_dns_zones.model == "privatelink.openai.azure.com" && can(regex("(?s)resource \"azurerm_private_dns_zone_virtual_network_link\" \"demo\" \\{.*virtual_network_id\\s*=\\s*azurerm_virtual_network\\.demo\\.id", file("main.tf")))
    error_message = "The model privatelink zone must be linked to the demo VNet."
  }
  assert {
    condition     = local.private_endpoints.model.subresource == "account" && local.private_dns_zones[local.private_endpoints.model.zone] == "privatelink.openai.azure.com" && length(azurerm_subnet.private_endpoints.address_prefixes) == 1
    error_message = "The model private endpoint must use the account subresource and the privatelink.openai.azure.com zone, in the private endpoint subnet."
  }
  assert {
    condition     = length(local.private_endpoints) == 5 && can(regex("(?s)private_endpoints = \\{.*sql\\s*=\\s*\\{\\s*resource_id\\s*=\\s*azurerm_mssql_server\\.sql\\.id.*blob\\s*=\\s*\\{\\s*resource_id\\s*=\\s*azurerm_storage_account\\.blob\\.id.*model\\s*=\\s*\\{\\s*resource_id\\s*=\\s*azurerm_cognitive_account\\.model\\.id", file("main.tf")))
    error_message = "SQL, Blob and model private endpoints must target their own resources (SQL server, storage account, cognitive account)."
  }
  assert {
    condition     = length(azurerm_subnet.private_endpoints.address_prefixes) == 1 && can(regex("(?s)resource \"azurerm_private_endpoint\" \"demo\" \\{.*?subnet_id\\s*=\\s*azurerm_subnet\\.private_endpoints\\.id.*?private_connection_resource_id\\s*=\\s*each\\.value\\.resource_id", file("main.tf")))
    error_message = "Each private endpoint must attach to the private endpoint subnet and connect to its mapped resource."
  }
}

run "egress_and_config_no_environment_defaults" {
  command = plan

  assert {
    condition     = var.subscription_id != "" && alltrue([for n in ["subscription_id", "tenant_id", "presenter_reviewer_cidrs", "budget_amount", "budget_start_date", "budget_contact_emails", "terraform_state_backend", "location"] : !anytrue([for p in split("variable \"", file("variables.tf")) : startswith(p, "${n}\"") && can(regex("(?m)^\\s*default\\s*=", p))])])
    error_message = "Subscription, tenant, location, presenter/reviewer CIDR, budget, and state-backend inputs must not have Terraform defaults. The vnet, subnet, egress and resource-group inputs default or derive from name_prefix (see names.tftest.hcl)."
  }
}

run "backend_identity_and_locking_declared" {
  # Story criterion: Entra-auth azurerm backend with locking; no shared keys or SAS.
  command = plan

  assert {
    condition     = var.terraform_state_backend.use_azuread_auth && can(regex("(?s)backend \"azurerm\" \\{\\s*use_azuread_auth\\s*=\\s*true", file("main.tf")))
    error_message = "The azurerm backend block must declare use_azuread_auth = true (Entra identity)."
  }
  # The azurerm backend always takes a blob lease for state locking; the block must not opt out of it.
  assert {
    condition     = var.terraform_state_backend.locking_enabled && !can(regex("(?i)use_lease\\s*=\\s*false", file("main.tf")))
    error_message = "The azurerm backend must keep state locking (blob lease) enabled."
  }
}

run "backend_declares_no_shared_credentials" {
  command = plan

  assert {
    condition     = var.terraform_state_backend.use_azuread_auth == true && can(regex("(?s)backend \"azurerm\" \\{\\s*use_azuread_auth\\s*=\\s*true\\s*\\}", file("main.tf")))
    error_message = "The azurerm backend block must not declare shared keys, SAS tokens, client secrets, or disable Entra auth."
  }
}

run "M-14_M-18_encryption_transport_and_no_key_outputs" {
  command = plan

  assert {
    condition     = azurerm_storage_account.blob.infrastructure_encryption_enabled == true && azurerm_storage_account.blob.shared_access_key_enabled == false && azurerm_key_vault.kv.purge_protection_enabled == true
    error_message = "Blob needs infrastructure encryption and no shared keys; Key Vault needs purge protection."
  }
  assert {
    condition     = azurerm_key_vault.kv.network_acls[0].default_action == "Deny" && azurerm_key_vault.kv.public_network_access_enabled == false
    error_message = "Key Vault network ACL default action must be Deny with public access disabled."
  }
  assert {
    condition     = azurerm_mssql_server_transparent_data_encryption.sql != null && can(regex("(?s)resource \"azurerm_mssql_server_transparent_data_encryption\" \"sql\" \\{\\s*server_id\\s*=\\s*azurerm_mssql_server\\.sql\\.id", file("main.tf")))
    error_message = "SQL transparent data encryption must be enabled on the SQL server (the TDE resource must exist and target the SQL server)."
  }
  assert {
    condition     = azurerm_mssql_server.sql.minimum_tls_version == "1.2" && azurerm_mssql_server.sql.azuread_administrator[0].azuread_authentication_only == true
    error_message = "SQL must require TLS 1.2 and Entra-only auth."
  }
  assert {
    condition     = output.model_endpoint == "https://apim-demo-test.private.invalid/"
    error_message = "Model endpoint must be the approved subdomain host."
  }
  assert {
    condition     = azurerm_cognitive_account.model.local_auth_enabled == false
    error_message = "Key-based auth must be disabled on the model account."
  }
  assert {
    condition     = var.subscription_id != "" && !can(regex("(?is)output \"[^\"]*\" \\{[^}]*(AccountKey|ConnectionString|primary_access_key|primary_key|secondary_key|client_secret)", file("main.tf")))
    error_message = "No declared output may emit access keys, connection strings or secrets."
  }
}

run "rejects_anonymous_backend" {
  command = plan

  variables {
    terraform_state_backend = {
      storage_account_name = "stdemotfstate"
      container_name       = "tfstate"
      key                  = "demo.tfstate"
      use_azuread_auth     = false
      locking_enabled      = true
    }
  }

  expect_failures = [var.terraform_state_backend]
}

run "rejects_backend_without_locking" {
  command = plan

  variables {
    terraform_state_backend = {
      storage_account_name = "stdemotfstate"
      container_name       = "tfstate"
      key                  = "demo.tfstate"
      use_azuread_auth     = true
      locking_enabled      = false
    }
  }

  expect_failures = [var.terraform_state_backend]
}

run "rejects_backend_without_state_name" {
  command = plan

  variables {
    terraform_state_backend = {
      storage_account_name = ""
      container_name       = "tfstate"
      key                  = "demo.tfstate"
      use_azuread_auth     = true
      locking_enabled      = true
    }
  }

  expect_failures = [var.terraform_state_backend]
}

run "terraform_identity_scoped_to_demo_rg" {
  command = plan

  assert {
    condition     = azurerm_role_assignment.terraform_contributor.scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo-test" && azurerm_role_assignment.terraform_contributor.role_definition_name == "Contributor"
    error_message = "Terraform identity must hold Contributor scoped to the demo resource group only."
  }
  assert {
    condition     = azurerm_role_assignment.terraform_contributor.principal_id == "33333333-3333-3333-3333-333333333333"
    error_message = "Terraform role assignment must target the configured Terraform principal."
  }
}


run "rejects_empty_gateway_endpoint" {
  command = plan
  variables { gateway_endpoint = " " }
  expect_failures = [var.gateway_endpoint]
}

run "rejects_empty_gateway_region" {
  command = plan
  variables { gateway_region = " " }
  expect_failures = [var.gateway_region]
}

run "rejects_empty_gateway_api_version" {
  command = plan
  variables { gateway_api_version = " " }
  expect_failures = [var.gateway_api_version]
}

run "rejects_empty_gateway_api_management_tier" {
  command = plan
  variables { gateway_api_management_tier = " " }
  expect_failures = [var.gateway_api_management_tier]
}

run "rejects_empty_gateway_model_deployment" {
  command = plan
  variables { gateway_model_deployment = " " }
  expect_failures = [var.gateway_model_deployment]
}
