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
