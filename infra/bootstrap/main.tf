terraform {
  required_version = ">= 1.16.0"
  backend "local" {}
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    azapi = {
      source  = "Azure/azapi"
      version = "~> 2.0"
    }
  }
}

provider "azurerm" {
  subscription_id = var.subscription_id
  tenant_id       = var.tenant_id
  features {}
}

provider "azapi" {
  subscription_id = var.subscription_id
  tenant_id       = var.tenant_id
}

variable "subscription_id" { type = string }
variable "tenant_id" { type = string }
variable "prefix" {
  type = string
  validation {
    condition     = can(regex("^[a-z][a-z0-9]{2,9}$", var.prefix))
    error_message = "prefix must be 3-10 lowercase letters/digits starting with a letter."
  }
}
variable "location" {
  type    = string
  default = "swedencentral"
}
variable "operator_id" { type = string }
variable "ssh_public_key" { type = string }

locals {
  name       = "${var.prefix}-deploy"
  account    = "st${var.prefix}tf${substr(sha1("${var.subscription_id}/${var.prefix}"), 0, 6)}"
  account_id = "${azurerm_resource_group.bootstrap.id}/providers/Microsoft.Storage/storageAccounts/${local.account}"
}

resource "azurerm_resource_group" "bootstrap" {
  name     = "rg-${var.prefix}-tfstate"
  location = var.location
}

resource "azapi_resource" "state" {
  type      = "Microsoft.Storage/storageAccounts@2023-05-01"
  name      = local.account
  parent_id = azurerm_resource_group.bootstrap.id
  location  = var.location
  body = {
    kind = "StorageV2"
    sku  = { name = "Standard_LRS" }
    properties = {
      allowSharedKeyAccess     = false
      allowBlobPublicAccess    = false
      publicNetworkAccess      = "Disabled"
      minimumTlsVersion        = "TLS1_2"
      supportsHttpsTrafficOnly = true
    }
  }
  lifecycle { prevent_destroy = true }
}

resource "azapi_resource" "blob" {
  type      = "Microsoft.Storage/storageAccounts/blobServices@2023-05-01"
  name      = "default"
  parent_id = azapi_resource.state.id
  body = {
    properties = {
      isVersioningEnabled            = true
      deleteRetentionPolicy          = { enabled = true, days = 14 }
      containerDeleteRetentionPolicy = { enabled = true, days = 14 }
    }
  }
}

resource "azapi_resource" "container" {
  type      = "Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01"
  name      = "tfstate"
  parent_id = azapi_resource.blob.id
  body      = { properties = { publicAccess = "None" } }
  lifecycle { prevent_destroy = true }
}

resource "azurerm_role_assignment" "state_operator" {
  scope                = "${local.account_id}/blobServices/default/containers/tfstate"
  role_definition_name = "Storage Blob Data Owner"
  principal_id         = var.operator_id
  principal_type       = "User"
  depends_on           = [azapi_resource.container]
}

resource "azurerm_virtual_network" "executor" {
  name                = "vnet-${local.name}"
  resource_group_name = azurerm_resource_group.bootstrap.name
  location            = var.location
  address_space       = ["10.21.0.0/16"]
}

resource "azurerm_subnet" "executor" {
  name                            = "executor"
  resource_group_name             = azurerm_resource_group.bootstrap.name
  virtual_network_name            = azurerm_virtual_network.executor.name
  address_prefixes                = ["10.21.1.0/24"]
  default_outbound_access_enabled = false
}

resource "azurerm_network_security_group" "executor" {
  name                = "nsg-${local.name}"
  resource_group_name = azurerm_resource_group.bootstrap.name
  location            = var.location
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

resource "azurerm_subnet_network_security_group_association" "executor" {
  subnet_id                 = azurerm_subnet.executor.id
  network_security_group_id = azurerm_network_security_group.executor.id
}

resource "azurerm_public_ip" "nat" {
  name                = "pip-${local.name}-nat"
  resource_group_name = azurerm_resource_group.bootstrap.name
  location            = var.location
  allocation_method   = "Static"
  sku                 = "Standard"
}

resource "azurerm_nat_gateway" "executor" {
  name                = "ng-${local.name}"
  resource_group_name = azurerm_resource_group.bootstrap.name
  location            = var.location
  sku_name            = "Standard"
}

resource "azurerm_nat_gateway_public_ip_association" "executor" {
  nat_gateway_id       = azurerm_nat_gateway.executor.id
  public_ip_address_id = azurerm_public_ip.nat.id
}

resource "azurerm_subnet_nat_gateway_association" "executor" {
  subnet_id      = azurerm_subnet.executor.id
  nat_gateway_id = azurerm_nat_gateway.executor.id
}

resource "azurerm_private_dns_zone" "state" {
  name                = "privatelink.blob.core.windows.net"
  resource_group_name = azurerm_resource_group.bootstrap.name
}

resource "azurerm_private_dns_zone_virtual_network_link" "state" {
  name                  = "executor"
  resource_group_name   = azurerm_resource_group.bootstrap.name
  private_dns_zone_name = azurerm_private_dns_zone.state.name
  virtual_network_id    = azurerm_virtual_network.executor.id
}

resource "azurerm_private_endpoint" "state" {
  name                = "pe-${local.name}-state"
  resource_group_name = azurerm_resource_group.bootstrap.name
  location            = var.location
  subnet_id           = azurerm_subnet.executor.id
  private_service_connection {
    name                           = "state"
    private_connection_resource_id = azapi_resource.state.id
    subresource_names              = ["blob"]
    is_manual_connection           = false
  }
  private_dns_zone_group {
    name                 = "state"
    private_dns_zone_ids = [azurerm_private_dns_zone.state.id]
  }
}

resource "azurerm_network_interface" "executor" {
  name                = "nic-${local.name}"
  resource_group_name = azurerm_resource_group.bootstrap.name
  location            = var.location
  ip_configuration {
    name                          = "private"
    subnet_id                     = azurerm_subnet.executor.id
    private_ip_address_allocation = "Dynamic"
  }
}

resource "azurerm_linux_virtual_machine" "executor" {
  name                            = "vm-${local.name}"
  resource_group_name             = azurerm_resource_group.bootstrap.name
  location                        = var.location
  size                            = "Standard_D2s_v5"
  admin_username                  = "azureuser"
  disable_password_authentication = true
  network_interface_ids           = [azurerm_network_interface.executor.id]
  secure_boot_enabled             = true
  vtpm_enabled                    = true
  patch_mode                      = "AutomaticByPlatform"
  patch_assessment_mode           = "AutomaticByPlatform"
  admin_ssh_key {
    username   = "azureuser"
    public_key = var.ssh_public_key
  }
  os_disk {
    caching              = "ReadWrite"
    storage_account_type = "StandardSSD_LRS"
    disk_size_gb         = 64
  }
  source_image_reference {
    publisher = "Canonical"
    offer     = "ubuntu-24_04-lts"
    sku       = "server"
    version   = "latest"
  }
  identity { type = "SystemAssigned" }
  boot_diagnostics {}
  depends_on = [
    azurerm_subnet_network_security_group_association.executor,
    azurerm_subnet_nat_gateway_association.executor,
    azurerm_nat_gateway_public_ip_association.executor,
  ]
}

output "executor_vnet_id" { value = azurerm_virtual_network.executor.id }
output "executor_vm_id" { value = azurerm_linux_virtual_machine.executor.id }
output "state_account" { value = local.account }
