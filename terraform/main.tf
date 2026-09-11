locals {
  application_deployment = {
    deployment_name                  = var.deployment_name
    resource_group_name              = var.resource_group_name
    region                           = var.region
    function_runtime                 = var.function_runtime
    function_worker_model            = var.function_worker_model
    target_framework                 = var.target_framework
    hosting_plan                     = var.hosting_plan
    durable_backend                  = var.durable_backend
    durable_storage_kind             = var.durable_storage_kind
    sql_engine                       = var.sql_engine
    sql_sku                          = var.sql_sku
    sql_authentication               = var.sql_authentication
    sql_endpoint_type                = var.sql_endpoint_type
    sql_endpoint_access              = var.sql_endpoint_access
    evidence_retention_days          = var.evidence_retention_days
    monitoring_retention_days        = var.monitoring_retention_days
    fixture_retention_days           = var.fixture_retention_days
    azure_openai_deployment_type     = var.azure_openai_deployment_type
    azure_openai_quota_tokens_minute = var.azure_openai_quota_tokens_minute
  }
}
