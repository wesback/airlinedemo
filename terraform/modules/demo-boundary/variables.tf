variable "deployment_name" {
  description = "Stable deployment name used to derive storage account names."
  type        = string
}

variable "resource_group_name" {
  description = "Dedicated resource group owned by this demo deployment."
  type        = string
}

variable "region" {
  description = "Azure region for the demo resources."
  type        = string
}

variable "evidence_retention_days" {
  description = "Retention period for evidence blobs, snapshots, and versions."
  type        = number

  validation {
    condition     = var.evidence_retention_days == 90
    error_message = "evidence_retention_days must be 90 for this demo."
  }
}

variable "tags" {
  description = "Ownership, cost, and lifecycle tags applied to every resource."
  type        = map(string)
}
