variable "name_prefix" {
  description = "Short lowercase prefix (3-10 letters or digits) from which every unset resource name is derived. Required unless every derived name is set."
  type        = string
  default     = null
  validation {
    condition = (var.name_prefix == null ? false : can(regex("^[a-z][a-z0-9]{2,9}$", var.name_prefix))) || alltrue([
      for v in [var.resource_group_name, var.sql_server_name, var.storage_account_name, var.key_vault_name, var.model_account_name,
      var.model_endpoint_subdomain, var.model_deployment_name, var.acr_name, var.container_apps_environment_name, var.container_app_name] : v != null
    ])
    error_message = "name_prefix must be 3-10 lowercase letters or digits starting with a letter, unless every derived resource name is set explicitly."
  }
}

variable "subscription_id" {
  type     = string
  nullable = false
}

variable "tenant_id" {
  type     = string
  nullable = false
}

variable "location" {
  type = string
}

variable "resource_group_name" {
  description = "Optional. Derived from name_prefix when not set."
  type        = string
}

variable "vnet_address_space" {
  description = "Virtual network range. Both subnets default to /24 slices of the first entry."
  type        = list(string)
}

variable "app_subnet_cidr" {
  description = "Optional. Defaults to the second /24 of the first vnet_address_space entry."
  type        = string
}

variable "private_endpoint_subnet_cidr" {
  description = "Optional. Defaults to the third /24 of the first vnet_address_space entry."
  type        = string
}

variable "presenter_reviewer_cidrs" {
  type     = list(string)
  nullable = false
  validation {
    condition     = alltrue([for c in var.presenter_reviewer_cidrs : can(cidrnetmask(c))])
    error_message = "presenter_reviewer_cidrs entries must be CIDR blocks."
  }
}

variable "egress_allowed_destinations" {
  description = "Allowed egress CIDRs. Empty (the default) allows none."
  type        = list(string)
  nullable    = false
  validation {
    condition     = alltrue([for c in var.egress_allowed_destinations : can(cidrnetmask(c))])
    error_message = "egress_allowed_destinations entries must be CIDR blocks."
  }
}

variable "sql_database_name" {
  description = "SQL database name."
  type        = string
}

variable "sql_server_name" {
  description = "Optional. Derived from name_prefix when not set."
  type        = string
}

variable "sql_admin_group_name" {
  type = string
}

variable "sql_admin_group_object_id" {
  type = string
}

variable "storage_account_name" {
  description = "Optional. Derived from name_prefix when not set."
  type        = string
}

variable "key_vault_name" {
  description = "Optional. Derived from name_prefix when not set."
  type        = string
}

variable "model_account_name" {
  description = "Optional. Derived from name_prefix when not set."
  type        = string
}

variable "model_endpoint_subdomain" {
  description = "Optional. Derived from name_prefix when not set."
  type        = string
}

variable "model_deployment_name" {
  description = "Name of the Azure OpenAI deployment. Optional; defaults to gpt-<name_prefix>. Empty or whitespace is rejected."
  type        = string
  default     = null
  validation {
    condition     = var.model_deployment_name == null ? true : length(trimspace(var.model_deployment_name)) > 0
    error_message = "model_deployment_name must not be empty or whitespace."
  }
}

variable "model_name" {
  description = "Azure OpenAI model name for the deployment. Required; empty or whitespace is rejected."
  type        = string
  nullable    = false
  validation {
    condition     = length(trimspace(var.model_name)) > 0
    error_message = "model_name must not be empty or whitespace."
  }
}

variable "model_version" {
  description = "Azure OpenAI model version for the deployment. Required; empty or whitespace is rejected."
  type        = string
  nullable    = false
  validation {
    condition     = length(trimspace(var.model_version)) > 0
    error_message = "model_version must not be empty or whitespace."
  }
}

variable "terraform_principal_object_id" {
  type = string
}

variable "budget_amount" {
  type     = number
  nullable = false
}

variable "budget_start_date" {
  type = string
}

variable "budget_contact_emails" {
  type = list(string)
}

variable "telemetry_reader_group_id" {
  description = "Entra group object id with restricted, read-only access to diagnostics. Required."
  type        = string
  nullable    = false
  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", var.telemetry_reader_group_id))
    error_message = "telemetry_reader_group_id must be an Entra object id (GUID)."
  }
}

variable "security_response_owner" {
  description = "Named human who receives security alerts. Deployment is blocked until set."
  type = object({
    name  = string
    email = string
  })
  nullable = false
  validation {
    condition     = length(trimspace(var.security_response_owner.name)) > 0 && can(regex("^[^@\\s]+@[^@\\s]+$", var.security_response_owner.email))
    error_message = "security_response_owner requires a named person and a valid email address."
  }
}

variable "security_alert_settings" {
  description = "Explicit security alert settings. Alerts must be enabled with positive thresholds before deployment."
  type = object({
    enabled                      = bool
    failure_threshold            = number
    window_minutes               = number
    evaluation_frequency_minutes = number
  })
  default = {
    enabled                      = true
    failure_threshold            = 5
    window_minutes               = 15
    evaluation_frequency_minutes = 5
  }
  nullable = false
  validation {
    condition     = var.security_alert_settings.enabled == true && var.security_alert_settings.failure_threshold >= 1 && var.security_alert_settings.evaluation_frequency_minutes >= 5 && var.security_alert_settings.window_minutes >= var.security_alert_settings.evaluation_frequency_minutes
    error_message = "security_alert_settings must enable alerts, use a threshold of at least 1, evaluate every 5 minutes or more, and use a window at least as long as the evaluation frequency."
  }
}

variable "terraform_state_backend" {
  type = object({
    storage_account_name = string
    container_name       = optional(string, "tfstate")
    key                  = optional(string, "demo.tfstate")
    use_azuread_auth     = optional(bool, true)
    locking_enabled      = optional(bool, true)
  })
  nullable = false
  validation {
    condition     = var.terraform_state_backend.use_azuread_auth == true && var.terraform_state_backend.locking_enabled == true && length(var.terraform_state_backend.storage_account_name) > 0
    error_message = "terraform_state_backend requires use_azuread_auth = true, locking_enabled = true and a state storage account name."
  }
}

variable "acr_name" {
  description = "Optional. Derived from name_prefix when not set."
  type        = string
}

variable "container_apps_environment_name" {
  description = "Optional. Derived from name_prefix when not set."
  type        = string
}

variable "container_app_name" {
  description = "Optional. Derived from name_prefix when not set."
  type        = string
}

variable "image_digest" {
  description = "Immutable digest of the application image, for example sha256:<64 lowercase hex characters>."
  type        = string
  nullable    = false
  validation {
    condition     = can(regex("^sha256:[0-9a-f]{64}$", var.image_digest))
    error_message = "image_digest must be a sha256 digest (sha256 followed by 64 lowercase hex characters), not a tag."
  }
}

variable "demo_passcode_secret_uri" {
  description = "Key Vault secret URI of the demo passcode. The Container App references it as a secret; the value is never inlined."
  type        = string
  nullable    = false
}
