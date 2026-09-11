output "deployment_name" {
  description = "Approved deployment name for later application deployment."
  value       = var.deployment_name
}

output "resource_group_name" {
  description = "Approved resource group name for later application deployment."
  value       = var.resource_group_name
}

output "region" {
  description = "Approved Azure region for later application deployment."
  value       = var.region
}

output "resource_group_id" {
  description = "Non-secret resource identifier for the dedicated demo resource group."
  value       = module.demo_boundary.resource_group_id
}

output "runtime_host_storage_account_id" {
  description = "Non-secret resource identifier for Durable Functions host/task-hub storage."
  value       = module.demo_boundary.runtime_host_storage_account_id
}

output "runtime_host_storage_account_name" {
  description = "Non-secret name of the Durable Functions host/task-hub storage account."
  value       = module.demo_boundary.runtime_host_storage_account_name
}

output "evidence_storage_account_id" {
  description = "Non-secret resource identifier for private evidence storage."
  value       = module.demo_boundary.evidence_storage_account_id
}

output "evidence_storage_account_name" {
  description = "Non-secret name of private evidence storage."
  value       = module.demo_boundary.evidence_storage_account_name
}

output "application_insights_id" {
  description = "Non-secret Application Insights resource identifier."
  value       = module.observability.application_insights_id
}

output "log_analytics_workspace_id" {
  description = "Non-secret Log Analytics workspace resource identifier."
  value       = module.observability.log_analytics_workspace_id
}

output "function_runtime" {
  description = "Approved Azure Functions runtime boundary."
  value       = var.function_runtime
}

output "function_worker_model" {
  description = "Approved Azure Functions worker model."
  value       = var.function_worker_model
}

output "target_framework" {
  description = "Approved application target framework."
  value       = var.target_framework
}

output "durable_storage_kind" {
  description = "Approved Durable Functions storage kind."
  value       = var.durable_storage_kind
}

output "sql_authentication" {
  description = "Approved SQL authentication boundary."
  value       = var.sql_authentication
}

output "evidence_retention_days" {
  description = "Approved evidence-version retention period."
  value       = var.evidence_retention_days
}

output "monitoring_retention_days" {
  description = "Approved application monitoring retention period."
  value       = var.monitoring_retention_days
}

output "fixture_retention_days" {
  description = "Approved synthetic fixture-data retention period."
  value       = var.fixture_retention_days
}

output "application_deployment" {
  description = "Non-secret application deployment settings. No resource endpoints or credentials are included."
  value       = local.application_deployment
}
