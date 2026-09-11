variable "deployment_name" {
  description = "Stable deployment name used for monitoring resource names."
  type        = string
}

variable "resource_group_name" {
  description = "Dedicated resource group for monitoring resources."
  type        = string
}

variable "region" {
  description = "Azure region for monitoring resources."
  type        = string
}

variable "retention_days" {
  description = "Application Insights and Log Analytics retention period."
  type        = number

  validation {
    condition     = var.retention_days == 90
    error_message = "retention_days must be 90 for this demo."
  }
}

variable "tags" {
  description = "Ownership, cost, and lifecycle tags applied to monitoring resources."
  type        = map(string)
}
