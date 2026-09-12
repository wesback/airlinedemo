variable "deployment_name" {
  description = "Stable deployment name used to derive Container Apps resource names."
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

variable "log_analytics_workspace_id" {
  description = "Existing shared Log Analytics workspace for the Container Apps environment."
  type        = string
}

variable "container_image" {
  description = "Explicit non-secret application image reference."
  type        = string
}

variable "container_port" {
  description = "Container Apps ingress target port exposed by the application."
  type        = number
}

variable "allowed_source_ranges" {
  description = "Explicit public ingress CIDR allow-list for the Container App."
  type        = list(string)

  validation {
    condition     = length(var.allowed_source_ranges) > 0 && alltrue([for ip in var.allowed_source_ranges : can(cidrhost(ip, 0))])
    error_message = "allowed_source_ranges must contain at least one valid CIDR range."
  }
}

variable "min_replicas" {
  description = "Minimum Container App replica count."
  type        = number
}

variable "max_replicas" {
  description = "Maximum Container App replica count."
  type        = number
}

variable "tags" {
  description = "Ownership, cost, and lifecycle tags."
  type        = map(string)
}
