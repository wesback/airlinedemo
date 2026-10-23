mock_provider "azurerm" {}

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

# AC: MODEL-INFRA-01
run "model_account_uses_sweden_central" {
  command = plan

  assert {
    condition     = azurerm_cognitive_account.model.location == "swedencentral"
    error_message = "The model account must be located in swedencentral, independent of the resource group location."
  }

  assert {
    condition     = azurerm_resource_group.demo.location == "uksouth"
    error_message = "The model account location must not be derived from the resource group location."
  }
}

# AC: MODEL-INFRA-02
run "model_deployments_require_global_standard" {
  command = plan

  assert {
    condition     = azurerm_cognitive_deployment.model.sku[0].name == "GlobalStandard"
    error_message = "The model deployment SKU (scale type) must be GlobalStandard, not Standard, GlobalProvisionedManaged or any other value."
  }

  assert {
    condition     = azurerm_cognitive_deployment.model.name == var.model_deployment_name && can(regex("(?s)resource \"azurerm_cognitive_deployment\" \"model\" \\{.*cognitive_account_id\\s*=\\s*azurerm_cognitive_account\\.model\\.id", file("main.tf")))
    error_message = "The model deployment must belong to the model account."
  }
}

# AC: MODEL-INFRA-03
run "model_deployment_inputs_are_required" {
  command = plan

  assert {
    condition     = length(trimspace(var.model_deployment_name)) > 0 && length(trimspace(var.model_name)) > 0 && length(trimspace(var.model_version)) > 0
    error_message = "model_deployment_name, model_name and model_version must be non-empty."
  }

  assert {
    condition     = azurerm_cognitive_deployment.model.model[0].name == var.model_name && azurerm_cognitive_deployment.model.model[0].version == var.model_version
    error_message = "The model deployment must use the model_name and model_version inputs."
  }

  # Each variable block is isolated by splitting on the declaration keyword, so inline and multi-line defaults are both detected.
  assert {
    condition     = var.model_name != null && length(try(split("\nvariable ", split("variable \"model_name\"", file("variables.tf"))[1])[0], "")) > 0 && !can(regex("(?m)\\bdefault\\s*=", split("\nvariable ", split("variable \"model_name\"", file("variables.tf"))[1])[0]))
    error_message = "model_name must be a required input with no default value."
  }

  assert {
    condition     = var.model_version != null && length(try(split("\nvariable ", split("variable \"model_version\"", file("variables.tf"))[1])[0], "")) > 0 && !can(regex("(?m)\\bdefault\\s*=", split("\nvariable ", split("variable \"model_version\"", file("variables.tf"))[1])[0]))
    error_message = "model_version must be a required input with no default value."
  }
}

run "accepts_null_model_deployment_name" {
  command = plan

  variables {
    name_prefix           = "demo"
    model_deployment_name = null
  }

  assert {
    condition     = var.model_deployment_name == null
    error_message = "model_deployment_name must remain null when explicitly set to null."
  }

  assert {
    condition     = local.model_deployment_name == "gpt-demo"
    error_message = "A null model_deployment_name must derive the deployment name as gpt-demo."
  }

  assert {
    condition     = azurerm_cognitive_deployment.model.name == "gpt-demo"
    error_message = "The deployment resource must use the derived gpt-demo name."
  }
}

run "rejects_empty_model_deployment_name" {
  command = plan

  variables {
    model_deployment_name = "   "
  }

  expect_failures = [var.model_deployment_name]
}

run "rejects_empty_model_name" {
  command = plan

  variables {
    model_name = ""
  }

  expect_failures = [var.model_name]
}

run "rejects_empty_model_version" {
  command = plan

  variables {
    model_version = " "
  }

  expect_failures = [var.model_version]
}

# AC: MODEL-INFRA-04
run "model_endpoint_remains_private_and_keyless" {
  command = plan

  assert {
    condition     = azurerm_cognitive_account.model.public_network_access_enabled == false && azurerm_cognitive_account.model.local_auth_enabled == false && azurerm_cognitive_account.model.network_acls[0].default_action == "Deny"
    error_message = "The model account must disable public access and local authentication and deny by default."
  }

  assert {
    condition     = azurerm_private_endpoint.demo["model"].private_service_connection[0].subresource_names[0] == "account"
    error_message = "The model private endpoint must target the account subresource."
  }

  assert {
    condition     = azurerm_private_dns_zone.demo["model"].name == "privatelink.openai.azure.com" && azurerm_private_dns_zone_virtual_network_link.demo["model"].private_dns_zone_name == "privatelink.openai.azure.com" && can(regex("(?s)resource \"azurerm_private_dns_zone_virtual_network_link\" \"demo\" \\{.*private_dns_zone_name\\s*=\\s*azurerm_private_dns_zone\\.demo\\[each\\.key\\]\\.name.*virtual_network_id\\s*=\\s*azurerm_virtual_network\\.demo\\.id", file("main.tf")))
    error_message = "The model privatelink.openai.azure.com zone must be linked to the demo VNet."
  }

  assert {
    condition     = azurerm_private_endpoint.demo["model"].private_dns_zone_group[0].name == "dzg-model" && length(azurerm_private_endpoint.demo["model"].private_dns_zone_group[0].private_dns_zone_ids) == 1 && can(regex("(?s)private_dns_zone_group \\{\\s*name\\s*=\\s*\"dzg-[^\"]*\"\\s*private_dns_zone_ids\\s*=\\s*\\[azurerm_private_dns_zone\\.demo\\[each\\.value\\.zone\\]\\.id\\]", file("main.tf"))) && can(regex("zone\\s*=\\s*\"model\"\\s*\\}", file("main.tf")))
    error_message = "The model private endpoint must register in its DNS zone group with exactly one private DNS zone."
  }
}

# AC: MODEL-INFRA-04 (the model endpoint's zone key must be "model", and the zone group must resolve that key)
run "model_endpoint_dns_zone_group_targets_model_zone" {
  command = plan

  assert {
    condition     = azurerm_private_dns_zone.demo["model"].name == "privatelink.openai.azure.com" && can(regex("(?m)^\\s*model\\s*=\\s*\\{\\s*resource_id\\s*=\\s*azurerm_cognitive_account\\.model\\.id,\\s*subresource\\s*=\\s*\"account\",\\s*zone\\s*=\\s*\"model\"\\s*\\}", file("main.tf")))
    error_message = "The model private endpoint must be mapped to the account subresource of the model account and to the model DNS zone key."
  }

  assert {
    condition     = azurerm_private_endpoint.demo["model"].private_dns_zone_group[0].name == "dzg-model" && can(regex("(?s)private_dns_zone_group \\{\\s*name\\s*=\\s*\"dzg-\\$\\{each\\.key\\}\"\\s*private_dns_zone_ids\\s*=\\s*\\[azurerm_private_dns_zone\\.demo\\[each\\.value\\.zone\\]\\.id\\]", file("main.tf")))
    error_message = "Each private endpoint's DNS zone group must resolve its own zone key (each.value.zone) to the matching private DNS zone."
  }
}
