variable "name_prefix" {
  description = "Short lowercase prefix (3-10 letters or digits) from which every unset resource name is derived. Required unless every derived name is set."
  type        = string
  default     = null
  validation {
    condition = (var.name_prefix == null ? false : can(regex("^[a-z][a-z0-9]{2,9}$", var.name_prefix))) || alltrue([
      for v in [var.resource_group_name, var.sql_server_name, var.storage_account_name, var.key_vault_name, var.model_account_name,
      var.model_endpoint_subdomain, var.model_deployment_name, var.acr_name, var.container_apps_environment_name, var.container_app_name] : v != null
    ])
    error_message = "name_prefix must be 3-10 lowercase letters or digits starting with a letter, unless every derived resource name is set explicitly."
  }
}

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
  description = "Optional. Derived from name_prefix when not set."
  type        = string
  default     = null
}

variable "vnet_address_space" {
  description = "Virtual network range. Both subnets default to /24 slices of the first entry."
  type        = list(string)
  default     = ["10.20.0.0/16"]
}

variable "app_subnet_cidr" {
  description = "Optional. Defaults to the second /24 of the first vnet_address_space entry."
  type        = string
  default     = null
}

variable "private_endpoint_subnet_cidr" {
  description = "Optional. Defaults to the third /24 of the first vnet_address_space entry."
  type        = string
  default     = null
}

variable "gateway_subnet_cidr" {
  description = "Optional. Dedicated API Management subnet; defaults to the fourth /24 of the first vnet_address_space entry."
  type        = string
  default     = null
}

variable "deployer_ip_cidrs" {
  description = "Public CIDRs of the machine running Terraform. Empty (the default) keeps Key Vault fully private, so Terraform must then run inside the VNet to create the signing certificate."
  type        = list(string)
  default     = []
  nullable    = false
  validation {
    condition     = alltrue([for c in var.deployer_ip_cidrs : can(cidrnetmask(c))])
    error_message = "deployer_ip_cidrs entries must be CIDR blocks."
  }
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
  description = "Allowed egress CIDRs. Empty (the default) allows none."
  type        = list(string)
  default     = []
  nullable    = false
  validation {
    condition     = alltrue([for c in var.egress_allowed_destinations : can(cidrnetmask(c))])
    error_message = "egress_allowed_destinations entries must be CIDR blocks."
  }
}

variable "sql_database_name" {
  description = "SQL database name."
  type        = string
  default     = "airlinedemo"
}

variable "sql_server_name" {
  description = "Optional. Derived from name_prefix when not set."
  type        = string
  default     = null
}

variable "sql_admin_group_name" {
  type = string
}

variable "sql_admin_group_object_id" {
  type = string
}

variable "storage_account_name" {
  description = "Optional. Derived from name_prefix when not set."
  type        = string
  default     = null
}

variable "key_vault_name" {
  description = "Optional. Derived from name_prefix when not set."
  type        = string
  default     = null
}

variable "model_account_name" {
  description = "Optional. Derived from name_prefix when not set."
  type        = string
  default     = null
}

variable "model_endpoint_subdomain" {
  description = "Optional. Derived from name_prefix when not set."
  type        = string
  default     = null
}

variable "gateway_endpoint" {
  description = "Optional. Defaults to the API Management gateway URL."
  type        = string
  default     = null
  validation {
    condition     = var.gateway_endpoint == null ? true : length(trimspace(var.gateway_endpoint)) > 0 && can(regex("^https://", lower(trimspace(var.gateway_endpoint))))
    error_message = "gateway_endpoint must not be empty or whitespace."
  }
}

variable "gateway_region" {
  description = "Optional. Defaults to location; VNet injection requires API Management in the virtual network's region."
  type        = string
  default     = null
  validation {
    condition     = var.gateway_region == null ? true : length(trimspace(var.gateway_region)) > 0
    error_message = "gateway_region must not be empty or whitespace."
  }
}

variable "gateway_api_management_tier" {
  type     = string
  default  = "Developer"
  nullable = false
  validation {
    condition     = length(trimspace(var.gateway_api_management_tier)) > 0
    error_message = "gateway_api_management_tier must not be empty or whitespace."
  }
}

variable "gateway_model_deployment" {
  description = "Optional. Defaults to the model deployment this configuration creates."
  type        = string
  default     = null
  validation {
    condition     = var.gateway_model_deployment == null ? true : length(trimspace(var.gateway_model_deployment)) > 0
    error_message = "gateway_model_deployment must not be empty or whitespace."
  }
}

variable "gateway_audience" {
  description = "Identifier URI of the gateway's Entra application and the JWT audience the policy accepts. Null uses api://<client id> of the application Terraform creates; a custom URI must be allowed by the tenant's app management policy."
  type        = string
  default     = null
  validation {
    condition     = var.gateway_audience == null ? true : can(regex("^(api|https)://[^\\s/]+(/[^\\s]*[^\\s/])?$", var.gateway_audience))
    error_message = "gateway_audience must be null or an api:// or https:// URI without whitespace or a trailing slash."
  }
}

variable "gateway_approved_application_identity" {
  description = "Entra object identity approved to call the private model gateway. Optional; defaults to the app's managed identity."
  type        = string
  default     = null
  validation {
    condition     = var.gateway_approved_application_identity == null ? true : can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", var.gateway_approved_application_identity))
    error_message = "gateway_approved_application_identity must be an Entra object ID (GUID)."
  }
}

variable "model_deployment_name" {
  description = "Name of the Azure OpenAI deployment. Optional; defaults to gpt-<name_prefix>. Empty or whitespace is rejected."
  type        = string
  default     = null
  validation {
    condition     = var.model_deployment_name == null ? true : length(trimspace(var.model_deployment_name)) > 0
    error_message = "model_deployment_name must not be empty or whitespace."
  }
}

variable "model_name" {
  description = "Azure OpenAI model name for the deployment. Required; empty or whitespace is rejected."
  type        = string
  nullable    = false
  validation {
    condition     = length(trimspace(var.model_name)) > 0
    error_message = "model_name must not be empty or whitespace."
  }
}

variable "model_version" {
  description = "Azure OpenAI model version for the deployment. Required; empty or whitespace is rejected."
  type        = string
  nullable    = false
  validation {
    condition     = length(trimspace(var.model_version)) > 0
    error_message = "model_version must not be empty or whitespace."
  }
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

variable "telemetry_reader_principal_type" {
  description = "Entra principal type for restricted, read-only diagnostics access."
  type        = string
  default     = "Group"
  nullable    = false
  validation {
    condition     = contains(["Group", "User"], var.telemetry_reader_principal_type)
    error_message = "telemetry_reader_principal_type must be Group or User."
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
  default = {
    enabled                      = true
    failure_threshold            = 5
    window_minutes               = 15
    evaluation_frequency_minutes = 5
  }
  nullable = false
  validation {
    condition     = var.security_alert_settings.enabled == true && var.security_alert_settings.failure_threshold >= 1 && var.security_alert_settings.evaluation_frequency_minutes >= 5 && var.security_alert_settings.window_minutes >= var.security_alert_settings.evaluation_frequency_minutes
    error_message = "security_alert_settings must enable alerts, use a threshold of at least 1, evaluate every 5 minutes or more, and use a window at least as long as the evaluation frequency."
  }
}

variable "terraform_state_backend" {
  type = object({
    storage_account_name = string
    container_name       = optional(string, "tfstate")
    key                  = optional(string, "demo.tfstate")
    use_azuread_auth     = optional(bool, true)
    locking_enabled      = optional(bool, true)
  })
  nullable = false
  validation {
    condition     = var.terraform_state_backend.use_azuread_auth == true && var.terraform_state_backend.locking_enabled == true && length(var.terraform_state_backend.storage_account_name) > 0
    error_message = "terraform_state_backend requires use_azuread_auth = true, locking_enabled = true and a state storage account name."
  }
}

variable "acr_name" {
  description = "Optional. Derived from name_prefix when not set."
  type        = string
  default     = null
}

variable "container_apps_environment_name" {
  description = "Optional. Derived from name_prefix when not set."
  type        = string
  default     = null
}

variable "container_app_name" {
  description = "Optional. Derived from name_prefix when not set."
  type        = string
  default     = null
}

variable "image_digest" {
  description = "Immutable digest of the application image, for example sha256:<64 lowercase hex characters>. Null on the first (platform) phase: the container app is created once the digest and demo_passcode_secret_uri are both set. Afterwards the release workflow owns the running image."
  type        = string
  default     = null
  validation {
    condition     = var.image_digest == null || can(regex("^sha256:[0-9a-f]{64}$", var.image_digest))
    error_message = "image_digest must be a sha256 digest (sha256 followed by 64 lowercase hex characters), not a tag."
  }
}

variable "demo_passcode_secret_uri" {
  description = "Key Vault secret URI of the demo passcode. The Container App references it as a secret; the value is never inlined. Set together with image_digest."
  type        = string
  default     = null
  validation {
    condition     = (var.demo_passcode_secret_uri == null) == (var.image_digest == null)
    error_message = "Set image_digest and demo_passcode_secret_uri together, or leave both unset for the platform-only first phase."
  }
}

variable "enable_build_runner" {
  description = "Create the self-hosted GitHub Actions runner VM (label private-acr) that builds, pushes and signs images through the private registry."
  type        = bool
  default     = false
  nullable    = false
}

variable "build_runner_github_repository" {
  description = "Repository the runner registers with, as owner/name. Required when enable_build_runner is true."
  type        = string
  default     = null
  validation {
    condition     = var.build_runner_github_repository == null || can(regex("^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$", var.build_runner_github_repository))
    error_message = "build_runner_github_repository must be owner/name."
  }
}

variable "build_runner_token_secret_name" {
  description = "Key Vault secret (in this deployment's vault) holding a GitHub token allowed to create runner registration tokens for the repository. Terraform creates it with the value unset; set the real token after the first apply."
  type        = string
  default     = "github-runner-token"
  nullable    = false
  validation {
    condition     = can(regex("^[A-Za-z0-9-]{1,127}$", var.build_runner_token_secret_name))
    error_message = "build_runner_token_secret_name must be a Key Vault secret name."
  }
}

variable "build_runner_admin_ssh_public_key" {
  description = "OpenSSH public key for the runner VM admin account. The VM has no inbound access; the key is for az vm run-command or Bastion break-glass. Required when enable_build_runner is true."
  type        = string
  default     = null
  validation {
    condition     = var.build_runner_admin_ssh_public_key == null || can(regex("^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp256) [A-Za-z0-9+/=]+", var.build_runner_admin_ssh_public_key))
    error_message = "build_runner_admin_ssh_public_key must be an OpenSSH public key."
  }
}

variable "build_runner_vm_size" {
  description = "Runner VM size."
  type        = string
  default     = "Standard_D2s_v5"
  nullable    = false
}

variable "build_runner_subnet_cidr" {
  description = "Optional. Defaults to the fifth /24 of the virtual network."
  type        = string
  default     = null
  validation {
    condition     = var.build_runner_subnet_cidr == null || can(cidrnetmask(var.build_runner_subnet_cidr))
    error_message = "build_runner_subnet_cidr must be a CIDR block."
  }
}

variable "adopt_key_vault" {
  description = "Adopt a Key Vault that scripts/deploy.sh recovered from soft delete (purge protection keeps the name for 90 days after a destroy). Terraform imports the resource group, the vault, the deployer's two vault roles and the existing signing certificate and runner token instead of failing with 'already exists'. Imports of objects already in state are no-ops. Leave null otherwise."
  type = object({
    certificates_role_assignment_id = string
    secrets_role_assignment_id      = string
    signing_certificate_version     = optional(string)
    build_runner_token_version      = optional(string)
  })
  default = null
  validation {
    condition = var.adopt_key_vault == null || alltrue([
      for id in [try(var.adopt_key_vault.certificates_role_assignment_id, ""), try(var.adopt_key_vault.secrets_role_assignment_id, "")] :
      can(regex("^/subscriptions/[0-9a-f-]{36}/resourceGroups/[^/]+/providers/Microsoft\\.KeyVault/vaults/[^/]+/providers/Microsoft\\.Authorization/roleAssignments/[0-9a-f-]{36}$", id))
    ])
    error_message = "adopt_key_vault role assignment ids must be role assignments scoped to the deployment Key Vault."
  }
  validation {
    condition = var.adopt_key_vault == null || alltrue([
      for v in [try(var.adopt_key_vault.signing_certificate_version, null), try(var.adopt_key_vault.build_runner_token_version, null)] :
      v == null || can(regex("^[0-9a-f]{32}$", v))
    ])
    error_message = "adopt_key_vault object versions must be 32 lowercase hex characters."
  }
}
