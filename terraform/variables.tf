variable "deployment_name" {
  description = "Stable demo deployment name from the approved preflight record."
  type        = string

  validation {
    condition     = can(regex("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$", var.deployment_name))
    error_message = "deployment_name must be 3-64 characters, lowercase, and use only letters, numbers, and hyphens."
  }
}

variable "resource_group_name" {
  description = "Resource group owned by this demo deployment."
  type        = string

  validation {
    condition     = can(regex("^[A-Za-z0-9][A-Za-z0-9._()-]{0,89}$", var.resource_group_name))
    error_message = "resource_group_name must be 1-90 characters and use Azure resource-group name characters."
  }
}

variable "environment" {
  description = "Lifecycle environment tag for resources owned by this deployment."
  type        = string
  default     = "demo"

  validation {
    condition     = var.environment == "demo"
    error_message = "environment must remain demo for this deployment."
  }
}

variable "owner" {
  description = "Owning team tag for resources in the disposable demo boundary."
  type        = string
  default     = "airlinedemo"

  validation {
    condition     = can(regex("^[A-Za-z0-9][A-Za-z0-9 ._-]{1,62}[A-Za-z0-9]$", var.owner))
    error_message = "owner must be 3-64 characters and use only letters, numbers, spaces, dots, underscores, and hyphens."
  }
}

variable "cost_center" {
  description = "Cost allocation tag for resources in the disposable demo boundary."
  type        = string
  default     = "airlinedemo-demo"

  validation {
    condition     = can(regex("^[A-Za-z0-9][A-Za-z0-9 ._-]{1,62}[A-Za-z0-9]$", var.cost_center))
    error_message = "cost_center must be 3-64 characters and use only letters, numbers, spaces, dots, underscores, and hyphens."
  }
}

variable "region" {
  description = "Azure deployment region. Changing the default is an explicit deployment decision."
  type        = string
  default     = "swedencentral"

  validation {
    condition     = can(regex("^[a-z0-9]+(?:[a-z0-9-]*[a-z0-9]+)?$", var.region))
    error_message = "region must be a lowercase Azure region name."
  }
}

variable "function_runtime" {
  description = "Azure Functions runtime generation approved by preflight."
  type        = string
  default     = "Azure Functions v4"

  validation {
    condition     = var.function_runtime == "Azure Functions v4"
    error_message = "function_runtime must remain Azure Functions v4 for this deployment."
  }
}

variable "function_worker_model" {
  description = "Azure Functions worker model approved by preflight."
  type        = string
  default     = "dotnet-isolated"

  validation {
    condition     = var.function_worker_model == "dotnet-isolated"
    error_message = "function_worker_model must be dotnet-isolated for this deployment."
  }
}

variable "target_framework" {
  description = "Application target framework approved by preflight."
  type        = string
  default     = "net10.0"

  validation {
    condition     = var.target_framework == "net10.0"
    error_message = "target_framework must be net10.0 for this deployment."
  }
}

variable "hosting_plan" {
  description = "Azure Functions hosting plan approved by preflight."
  type        = string
  default     = "Flex Consumption"

  validation {
    condition     = var.hosting_plan == "Flex Consumption"
    error_message = "hosting_plan must be Flex Consumption for this deployment."
  }
}

variable "durable_storage_kind" {
  description = "Azure Storage kind for the Durable Functions backend."
  type        = string
  default     = "StorageV2"

  validation {
    condition     = var.durable_storage_kind == "StorageV2"
    error_message = "durable_storage_kind must be StorageV2 for this deployment."
  }
}

variable "durable_backend" {
  description = "Durable Functions backend approved by preflight."
  type        = string
  default     = "Azure Storage"

  validation {
    condition     = var.durable_backend == "Azure Storage"
    error_message = "durable_backend must be Azure Storage for this deployment."
  }
}

variable "sql_engine" {
  description = "Database engine approved by preflight."
  type        = string
  default     = "Azure SQL"

  validation {
    condition     = var.sql_engine == "Azure SQL"
    error_message = "sql_engine must be Azure SQL for this deployment."
  }
}

variable "sql_sku" {
  description = "Azure SQL service tier approved by preflight."
  type        = string
  default     = "Serverless General Purpose"

  validation {
    condition     = var.sql_sku == "Serverless General Purpose"
    error_message = "sql_sku must be Serverless General Purpose for this deployment."
  }
}

variable "sql_authentication" {
  description = "Database authentication boundary approved by preflight."
  type        = string
  default     = "system-assigned managed identity"

  validation {
    condition     = var.sql_authentication == "system-assigned managed identity"
    error_message = "sql_authentication must use a system-assigned managed identity."
  }
}

variable "sql_endpoint_access" {
  description = "SQL endpoint exposure boundary approved by preflight."
  type        = string
  default     = "firewall-restricted"

  validation {
    condition     = var.sql_endpoint_access == "firewall-restricted"
    error_message = "sql_endpoint_access must remain firewall-restricted."
  }
}

variable "sql_endpoint_type" {
  description = "SQL endpoint type approved by preflight."
  type        = string
  default     = "public"

  validation {
    condition     = var.sql_endpoint_type == "public"
    error_message = "sql_endpoint_type must be public for this deployment."
  }
}

variable "evidence_retention_days" {
  description = "Evidence blob-version retention period for synthetic demonstrator data."
  type        = number
  default     = 90

  validation {
    condition     = var.evidence_retention_days == 90
    error_message = "evidence_retention_days must be 90 for this approved synthetic-data configuration."
  }
}

variable "monitoring_retention_days" {
  description = "Application Insights and Log Analytics retention period."
  type        = number
  default     = 90

  validation {
    condition     = var.monitoring_retention_days == 90
    error_message = "monitoring_retention_days must be 90 for this approved configuration."
  }
}

variable "fixture_retention_days" {
  description = "Synthetic fixture-data retention period."
  type        = number
  default     = 90

  validation {
    condition     = var.fixture_retention_days == 90
    error_message = "fixture_retention_days must be 90 for this approved configuration."
  }
}

variable "azure_openai_deployment_type" {
  description = "Azure OpenAI deployment type approved by preflight."
  type        = string
  default     = "Standard"

  validation {
    condition     = var.azure_openai_deployment_type == "Standard"
    error_message = "azure_openai_deployment_type must be Standard."
  }
}

variable "azure_openai_model" {
  description = "Exact Azure OpenAI model name confirmed by the deployment readiness gate."
  type        = string

  validation {
    condition     = length(trimspace(var.azure_openai_model)) > 0
    error_message = "azure_openai_model must be explicitly supplied; Terraform must not select a model."
  }
}

variable "azure_openai_model_version" {
  description = "Exact Azure OpenAI model version confirmed by the deployment readiness gate."
  type        = string

  validation {
    condition     = length(trimspace(var.azure_openai_model_version)) > 0
    error_message = "azure_openai_model_version must be explicitly supplied; Terraform must not select a default version."
  }
}

variable "azure_openai_quota_tokens_minute" {
  description = "Azure OpenAI quota reserved by the approved preflight record."
  type        = number
  default     = 30000

  validation {
    condition     = var.azure_openai_quota_tokens_minute == 30000
    error_message = "azure_openai_quota_tokens_minute must be 30000 for this configuration."
  }
}

variable "sql_aad_admin_login" {
  description = "Existing Entra administrator login supplied privately for the SQL server prerequisite."
  type        = string
  sensitive   = true

  validation {
    condition     = length(trimspace(var.sql_aad_admin_login)) > 0
    error_message = "sql_aad_admin_login must be supplied by the separately owned identity prerequisite."
  }
}

variable "sql_aad_admin_object_id" {
  description = "Existing Entra administrator object ID supplied privately for the SQL server prerequisite."
  type        = string
  sensitive   = true

  validation {
    condition     = can(regex("^[0-9a-fA-F-]{36}$", var.sql_aad_admin_object_id))
    error_message = "sql_aad_admin_object_id must be a GUID supplied by the separately owned identity prerequisite."
  }
}

variable "cognitive_allowed_ip_ranges" {
  description = "Explicit public firewall allow-list for Document Intelligence and Azure OpenAI. Empty denies public data-plane access."
  type        = list(string)
  default     = []

  validation {
    condition     = alltrue([for ip in var.cognitive_allowed_ip_ranges : can(cidrhost(ip, 0))])
    error_message = "cognitive_allowed_ip_ranges must contain valid CIDR ranges."
  }
}

variable "function_allowed_ip_ranges" {
  description = "Explicit public firewall allow-list for the Function App. Empty denies public endpoint access."
  type        = list(string)
  default     = []

  validation {
    condition     = alltrue([for ip in var.function_allowed_ip_ranges : can(cidrhost(ip, 0))])
    error_message = "function_allowed_ip_ranges must contain valid CIDR ranges."
  }
}
