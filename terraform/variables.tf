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

variable "target_framework" {
  description = "Application target framework approved by preflight."
  type        = string
  default     = "net10.0"

  validation {
    condition     = var.target_framework == "net10.0"
    error_message = "target_framework must be net10.0 for this deployment."
  }
}

variable "container_image" {
  description = "Explicit non-secret application image reference. Image deployment is separate from infrastructure provisioning."
  type        = string

  validation {
    condition     = length(trimspace(var.container_image)) > 0 && !strcontains(var.container_image, "@")
    error_message = "container_image must be an explicit tagged image reference, not a digest or empty value."
  }
}

variable "container_port" {
  description = "Container Apps ingress target port exposed by the application."
  type        = number
  default     = 8080

  validation {
    condition     = var.container_port >= 1 && var.container_port <= 65535
    error_message = "container_port must be a valid TCP port."
  }
}

variable "container_allowed_source_ranges" {
  description = "Explicit public ingress CIDR allow-list for the Container App."
  type        = list(string)

  validation {
    condition     = length(var.container_allowed_source_ranges) > 0 && alltrue([for ip in var.container_allowed_source_ranges : can(cidrhost(ip, 0))])
    error_message = "container_allowed_source_ranges must contain at least one valid CIDR range."
  }
}

variable "container_min_replicas" {
  description = "Minimum Container App replica count; zero preserves scale-to-zero."
  type        = number
  default     = 0

  validation {
    condition     = var.container_min_replicas == 0
    error_message = "container_min_replicas must remain zero for the approved consumption workload."
  }
}

variable "container_max_replicas" {
  description = "Maximum Container App replica count for the bounded demonstrator."
  type        = number
  default     = 1

  validation {
    condition     = var.container_max_replicas == 1
    error_message = "container_max_replicas must remain one for the bounded demonstrator."
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
