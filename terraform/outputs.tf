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

output "target_framework" {
  description = "Approved application target framework."
  value       = var.target_framework
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
  description = "Non-secret application deployment settings. No resource endpoints or credentials are included in secret form; approved public endpoints are included separately."
  value       = local.application_deployment
}

output "container_app_id" {
  description = "Non-secret resource identifier for the Container App."
  value       = module.container_apps.container_app_id
}

output "container_app_registry_id" {
  description = "Non-secret resource identifier for the application container registry."
  value       = module.container_apps.container_registry_id
}

output "container_app_runtime_principal_id" {
  description = "Non-secret principal ID for the Container App system-assigned runtime identity."
  value       = module.container_apps.container_app_runtime_principal_id
}

output "migration_identity_id" {
  description = "Non-secret resource ID for the distinct Terraform-managed migration identity."
  value       = module.container_apps.migration_identity_id
}

output "migration_identity_client_id" {
  description = "Non-secret client ID for the distinct Terraform-managed migration identity."
  value       = module.container_apps.migration_identity_client_id
}

output "migration_identity_principal_id" {
  description = "Non-secret principal ID for the distinct Terraform-managed migration identity."
  value       = module.container_apps.migration_identity_principal_id
}

output "sql_server_id" {
  description = "Non-secret resource identifier for the Azure SQL logical server."
  value       = module.sql.sql_server_id
}

output "sql_server_fully_qualified_domain_name" {
  description = "Public Azure SQL server endpoint; access remains firewall restricted."
  value       = module.sql.sql_server_fully_qualified_domain_name
}

output "sql_database_id" {
  description = "Non-secret resource identifier for the workflow database."
  value       = module.sql.sql_database_id
}

output "document_intelligence_id" {
  description = "Non-secret resource identifier for Document Intelligence."
  value       = module.document_intelligence.account_id
}

output "document_intelligence_endpoint" {
  description = "Public Document Intelligence endpoint; access remains firewall restricted."
  value       = module.document_intelligence.endpoint
}

output "azure_openai_id" {
  description = "Non-secret resource identifier for the Azure OpenAI account."
  value       = module.ai.account_id
}

output "azure_openai_endpoint" {
  description = "Public Azure OpenAI endpoint; access remains firewall restricted."
  value       = module.ai.endpoint
}

output "azure_openai_deployment_id" {
  description = "Non-secret resource identifier for the approved Azure OpenAI deployment."
  value       = module.ai.deployment_id
}
