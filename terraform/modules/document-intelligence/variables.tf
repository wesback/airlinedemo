variable "deployment_name" {
  description = "Stable deployment name used for the Document Intelligence resource."
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

variable "allowed_ip_ranges" {
  description = "Explicit public firewall allow-list. An empty list denies public data-plane access."
  type        = list(string)
}

variable "tags" {
  description = "Ownership, cost, and lifecycle tags."
  type        = map(string)
}
