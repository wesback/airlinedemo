# Credential-free example from deployment/preflight.json.
# The region is repeated deliberately so a regional override is visible in
# configuration rather than being an implicit deployment choice.
deployment_name     = "airlinedemo-swc-demo"
resource_group_name = "rg-airlinedemo-swc-demo"
region              = "swedencentral"
environment         = "demo"
owner               = "airlinedemo"
cost_center         = "airlinedemo-demo"

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

# These values remain required private inputs until the readiness gate
# confirms the exact model/version in Sweden Central. Terraform has no model
# fallback.
# azure_openai_model         = "confirmed-model-name"
# azure_openai_model_version = "confirmed-model-version"

# These values are supplied by the separately owned Entra/app prerequisite.
# sql_aad_admin_login     = "private-entra-admin-login"
# sql_aad_admin_object_id = "00000000-0000-0000-0000-000000000000"

# Keep the public cognitive endpoints firewall restricted. Add only approved
# operator ranges for a disposable synthetic-data rehearsal.
# cognitive_allowed_ip_ranges = ["203.0.113.10/32"]

# Keep the public Functions endpoint deny-by-default. Add only approved
# operator ranges for a disposable synthetic-data rehearsal.
# function_allowed_ip_ranges = ["203.0.113.10/32"]
