variable "foundry_enabled" {
  description = "Enable the separately reviewed private Foundry candidate; does not replace retained resources."
  type        = bool
  default     = false
  nullable    = false
}

variable "foundry_agent_subnet_cidr" {
  description = "Optional dedicated private /24; defaults to the sixth /24 of the application VNet."
  type        = string
  default     = null
  validation {
    condition = var.foundry_agent_subnet_cidr == null ? true : (
      can(cidrnetmask(var.foundry_agent_subnet_cidr)) &&
      can(regex("/24$", var.foundry_agent_subnet_cidr)) &&
      can(regex("^(10\\.|172\\.(1[6-9]|2[0-9]|3[01])\\.|192\\.168\\.)", var.foundry_agent_subnet_cidr))
    )
    error_message = "The Foundry agent subnet must be an RFC1918 IPv4 /24."
  }
}

data "azurerm_virtual_network" "foundry_executor" {
  count               = var.foundry_enabled && var.deployment_executor != null ? 1 : 0
  name                = split("/", var.deployment_executor.vnet_id)[8]
  resource_group_name = split("/", var.deployment_executor.vnet_id)[4]
}

locals {
  foundry_agent_cidr = var.foundry_enabled ? coalesce(var.foundry_agent_subnet_cidr,
  cidrsubnet(var.vnet_address_space[0], 24 - tonumber(split("/", var.vnet_address_space[0])[1]), 5)) : null
  foundry_known_cidrs = var.foundry_enabled ? merge({
    agent     = local.foundry_agent_cidr
    app       = local.app_subnet_cidr
    endpoints = local.private_endpoint_subnet_cidr
    gateway   = local.gateway_subnet_cidr
    runner    = local.build_runner_subnet_cidr
    }, { for index, cidr in var.vnet_address_space : "vnet-${index}" => cidr },
    { for index, cidr in try(data.azurerm_virtual_network.foundry_executor[0].address_space, []) : "peer-${index}" => cidr }
  ) : {}
  foundry_ip_ranges = {
    for name, cidr in local.foundry_known_cidrs : name => [
      for host in [0, -1] : sum([
        for index, octet in split(".", cidrhost(cidr, host)) : tonumber(octet) * pow(256, 3 - index)
      ])
    ]
  }
  foundry_dns_zones = var.foundry_enabled ? {
    project  = "privatelink.services.ai.azure.com"
    document = "privatelink.documents.azure.com"
    search   = "privatelink.search.windows.net"
  } : {}
  foundry_platform_egress = var.foundry_enabled ? merge(local.container_apps_platform_egress, {
    data = { priority = 210, destination = local.private_endpoint_subnet_cidr, protocol = "Tcp", ports = ["443"] }
    self = { priority = 211, destination = local.foundry_agent_cidr, protocol = "*", ports = ["*"] }
  }) : {}
}

resource "azurerm_subnet" "foundry_agent" {
  count                           = var.foundry_enabled ? 1 : 0
  name                            = "snet-foundry-agent"
  resource_group_name             = azurerm_resource_group.demo.name
  virtual_network_name            = azurerm_virtual_network.demo.name
  address_prefixes                = [local.foundry_agent_cidr]
  default_outbound_access_enabled = false

  delegation {
    name = "foundry-agent"
    service_delegation {
      name    = "Microsoft.App/environments"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }

  lifecycle {
    precondition {
      condition     = var.location == "swedencentral"
      error_message = "Foundry agent networking is restricted to Sweden Central."
    }
    precondition {
      condition     = can(regex("^(10\\.|172\\.(1[6-9]|2[0-9]|3[01])\\.|192\\.168\\.)", local.foundry_agent_cidr))
      error_message = "Foundry agent networking requires an RFC1918 subnet."
    }
    precondition {
      condition = anytrue([
        for name, bounds in local.foundry_ip_ranges : startswith(name, "vnet-") &&
        bounds[0] <= local.foundry_ip_ranges.agent[0] && bounds[1] >= local.foundry_ip_ranges.agent[1]
      ])
      error_message = "The Foundry agent subnet must belong to the application VNet."
    }
    precondition {
      condition = alltrue([
        for name, bounds in local.foundry_ip_ranges :
        name == "agent" || startswith(name, "vnet-") ||
        local.foundry_ip_ranges.agent[1] < bounds[0] || local.foundry_ip_ranges.agent[0] > bounds[1]
      ])
      error_message = "The Foundry agent subnet must not overlap application, endpoint, gateway, runner or executor ranges."
    }
  }
}

resource "azurerm_network_security_group" "foundry_agent" {
  count               = var.foundry_enabled ? 1 : 0
  name                = "nsg-foundry-agent"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name

  dynamic "security_rule" {
    for_each = local.foundry_platform_egress
    content {
      name                       = "allow-${security_rule.key}"
      priority                   = security_rule.value.priority
      direction                  = "Outbound"
      access                     = "Allow"
      protocol                   = security_rule.value.protocol
      source_port_range          = "*"
      destination_port_range     = length(security_rule.value.ports) == 1 ? security_rule.value.ports[0] : null
      destination_port_ranges    = length(security_rule.value.ports) > 1 ? security_rule.value.ports : null
      source_address_prefix      = local.foundry_agent_cidr
      destination_address_prefix = security_rule.value.destination
    }
  }
  security_rule {
    name                       = "deny-other-egress"
    priority                   = 4096
    direction                  = "Outbound"
    access                     = "Deny"
    protocol                   = "*"
    source_port_range          = "*"
    destination_port_range     = "*"
    source_address_prefix      = "*"
    destination_address_prefix = "*"
  }
}

resource "azurerm_subnet_network_security_group_association" "foundry_agent" {
  count                     = var.foundry_enabled ? 1 : 0
  subnet_id                 = azurerm_subnet.foundry_agent[0].id
  network_security_group_id = azurerm_network_security_group.foundry_agent[0].id

  lifecycle {
    precondition {
      condition     = azurerm_network_security_group.foundry_agent[0].id != ""
      error_message = "A Foundry agent subnet requires its network security group."
    }
  }
}

resource "azurerm_private_dns_zone" "foundry" {
  for_each            = local.foundry_dns_zones
  name                = each.value
  resource_group_name = azurerm_resource_group.demo.name
}

resource "azurerm_private_dns_zone_virtual_network_link" "foundry_application" {
  for_each              = local.foundry_dns_zones
  name                  = "foundry-application"
  resource_group_name   = azurerm_resource_group.demo.name
  private_dns_zone_name = azurerm_private_dns_zone.foundry[each.key].name
  virtual_network_id    = azurerm_virtual_network.demo.id
  registration_enabled  = false
}

resource "azurerm_private_dns_zone_virtual_network_link" "foundry_executor" {
  for_each              = var.deployment_executor == null ? {} : local.foundry_dns_zones
  name                  = "deployment-executor"
  resource_group_name   = azurerm_resource_group.demo.name
  private_dns_zone_name = azurerm_private_dns_zone.foundry[each.key].name
  virtual_network_id    = var.deployment_executor.vnet_id
  registration_enabled  = false
}
