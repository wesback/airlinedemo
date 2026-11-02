mock_provider "azurerm" {}
mock_provider "azapi" {}

variables {
  subscription_id = "00000000-0000-0000-0000-000000000000"
  tenant_id       = "11111111-1111-1111-1111-111111111111"
  operator_id     = "22222222-2222-2222-2222-222222222222"
  prefix          = "demo1"
  ssh_public_key  = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOpZeKzLEfnqDq3z1vOjV6MWyV5GNymcNKWMlb4pFO7V"
}

run "private_bootstrap" {
  command = plan
  assert {
    condition = (
      azapi_resource.state.body.properties.publicNetworkAccess == "Disabled" &&
      !azapi_resource.state.body.properties.allowSharedKeyAccess &&
      !azapi_resource.state.body.properties.allowBlobPublicAccess &&
      azapi_resource.blob.body.properties.isVersioningEnabled &&
      azapi_resource.container.body.properties.publicAccess == "None"
    )
    error_message = "State must be private, Entra-only, non-anonymous and versioned."
  }
  assert {
    condition = (
      !azurerm_subnet.executor.default_outbound_access_enabled &&
      azurerm_linux_virtual_machine.executor.disable_password_authentication &&
      azurerm_linux_virtual_machine.executor.secure_boot_enabled &&
      azurerm_linux_virtual_machine.executor.vtpm_enabled &&
      azurerm_network_interface.executor.ip_configuration[0].public_ip_address_id == null &&
      one(azurerm_network_security_group.executor.security_rule).access == "Deny" &&
      one(azurerm_network_security_group.executor.security_rule).source_address_prefix == "*"
    )
    error_message = "Executor must have no public NIC/password/SSH ingress or implicit outbound connectivity."
  }
  assert {
    condition     = azurerm_private_endpoint.state.private_service_connection[0].subresource_names == tolist(["blob"])
    error_message = "State connectivity must use a Blob private endpoint."
  }
}

run "reject_unsafe_prefix" {
  command = plan
  variables { prefix = "../other" }
  expect_failures = [var.prefix]
}
