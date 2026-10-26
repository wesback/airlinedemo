# Optional self-hosted GitHub Actions runner inside the virtual network. The build and release workflows run their
# private-acr jobs here because the registry and Key Vault are reachable only through private endpoints.
# The jobs sign in with their own OIDC identities; this VM's identity can only read the runner token secret.
locals {
  build_runner_count = var.enable_build_runner ? 1 : 0
  build_runner_name  = "vm-${local.name_prefix}-runner"
}

resource "azurerm_subnet" "build_runner" {
  count                           = local.build_runner_count
  name                            = "snet-build-runner"
  resource_group_name             = azurerm_resource_group.demo.name
  virtual_network_name            = azurerm_virtual_network.demo.name
  address_prefixes                = [local.build_runner_subnet_cidr]
  default_outbound_access_enabled = false
}

resource "azurerm_network_security_group" "build_runner" {
  count               = local.build_runner_count
  name                = "nsg-build-runner"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name

  security_rule {
    name                       = "deny-all-inbound"
    priority                   = 4096
    direction                  = "Inbound"
    access                     = "Deny"
    protocol                   = "*"
    source_port_range          = "*"
    destination_port_range     = "*"
    source_address_prefix      = "*"
    destination_address_prefix = "*"
  }
}

resource "azurerm_subnet_network_security_group_association" "build_runner" {
  count                     = local.build_runner_count
  subnet_id                 = azurerm_subnet.build_runner[0].id
  network_security_group_id = azurerm_network_security_group.build_runner[0].id
}

# Outbound to GitHub and package mirrors; the VM has no public address of its own.
resource "azurerm_public_ip" "build_runner_nat" {
  count               = local.build_runner_count
  name                = "pip-${local.name_prefix}-runner-nat"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name
  allocation_method   = "Static"
  sku                 = "Standard"
}

resource "azurerm_nat_gateway" "build_runner" {
  count               = local.build_runner_count
  name                = "ng-${local.name_prefix}-runner"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name
  sku_name            = "Standard"
}

resource "azurerm_nat_gateway_public_ip_association" "build_runner" {
  count                = local.build_runner_count
  nat_gateway_id       = azurerm_nat_gateway.build_runner[0].id
  public_ip_address_id = azurerm_public_ip.build_runner_nat[0].id
}

resource "azurerm_subnet_nat_gateway_association" "build_runner" {
  count          = local.build_runner_count
  subnet_id      = azurerm_subnet.build_runner[0].id
  nat_gateway_id = azurerm_nat_gateway.build_runner[0].id
}

resource "azurerm_user_assigned_identity" "build_runner" {
  count               = local.build_runner_count
  name                = "id-${local.name_prefix}-runner"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name
}

# The vault is new on the first apply, so Terraform creates the token secret with the write-only value "unset"
# for the deployer to overwrite; the runner waits for a real value. Terraform never writes it again, also not
# after adopting a recovered vault (adopt_key_vault), so a stored token is never replaced.
resource "azurerm_key_vault_secret" "build_runner_token" {
  count            = local.build_runner_count
  name             = var.build_runner_token_secret_name
  key_vault_id     = azurerm_key_vault.kv.id
  value_wo         = "unset"
  value_wo_version = 1
  content_type     = "GitHub token for runner registration"

  lifecycle {
    ignore_changes = [value, value_wo_version]
  }

  depends_on = [
    azurerm_role_assignment.terraform_kv_secrets,
    azurerm_private_endpoint.demo,
    azurerm_private_dns_zone_virtual_network_link.demo,
  ]
}

resource "azurerm_role_assignment" "build_runner_token_secret" {
  count                = local.build_runner_count
  scope                = "${azurerm_key_vault.kv.id}/secrets/${var.build_runner_token_secret_name}"
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.build_runner[0].principal_id
  principal_type       = "ServicePrincipal"

  depends_on = [azurerm_key_vault_secret.build_runner_token]
}

resource "azurerm_network_interface" "build_runner" {
  count               = local.build_runner_count
  name                = "nic-${local.name_prefix}-runner"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name

  ip_configuration {
    name                          = "internal"
    subnet_id                     = azurerm_subnet.build_runner[0].id
    private_ip_address_allocation = "Dynamic"
  }
}

resource "azurerm_linux_virtual_machine" "build_runner" {
  count                           = local.build_runner_count
  name                            = local.build_runner_name
  location                        = azurerm_resource_group.demo.location
  resource_group_name             = azurerm_resource_group.demo.name
  size                            = var.build_runner_vm_size
  admin_username                  = "azureuser"
  disable_password_authentication = true
  network_interface_ids           = [azurerm_network_interface.build_runner[0].id]
  secure_boot_enabled             = true
  vtpm_enabled                    = true
  patch_mode                      = "AutomaticByPlatform"
  patch_assessment_mode           = "AutomaticByPlatform"

  custom_data = base64encode(templatefile("${path.module}/templates/build-runner-cloud-init.yaml.tftpl", {
    client_id      = azurerm_user_assigned_identity.build_runner[0].client_id
    key_vault_name = local.key_vault_name
    secret_name    = var.build_runner_token_secret_name
    repository     = coalesce(var.build_runner_github_repository, "unset/unset")
    runner_name    = local.build_runner_name
  }))

  admin_ssh_key {
    username   = "azureuser"
    public_key = coalesce(var.build_runner_admin_ssh_public_key, "unset")
  }

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.build_runner[0].id]
  }

  os_disk {
    caching              = "ReadWrite"
    storage_account_type = "Premium_LRS"
    disk_size_gb         = 64
  }

  source_image_reference {
    publisher = "Canonical"
    offer     = "ubuntu-24_04-lts"
    sku       = "server"
    version   = "latest"
  }

  boot_diagnostics {}

  lifecycle {
    precondition {
      condition     = var.build_runner_github_repository != null && var.build_runner_admin_ssh_public_key != null
      error_message = "enable_build_runner needs build_runner_github_repository and build_runner_admin_ssh_public_key."
    }
  }

  # The runner reads its token from the private vault and pulls through the NAT gateway as soon as it boots.
  depends_on = [
    azurerm_role_assignment.build_runner_token_secret,
    azurerm_subnet_nat_gateway_association.build_runner,
    azurerm_subnet_network_security_group_association.build_runner,
    azurerm_private_endpoint.demo,
    azurerm_private_dns_zone_virtual_network_link.demo,
  ]
}
