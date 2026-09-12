variable "state_resource_group_name" {
  description = "Dedicated resource group for Terraform state. It must not be the disposable demo resource group."
  type        = string

  validation {
    condition     = can(regex("^[A-Za-z0-9][A-Za-z0-9._()-]{0,89}$", var.state_resource_group_name))
    error_message = "state_resource_group_name must be a valid, separately named Azure resource-group name."
  }
}

variable "state_storage_account_name" {
  description = "Dedicated StorageV2 account for Terraform state."
  type        = string

  validation {
    condition     = can(regex("^[a-z0-9]{3,24}$", var.state_storage_account_name))
    error_message = "state_storage_account_name must be 3-24 lowercase letters or numbers."
  }
}

variable "state_container_name" {
  description = "Private blob container that holds the workload Terraform state."
  type        = string
  default     = "tfstate"

  validation {
    condition     = can(regex("^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])?$", var.state_container_name))
    error_message = "state_container_name must be a valid lowercase blob container name."
  }
}

variable "region" {
  description = "Azure region for the independently owned state resources."
  type        = string
  default     = "swedencentral"
}

variable "approved_operator_ip_ranges" {
  description = "Explicit operator CIDR ranges allowed through the state account firewall."
  type        = list(string)

  validation {
    condition     = length(var.approved_operator_ip_ranges) > 0 && alltrue([
      for ip_range in var.approved_operator_ip_ranges : can(cidrhost(ip_range, 0))
    ])
    error_message = "approved_operator_ip_ranges must contain at least one valid, explicitly approved CIDR range."
  }
}

variable "deployment_principal_object_id" {
  description = "Object ID of the Entra deployment principal that may read and write the state container."
  type        = string

  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$", var.deployment_principal_object_id))
    error_message = "deployment_principal_object_id must be a valid Entra object ID."
  }
}

variable "owner" {
  description = "Accountable owner tag for the retained state backend."
  type        = string
  default     = "airlinedemo-platform"
}

variable "cost_center" {
  description = "Cost allocation tag for the retained state backend."
  type        = string
  default     = "airlinedemo-platform"
}
