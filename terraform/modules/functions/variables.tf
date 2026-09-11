variable "deployment_name" {
  description = "Stable deployment name used for Function App resources."
  type        = string
}

variable "resource_group_name" {
  description = "Tagged demo resource group owned by this deployment."
  type        = string
}

variable "region" {
  description = "Explicit Azure region selected by the deployment gate."
  type        = string
}

variable "function_runtime" {
  description = "Approved Azure Functions runtime generation."
  type        = string

  validation {
    condition     = var.function_runtime == "Azure Functions v4"
    error_message = "The Functions module requires Azure Functions v4."
  }
}

variable "function_worker_model" {
  description = "Approved isolated worker runtime."
  type        = string

  validation {
    condition     = var.function_worker_model == "dotnet-isolated"
    error_message = "The Functions module requires the dotnet-isolated worker."
  }
}

variable "target_framework" {
  description = "Approved .NET target framework."
  type        = string

  validation {
    condition     = var.target_framework == "net10.0"
    error_message = "The Functions module requires net10.0."
  }
}

variable "hosting_plan" {
  description = "Approved Functions hosting plan."
  type        = string

  validation {
    condition     = var.hosting_plan == "Flex Consumption"
    error_message = "The Functions module requires Flex Consumption."
  }
}

variable "durable_backend" {
  description = "Durable Functions backend."
  type        = string

  validation {
    condition     = var.durable_backend == "Azure Storage"
    error_message = "The Functions module requires Azure Storage for Durable Functions."
  }
}

variable "durable_storage_kind" {
  description = "Storage account kind used by the Durable Functions backend."
  type        = string

  validation {
    condition     = var.durable_storage_kind == "StorageV2"
    error_message = "The Functions module requires a StorageV2 Durable Functions backend."
  }
}

variable "runtime_host_storage_account_id" {
  description = "Resource identifier for the Durable Functions host storage account."
  type        = string
}

variable "runtime_host_storage_account_name" {
  description = "Name of the Durable Functions host storage account."
  type        = string
}

variable "application_insights_id" {
  description = "Resource identifier for the shared Application Insights resource."
  type        = string
}

variable "allowed_ip_ranges" {
  description = "Explicit public firewall allow-list for the Function App. Empty denies public endpoint access."
  type        = list(string)
  default     = []

  validation {
    condition     = alltrue([for ip in var.allowed_ip_ranges : can(cidrhost(ip, 0))])
    error_message = "allowed_ip_ranges must contain valid CIDR ranges."
  }
}

variable "tags" {
  description = "Ownership, cost, and lifecycle tags."
  type        = map(string)
}
