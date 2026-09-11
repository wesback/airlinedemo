variable "deployment_name" {
  description = "Stable deployment name used for SQL resource names."
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

variable "sql_engine" {
  description = "Approved database engine."
  type        = string

  validation {
    condition     = var.sql_engine == "Azure SQL"
    error_message = "The SQL module requires Azure SQL."
  }
}

variable "sql_sku" {
  description = "Approved SQL service tier."
  type        = string

  validation {
    condition     = var.sql_sku == "Serverless General Purpose"
    error_message = "The SQL module requires Serverless General Purpose."
  }
}

variable "sql_authentication" {
  description = "Approved database authentication boundary."
  type        = string

  validation {
    condition     = var.sql_authentication == "system-assigned managed identity"
    error_message = "The SQL module requires system-assigned managed identity authentication."
  }
}

variable "sql_endpoint_type" {
  description = "Approved SQL endpoint type."
  type        = string

  validation {
    condition     = var.sql_endpoint_type == "public"
    error_message = "The SQL module requires a public endpoint."
  }
}

variable "sql_endpoint_access" {
  description = "Approved SQL endpoint access boundary."
  type        = string

  validation {
    condition     = var.sql_endpoint_access == "firewall-restricted"
    error_message = "The SQL module requires a firewall-restricted endpoint."
  }
}

variable "sql_aad_admin_login" {
  description = "Existing Entra administrator login supplied by the identity prerequisite."
  type        = string
  sensitive   = true
}

variable "sql_aad_admin_object_id" {
  description = "Existing Entra administrator object ID supplied by the identity prerequisite."
  type        = string
  sensitive   = true
}

variable "tags" {
  description = "Ownership, cost, and lifecycle tags."
  type        = map(string)
}
