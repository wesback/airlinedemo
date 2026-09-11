# Credential-free example from deployment/preflight.json.
# The region is repeated deliberately so a regional override is visible in
# configuration rather than being an implicit deployment choice.
deployment_name     = "airlinedemo-swc-demo"
resource_group_name = "rg-airlinedemo-swc-demo"
region              = "swedencentral"

# These values mirror the approved non-secret application boundary.
function_runtime                 = "Azure Functions v4"
function_worker_model            = "dotnet-isolated"
target_framework                 = "net10.0"
hosting_plan                     = "Flex Consumption"
durable_backend                  = "Azure Storage"
durable_storage_kind             = "StorageV2"
sql_engine                       = "Azure SQL"
sql_sku                          = "Serverless General Purpose"
sql_authentication               = "system-assigned managed identity"
sql_endpoint_type                = "public"
sql_endpoint_access              = "firewall-restricted"
evidence_retention_days          = 90
monitoring_retention_days        = 90
fixture_retention_days           = 90
azure_openai_deployment_type     = "Standard"
azure_openai_quota_tokens_minute = 30000
