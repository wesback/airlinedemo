data "azurerm_storage_account" "runtime_host" {
  name                = var.runtime_host_storage_account_name
  resource_group_name = var.resource_group_name
}

resource "azurerm_storage_container" "deployment_package" {
  name                  = "function-package"
  storage_account_id    = var.runtime_host_storage_account_id
  container_access_type = "private"
}

resource "azurerm_service_plan" "flex" {
  name                = "plan-${var.deployment_name}"
  resource_group_name = var.resource_group_name
  location            = var.region
  os_type             = "Linux"
  sku_name            = "FC1"
  tags                = var.tags
}

resource "azurerm_function_app_flex_consumption" "this" {
  name                = "func-${var.deployment_name}"
  resource_group_name = var.resource_group_name
  location            = var.region
  service_plan_id     = azurerm_service_plan.flex.id

  storage_container_type      = "blobContainer"
  storage_container_endpoint  = "${data.azurerm_storage_account.runtime_host.primary_blob_endpoint}${azurerm_storage_container.deployment_package.name}"
  storage_authentication_type = "StorageAccountConnectionString"
  # The provider maps this key to AzureWebJobsStorage, the Azure Storage
  # dependency used by the Durable Functions extension.
  storage_access_key = data.azurerm_storage_account.runtime_host.primary_access_key

  runtime_name                  = var.function_worker_model
  runtime_version               = "10.0"
  maximum_instance_count        = 10
  instance_memory_in_mb         = 512
  https_only                    = true
  public_network_access_enabled = true

  app_settings = {
    FUNCTIONS_EXTENSION_VERSION        = "~4"
    FUNCTIONS_WORKER_RUNTIME           = var.function_worker_model
    FUNCTIONS_WORKER_RUNTIME_VERSION   = "10.0"
    DURABLE_TASKS_STORAGE_PROVIDER     = var.durable_backend
    DURABLE_TASKS_STORAGE_ACCOUNT_KIND = var.durable_storage_kind
    APPLICATIONINSIGHTS_RESOURCE_ID    = var.application_insights_id
  }

  identity {
    type = "SystemAssigned"
  }

  site_config {
    ip_restriction_default_action     = "Deny"
    scm_ip_restriction_default_action = "Deny"
    scm_use_main_ip_restriction       = true

    dynamic "ip_restriction" {
      for_each = var.allowed_ip_ranges

      content {
        name        = format("approved-demo-%03d", ip_restriction.key + 1)
        action      = "Allow"
        ip_address  = ip_restriction.value
        priority    = 100 + ip_restriction.key
        description = "Approved disposable synthetic-data demo source"
      }
    }
  }
  tags = var.tags
}
