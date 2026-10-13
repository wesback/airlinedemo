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
  type = string
}

variable "vnet_address_space" {
  type = list(string)
}

variable "app_subnet_cidr" {
  type = string
}

variable "private_endpoint_subnet_cidr" {
  type = string
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
  type     = list(string)
  nullable = false
  validation {
    condition     = alltrue([for c in var.egress_allowed_destinations : can(cidrnetmask(c))])
    error_message = "egress_allowed_destinations entries must be CIDR blocks."
  }
}

variable "sql_server_name" {
  type = string
}

variable "sql_admin_group_name" {
  type = string
}

variable "sql_admin_group_object_id" {
  type = string
}

variable "storage_account_name" {
  type = string
}

variable "key_vault_name" {
  type = string
}

variable "model_account_name" {
  type = string
}

variable "model_endpoint_subdomain" {
  type = string
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
  nullable = false
  validation {
    condition     = var.security_alert_settings.enabled == true && var.security_alert_settings.failure_threshold >= 1 && var.security_alert_settings.evaluation_frequency_minutes >= 5 && var.security_alert_settings.window_minutes >= var.security_alert_settings.evaluation_frequency_minutes
    error_message = "security_alert_settings must enable alerts, use a threshold of at least 1, evaluate every 5 minutes or more, and use a window at least as long as the evaluation frequency."
  }
}

variable "terraform_state_backend" {
  type = object({
    storage_account_name = string
    container_name       = string
    key                  = string
    use_azuread_auth     = bool
    locking_enabled      = bool
  })
  nullable = false
  validation {
    condition     = var.terraform_state_backend.use_azuread_auth == true && var.terraform_state_backend.locking_enabled == true && length(var.terraform_state_backend.storage_account_name) > 0
    error_message = "terraform_state_backend requires use_azuread_auth = true, locking_enabled = true and a state storage account name."
  }
}
