locals {
  common_tags = {
    deployment  = var.deployment_name
    environment = var.environment
    owner       = var.owner
    cost_center = var.cost_center
    managed_by  = "terraform"
  }

  application_deployment = {
    deployment_name                        = var.deployment_name
    resource_group_name                    = var.resource_group_name
    region                                 = var.region
    resource_group_id                      = module.demo_boundary.resource_group_id
    runtime_host_storage_account_id        = module.demo_boundary.runtime_host_storage_account_id
    runtime_host_storage_account_name      = module.demo_boundary.runtime_host_storage_account_name
    evidence_storage_account_id            = module.demo_boundary.evidence_storage_account_id
    evidence_storage_account_name          = module.demo_boundary.evidence_storage_account_name
    application_insights_id                = module.observability.application_insights_id
    log_analytics_workspace_id             = module.observability.log_analytics_workspace_id
    function_runtime                       = var.function_runtime
    function_worker_model                  = var.function_worker_model
    target_framework                       = var.target_framework
    hosting_plan                           = var.hosting_plan
    durable_backend                        = var.durable_backend
    durable_storage_kind                   = var.durable_storage_kind
    sql_engine                             = var.sql_engine
    sql_sku                                = var.sql_sku
    sql_authentication                     = var.sql_authentication
    sql_endpoint_type                      = var.sql_endpoint_type
    sql_endpoint_access                    = var.sql_endpoint_access
    evidence_retention_days                = var.evidence_retention_days
    monitoring_retention_days              = var.monitoring_retention_days
    fixture_retention_days                 = var.fixture_retention_days
    azure_openai_deployment_type           = var.azure_openai_deployment_type
    azure_openai_model                     = var.azure_openai_model
    azure_openai_model_version             = var.azure_openai_model_version
    azure_openai_quota_tokens_minute       = var.azure_openai_quota_tokens_minute
    function_app_id                        = module.functions.function_app_id
    function_app_endpoint                  = module.functions.function_app_endpoint
    sql_server_id                          = module.sql.sql_server_id
    sql_server_fully_qualified_domain_name = module.sql.sql_server_fully_qualified_domain_name
    sql_database_id                        = module.sql.sql_database_id
    document_intelligence_id               = module.document_intelligence.account_id
    document_intelligence_endpoint         = module.document_intelligence.endpoint
    azure_openai_id                        = module.ai.account_id
    azure_openai_endpoint                  = module.ai.endpoint
    azure_openai_deployment_id             = module.ai.deployment_id
    azure_openai_deployment_name           = module.ai.deployment_name
  }
}

module "demo_boundary" {
  source = "./modules/demo-boundary"

  deployment_name         = var.deployment_name
  resource_group_name     = var.resource_group_name
  region                  = var.region
  evidence_retention_days = var.evidence_retention_days
  tags                    = local.common_tags
}

module "observability" {
  source = "./modules/observability"

  deployment_name     = var.deployment_name
  resource_group_name = module.demo_boundary.resource_group_name
  region              = var.region
  retention_days      = var.monitoring_retention_days
  tags                = local.common_tags
}

module "functions" {
  source = "./modules/functions"

  deployment_name                   = var.deployment_name
  resource_group_name               = module.demo_boundary.resource_group_name
  region                            = var.region
  function_runtime                  = var.function_runtime
  function_worker_model             = var.function_worker_model
  target_framework                  = var.target_framework
  hosting_plan                      = var.hosting_plan
  durable_backend                   = var.durable_backend
  durable_storage_kind              = var.durable_storage_kind
  runtime_host_storage_account_id   = module.demo_boundary.runtime_host_storage_account_id
  runtime_host_storage_account_name = module.demo_boundary.runtime_host_storage_account_name
  application_insights_id           = module.observability.application_insights_id
  allowed_ip_ranges                 = var.function_allowed_ip_ranges
  tags                              = local.common_tags
}

module "sql" {
  source = "./modules/sql"

  deployment_name         = var.deployment_name
  resource_group_name     = module.demo_boundary.resource_group_name
  region                  = var.region
  sql_engine              = var.sql_engine
  sql_sku                 = var.sql_sku
  sql_authentication      = var.sql_authentication
  sql_endpoint_type       = var.sql_endpoint_type
  sql_endpoint_access     = var.sql_endpoint_access
  sql_aad_admin_login     = var.sql_aad_admin_login
  sql_aad_admin_object_id = var.sql_aad_admin_object_id
  tags                    = local.common_tags
}

module "document_intelligence" {
  source = "./modules/document-intelligence"

  deployment_name     = var.deployment_name
  resource_group_name = module.demo_boundary.resource_group_name
  region              = var.region
  allowed_ip_ranges   = var.cognitive_allowed_ip_ranges
  tags                = local.common_tags
}

module "ai" {
  source = "./modules/ai"

  deployment_name         = var.deployment_name
  resource_group_name     = module.demo_boundary.resource_group_name
  region                  = var.region
  model_name              = var.azure_openai_model
  model_version           = var.azure_openai_model_version
  deployment_type         = var.azure_openai_deployment_type
  quota_tokens_per_minute = var.azure_openai_quota_tokens_minute
  allowed_ip_ranges       = var.cognitive_allowed_ip_ranges
  tags                    = local.common_tags
}
