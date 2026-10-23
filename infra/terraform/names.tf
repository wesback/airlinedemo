# Resource names and subnet ranges derived from name_prefix; an explicitly set variable always wins.
locals {
  name_prefix = coalesce(var.name_prefix, "demo")
  # Stable per subscription and prefix; keeps globally unique names (storage, vault, registry, SQL, model) from colliding.
  name_suffix = substr(sha1("${var.subscription_id}/${local.name_prefix}"), 0, 6)

  resource_group_name             = coalesce(var.resource_group_name, "rg-${local.name_prefix}")
  sql_server_name                 = coalesce(var.sql_server_name, "sql-${local.name_prefix}-${local.name_suffix}")
  sql_database_name               = var.sql_database_name
  storage_account_name            = coalesce(var.storage_account_name, "st${local.name_prefix}${local.name_suffix}")
  key_vault_name                  = coalesce(var.key_vault_name, "kv-${local.name_prefix}-${local.name_suffix}")
  model_account_name              = coalesce(var.model_account_name, "aoai-${local.name_prefix}-${local.name_suffix}")
  model_endpoint_subdomain        = coalesce(var.model_endpoint_subdomain, "aoai-${local.name_prefix}-${local.name_suffix}")
  model_deployment_name           = var.model_deployment_name != null ? var.model_deployment_name : "gpt-${local.name_prefix}"
  acr_name                        = coalesce(var.acr_name, "acr${local.name_prefix}${local.name_suffix}")
  container_apps_environment_name = coalesce(var.container_apps_environment_name, "cae-${local.name_prefix}")
  container_app_name              = coalesce(var.container_app_name, "ca-${local.name_prefix}")

  app_subnet_cidr              = coalesce(var.app_subnet_cidr, cidrsubnet(var.vnet_address_space[0], 8, 1))
  private_endpoint_subnet_cidr = coalesce(var.private_endpoint_subnet_cidr, cidrsubnet(var.vnet_address_space[0], 8, 2))
}
