# A destroy soft-deletes the vault, and purge protection keeps its name for 90 days. scripts/deploy.sh recovers
# it (with its resource group) and grants the deployer's vault roles again, because RBAC assignments are not
# recovered; these imports then adopt what already exists. Each import is skipped when its object is in state.
locals {
  adopt_key_vault      = var.adopt_key_vault == null ? {} : { adopt = var.adopt_key_vault }
  key_vault_id         = "${local.rg_scope}/providers/Microsoft.KeyVault/vaults/${local.key_vault_name}"
  key_vault_data_plane = "https://${local.key_vault_name}.vault.azure.net"
}

import {
  for_each = local.adopt_key_vault
  to       = azurerm_resource_group.demo
  id       = local.rg_scope
}

import {
  for_each = local.adopt_key_vault
  to       = azurerm_key_vault.kv
  id       = local.key_vault_id
}

import {
  for_each = local.adopt_key_vault
  to       = azurerm_role_assignment.terraform_kv_certificates
  id       = each.value.certificates_role_assignment_id
}

import {
  for_each = local.adopt_key_vault
  to       = azurerm_role_assignment.terraform_kv_secrets
  id       = each.value.secrets_role_assignment_id
}

import {
  for_each = { for k, v in local.adopt_key_vault : k => v if v.signing_certificate_version != null }
  to       = azurerm_key_vault_certificate.signing
  id       = "${local.key_vault_data_plane}/certificates/${var.signing_certificate_name}/${each.value.signing_certificate_version}"
}

import {
  for_each = { for k, v in local.adopt_key_vault : k => v if v.build_runner_token_version != null && var.enable_build_runner }
  to       = azurerm_key_vault_secret.build_runner_token[0]
  id       = "${local.key_vault_data_plane}/secrets/${var.build_runner_token_secret_name}/${each.value.build_runner_token_version}"
}
