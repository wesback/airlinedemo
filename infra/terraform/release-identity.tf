# Workflow identities for building, signing, and releasing digest-pinned images (FR-011, AC-018, AC-019).
# GitHub Actions authenticates with OIDC federation: no client secret, storage key, or registry password is stored.

variable "signing_key_name" {
  description = "Name of the Key Vault key used to sign image digests."
  type        = string
  default     = "release-image-signing"
  nullable    = false
}

variable "signing_certificate_name" {
  description = "Name of the Key Vault certificate whose public part verifies image signatures."
  type        = string
  default     = "release-image-signing"
  nullable    = false
}

locals {
  github_oidc_issuer = "https://token.actions.githubusercontent.com"
  github_audience    = "api://AzureADTokenExchange"
  github_repository  = "wesback/airlinedemo"
}

resource "azurerm_user_assigned_identity" "build" {
  name                = "id-airline-build"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name
}

resource "azurerm_user_assigned_identity" "release" {
  name                = "id-airline-release"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name
}

# Build workflow: runs only from main, pushes images to the private ACR.
resource "azurerm_federated_identity_credential" "build" {
  name                      = "fic-airline-build-main"
  user_assigned_identity_id = azurerm_user_assigned_identity.build.id
  audience                  = [local.github_audience]
  issuer                    = local.github_oidc_issuer
  subject                   = "repo:${local.github_repository}:ref:refs/heads/main"
}

# Release workflow: runs only inside the production-release environment, which requires product-owner approval.
resource "azurerm_federated_identity_credential" "release" {
  name                      = "fic-airline-release-production"
  user_assigned_identity_id = azurerm_user_assigned_identity.release.id
  audience                  = [local.github_audience]
  issuer                    = local.github_oidc_issuer
  subject                   = "repo:${local.github_repository}:environment:production-release"
}

resource "azurerm_role_assignment" "build_acr_push" {
  scope                = azurerm_container_registry.acr.id
  role_definition_name = "AcrPush"
  principal_id         = azurerm_user_assigned_identity.build.principal_id
}

resource "azurerm_role_assignment" "release_acr_push" {
  scope                = azurerm_container_registry.acr.id
  role_definition_name = "AcrPush"
  principal_id         = azurerm_user_assigned_identity.release.principal_id
}

resource "azurerm_role_assignment" "release_signing_key" {
  scope                = "${azurerm_key_vault.kv.id}/keys/${var.signing_key_name}"
  role_definition_name = "Key Vault Crypto User"
  principal_id         = azurerm_user_assigned_identity.release.principal_id
}

resource "azurerm_role_assignment" "release_signing_certificate" {
  scope                = "${azurerm_key_vault.kv.id}/certificates/${var.signing_certificate_name}"
  role_definition_name = "Key Vault Certificate User"
  principal_id         = azurerm_user_assigned_identity.release.principal_id
}

resource "azurerm_role_assignment" "release_container_app" {
  scope                = azurerm_container_app.app.id
  role_definition_name = "Container Apps Contributor"
  principal_id         = azurerm_user_assigned_identity.release.principal_id
}

output "build_identity_client_id" {
  description = "Client ID of the build workflow identity (public; authentication is by OIDC federation)."
  value       = azurerm_user_assigned_identity.build.client_id
}

output "release_identity_client_id" {
  description = "Client ID of the release workflow identity (public; authentication is by OIDC federation)."
  value       = azurerm_user_assigned_identity.release.client_id
}
