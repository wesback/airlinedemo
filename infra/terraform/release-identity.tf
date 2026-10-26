# Workflow identities for building, signing, and releasing digest-pinned images (FR-011, AC-018, AC-019).
# GitHub Actions authenticates with OIDC federation: no client secret, storage key, or registry password is stored.

variable "signing_certificate_name" {
  description = "Name of the Key Vault certificate whose public part verifies image signatures."
  type        = string
  default     = "release-image-signing-certificate"
  nullable    = false
}

locals {
  github_oidc_issuer = "https://token.actions.githubusercontent.com"
  github_audience    = "api://AzureADTokenExchange"
  # This repository's default OIDC subject includes immutable owner and repository IDs.
  github_subject_prefix = "repo:wesback@14137481/airlinedemo@1364258050"
  # Key Vault creates the key that backs a certificate under the certificate's name, so this is the signing key.
  signing_key_id = "${azurerm_key_vault.kv.id}/keys/${var.signing_certificate_name}"
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
  subject                   = "${local.github_subject_prefix}:ref:refs/heads/main"
}

# Release workflow: runs only inside the production-release environment, which requires product-owner approval.
resource "azurerm_federated_identity_credential" "release" {
  name                      = "fic-airline-release-production"
  user_assigned_identity_id = azurerm_user_assigned_identity.release.id
  audience                  = [local.github_audience]
  issuer                    = local.github_oidc_issuer
  subject                   = "${local.github_subject_prefix}:environment:production-release"
}

resource "azurerm_role_assignment" "build_acr_push" {
  scope                = azurerm_container_registry.acr.id
  role_definition_name = "AcrPush"
  principal_id         = azurerm_user_assigned_identity.build.principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "release_acr_push" {
  scope                = azurerm_container_registry.acr.id
  role_definition_name = "AcrPush"
  principal_id         = azurerm_user_assigned_identity.release.principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_key_vault_certificate" "signing" {
  name         = var.signing_certificate_name
  key_vault_id = azurerm_key_vault.kv.id

  # Data-plane access needs the Terraform principal's role and the vault's private endpoint or deployer perimeter.
  depends_on = [
    azurerm_role_assignment.terraform_kv_certificates,
    azurerm_private_endpoint.demo,
    azurerm_private_dns_zone_virtual_network_link.demo,
    azurerm_network_security_perimeter_association.key_vault,
  ]

  certificate_policy {
    issuer_parameters {
      name = "Self"
    }

    key_properties {
      exportable = false
      key_size   = 3072
      key_type   = "RSA"
      reuse_key  = true
    }

    secret_properties {
      content_type = "application/x-pkcs12"
    }

    x509_certificate_properties {
      subject            = "CN=airline-demo-release-signing"
      key_usage          = ["digitalSignature"]
      validity_in_months = 12
    }
  }
}

resource "azurerm_role_assignment" "release_signing_key" {
  scope                = local.signing_key_id
  role_definition_name = "Key Vault Crypto User"
  principal_id         = azurerm_user_assigned_identity.release.principal_id
  principal_type       = "ServicePrincipal"

  # The key scope only exists once the certificate has created it.
  depends_on = [azurerm_key_vault_certificate.signing]
}

resource "azurerm_role_assignment" "release_signing_certificate" {
  scope                = azurerm_key_vault_certificate.signing.resource_manager_id
  role_definition_name = "Key Vault Certificate User"
  principal_id         = azurerm_user_assigned_identity.release.principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "release_container_app" {
  count                = local.deploy_application ? 1 : 0
  scope                = azurerm_container_app.app[0].id
  role_definition_name = "Container Apps Contributor"
  principal_id         = azurerm_user_assigned_identity.release.principal_id
  principal_type       = "ServicePrincipal"
}
