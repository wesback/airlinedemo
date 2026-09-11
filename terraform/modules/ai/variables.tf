variable "deployment_name" {
  description = "Stable deployment name used for Azure OpenAI resources."
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

variable "model_name" {
  description = "Exact Azure OpenAI model name confirmed by the deployment readiness gate."
  type        = string

  validation {
    condition     = length(trimspace(var.model_name)) > 0
    error_message = "model_name must be explicitly supplied; the AI module must not select a model."
  }
}

variable "model_version" {
  description = "Exact Azure OpenAI model version confirmed by the deployment readiness gate."
  type        = string

  validation {
    condition     = length(trimspace(var.model_version)) > 0
    error_message = "model_version must be explicitly supplied; the AI module must not select a version."
  }
}

variable "deployment_type" {
  description = "Azure OpenAI deployment type confirmed by the deployment readiness gate."
  type        = string

  validation {
    condition     = var.deployment_type == "Standard"
    error_message = "deployment_type must be Standard for this deployment."
  }
}

variable "quota_tokens_per_minute" {
  description = "Azure OpenAI quota in tokens per minute."
  type        = number

  validation {
    condition     = var.quota_tokens_per_minute == 30000
    error_message = "quota_tokens_per_minute must be 30000 for this approved configuration."
  }
}

variable "allowed_ip_ranges" {
  description = "Explicit public firewall allow-list. An empty list denies public data-plane access."
  type        = list(string)
}

variable "tags" {
  description = "Ownership, cost, and lifecycle tags."
  type        = map(string)
}
