terraform {
  required_version = ">= 1.16.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.0"
    }
  }

  # Entra auth only; the azurerm backend takes a blob lease for state locking.
  # Backend blocks cannot read variables, so terraform_state_backend validates the
  # intended settings and CI must not override use_azuread_auth via -backend-config.
  backend "azurerm" {
    use_azuread_auth = true
  }
}

provider "azurerm" {
  subscription_id = var.subscription_id
  tenant_id       = var.tenant_id
  # Storage has shared keys disabled, so container management must use Entra auth.
  storage_use_azuread = true
  features {}
}

provider "azuread" {
  tenant_id = var.tenant_id
}

locals {
  rg_scope = "/subscriptions/${var.subscription_id}/resourceGroups/${local.resource_group_name}"

  private_dns_zones = {
    sql   = "privatelink.database.windows.net"
    blob  = "privatelink.blob.core.windows.net"
    model = "privatelink.openai.azure.com"
    kv    = "privatelink.vaultcore.azure.net"
    acr   = "privatelink.azurecr.io"
  }

  private_endpoints = {
    sql   = { resource_id = azurerm_mssql_server.sql.id, subresource = "sqlServer", zone = "sql" }
    blob  = { resource_id = azurerm_storage_account.blob.id, subresource = "blob", zone = "blob" }
    model = { resource_id = azurerm_cognitive_account.model.id, subresource = "account", zone = "model" }
    kv    = { resource_id = azurerm_key_vault.kv.id, subresource = "vault", zone = "kv" }
    acr   = { resource_id = azurerm_container_registry.acr.id, subresource = "registry", zone = "acr" }
  }

  presenter_reviewer_rules = { for i, c in var.presenter_reviewer_cidrs : tostring(i) => c }
  egress_rules             = { for i, c in var.egress_allowed_destinations : tostring(i) => c }

  gateway_region = coalesce(var.gateway_region, var.location)
  # The client appends /openai/v1/chat/completions; the gateway API path is "openai".
  model_endpoint                        = coalesce(var.gateway_endpoint, azurerm_api_management.gateway.gateway_url)
  gateway_model_deployment              = coalesce(var.gateway_model_deployment, local.model_deployment_name)
  gateway_approved_application_identity = coalesce(var.gateway_approved_application_identity, azurerm_user_assigned_identity.app.principal_id)

  # Platform traffic a workload-profiles Container Apps environment needs when outbound is denied by default.
  # These are Azure service tags, not internet destinations; app egress stays limited to egress_allowed_destinations.
  container_apps_platform_egress = {
    entra     = { priority = 200, destination = "AzureActiveDirectory", protocol = "Tcp", ports = ["443"] }
    monitor   = { priority = 201, destination = "AzureMonitor", protocol = "Tcp", ports = ["443"] }
    mcr       = { priority = 202, destination = "MicrosoftContainerRegistry", protocol = "Tcp", ports = ["443"] }
    frontdoor = { priority = 203, destination = "AzureFrontDoor.FirstParty", protocol = "Tcp", ports = ["443"] }
    dns       = { priority = 204, destination = "168.63.129.16", protocol = "*", ports = ["53"] }
  }
}

resource "azurerm_resource_group" "demo" {
  name     = local.resource_group_name
  location = var.location
}

resource "azurerm_virtual_network" "demo" {
  name                = "vnet-demo"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name
  address_space       = var.vnet_address_space
}

resource "azurerm_subnet" "app" {
  name                 = "snet-app"
  resource_group_name  = azurerm_resource_group.demo.name
  virtual_network_name = azurerm_virtual_network.demo.name
  address_prefixes     = [local.app_subnet_cidr]

  delegation {
    name = "container-apps-environment"

    service_delegation {
      name    = "Microsoft.App/environments"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

resource "azurerm_subnet" "private_endpoints" {
  name                              = "snet-private-endpoints"
  resource_group_name               = azurerm_resource_group.demo.name
  virtual_network_name              = azurerm_virtual_network.demo.name
  address_prefixes                  = [local.private_endpoint_subnet_cidr]
  private_endpoint_network_policies = "Enabled"
}

# API Management VNet injection needs a dedicated subnet with an NSG that admits its management plane.
resource "azurerm_subnet" "gateway" {
  name                 = "snet-apim"
  resource_group_name  = azurerm_resource_group.demo.name
  virtual_network_name = azurerm_virtual_network.demo.name
  address_prefixes     = [local.gateway_subnet_cidr]
}

resource "azurerm_network_security_group" "gateway" {
  name                = "nsg-apim"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name

  security_rule {
    name                       = "allow-apim-management"
    priority                   = 100
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = "3443"
    source_address_prefix      = "ApiManagement"
    destination_address_prefix = "VirtualNetwork"
  }

  security_rule {
    name                       = "allow-load-balancer"
    priority                   = 110
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = "6390"
    source_address_prefix      = "AzureLoadBalancer"
    destination_address_prefix = "VirtualNetwork"
  }
}

resource "azurerm_subnet_network_security_group_association" "gateway" {
  subnet_id                 = azurerm_subnet.gateway.id
  network_security_group_id = azurerm_network_security_group.gateway.id
}

# Allow rules exist only for configured CIDRs; priority 4096 denies are the default.
resource "azurerm_network_security_group" "app" {
  name                = "nsg-app"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name
}

resource "azurerm_subnet_network_security_group_association" "app" {
  subnet_id                 = azurerm_subnet.app.id
  network_security_group_id = azurerm_network_security_group.app.id
}

resource "azurerm_network_security_rule" "ingress_allow" {
  for_each                    = local.presenter_reviewer_rules
  name                        = "allow-presenter-reviewer-${each.key}"
  priority                    = 100 + tonumber(each.key)
  direction                   = "Inbound"
  access                      = "Allow"
  protocol                    = "Tcp"
  source_port_range           = "*"
  destination_port_range      = "443"
  source_address_prefix       = each.value
  destination_address_prefix  = local.app_subnet_cidr
  resource_group_name         = azurerm_resource_group.demo.name
  network_security_group_name = azurerm_network_security_group.app.name
}

resource "azurerm_network_security_rule" "ingress_deny_all" {
  name                        = "deny-all-inbound"
  priority                    = 4096
  direction                   = "Inbound"
  access                      = "Deny"
  protocol                    = "*"
  source_port_range           = "*"
  destination_port_range      = "*"
  source_address_prefix       = "*"
  destination_address_prefix  = "*"
  resource_group_name         = azurerm_resource_group.demo.name
  network_security_group_name = azurerm_network_security_group.app.name
}

resource "azurerm_network_security_rule" "egress_private_endpoints" {
  name                        = "allow-private-endpoints-443"
  priority                    = 100
  direction                   = "Outbound"
  access                      = "Allow"
  protocol                    = "Tcp"
  source_port_range           = "*"
  destination_port_range      = "443"
  source_address_prefix       = local.app_subnet_cidr
  destination_address_prefix  = local.private_endpoint_subnet_cidr
  resource_group_name         = azurerm_resource_group.demo.name
  network_security_group_name = azurerm_network_security_group.app.name
}

resource "azurerm_network_security_rule" "egress_private_endpoints_sql" {
  name                        = "allow-private-endpoints-sql"
  priority                    = 101
  direction                   = "Outbound"
  access                      = "Allow"
  protocol                    = "Tcp"
  source_port_range           = "*"
  destination_port_ranges     = ["1433", "11000-11999"]
  source_address_prefix       = local.app_subnet_cidr
  destination_address_prefix  = local.private_endpoint_subnet_cidr
  resource_group_name         = azurerm_resource_group.demo.name
  network_security_group_name = azurerm_network_security_group.app.name
}

# The environment's own components talk to each other inside the infrastructure subnet.
resource "azurerm_network_security_rule" "egress_gateway" {
  name                        = "allow-model-gateway-443"
  priority                    = 102
  direction                   = "Outbound"
  access                      = "Allow"
  protocol                    = "Tcp"
  source_port_range           = "*"
  destination_port_range      = "443"
  source_address_prefix       = local.app_subnet_cidr
  destination_address_prefix  = local.gateway_subnet_cidr
  resource_group_name         = azurerm_resource_group.demo.name
  network_security_group_name = azurerm_network_security_group.app.name
}

resource "azurerm_network_security_rule" "app_subnet_internal_inbound" {
  name                        = "allow-app-subnet-internal-inbound"
  priority                    = 300
  direction                   = "Inbound"
  access                      = "Allow"
  protocol                    = "*"
  source_port_range           = "*"
  destination_port_range      = "*"
  source_address_prefix       = local.app_subnet_cidr
  destination_address_prefix  = local.app_subnet_cidr
  resource_group_name         = azurerm_resource_group.demo.name
  network_security_group_name = azurerm_network_security_group.app.name
}

# Load balancer health probes; without them ingress never becomes healthy.
resource "azurerm_network_security_rule" "app_load_balancer_inbound" {
  name                        = "allow-azure-load-balancer"
  priority                    = 301
  direction                   = "Inbound"
  access                      = "Allow"
  protocol                    = "Tcp"
  source_port_range           = "*"
  destination_port_range      = "30000-32767"
  source_address_prefix       = "AzureLoadBalancer"
  destination_address_prefix  = local.app_subnet_cidr
  resource_group_name         = azurerm_resource_group.demo.name
  network_security_group_name = azurerm_network_security_group.app.name
}

resource "azurerm_network_security_rule" "app_subnet_internal_outbound" {
  name                        = "allow-app-subnet-internal-outbound"
  priority                    = 300
  direction                   = "Outbound"
  access                      = "Allow"
  protocol                    = "*"
  source_port_range           = "*"
  destination_port_range      = "*"
  source_address_prefix       = local.app_subnet_cidr
  destination_address_prefix  = local.app_subnet_cidr
  resource_group_name         = azurerm_resource_group.demo.name
  network_security_group_name = azurerm_network_security_group.app.name
}

resource "azurerm_network_security_rule" "egress_container_apps_platform" {
  for_each                    = local.container_apps_platform_egress
  name                        = "allow-platform-${each.key}"
  priority                    = each.value.priority
  direction                   = "Outbound"
  access                      = "Allow"
  protocol                    = each.value.protocol
  source_port_range           = "*"
  destination_port_ranges     = each.value.ports
  source_address_prefix       = local.app_subnet_cidr
  destination_address_prefix  = each.value.destination
  resource_group_name         = azurerm_resource_group.demo.name
  network_security_group_name = azurerm_network_security_group.app.name
}

resource "azurerm_network_security_rule" "egress_allow" {
  for_each                    = local.egress_rules
  name                        = "allow-egress-${each.key}"
  priority                    = 110 + tonumber(each.key)
  direction                   = "Outbound"
  access                      = "Allow"
  protocol                    = "Tcp"
  source_port_range           = "*"
  destination_port_range      = "443"
  source_address_prefix       = local.app_subnet_cidr
  destination_address_prefix  = each.value
  resource_group_name         = azurerm_resource_group.demo.name
  network_security_group_name = azurerm_network_security_group.app.name
}

resource "azurerm_network_security_rule" "egress_deny_all" {
  name                        = "deny-all-outbound"
  priority                    = 4096
  direction                   = "Outbound"
  access                      = "Deny"
  protocol                    = "*"
  source_port_range           = "*"
  destination_port_range      = "*"
  source_address_prefix       = "*"
  destination_address_prefix  = "*"
  resource_group_name         = azurerm_resource_group.demo.name
  network_security_group_name = azurerm_network_security_group.app.name
}

resource "azurerm_mssql_server" "sql" {
  name                          = local.sql_server_name
  resource_group_name           = azurerm_resource_group.demo.name
  location                      = azurerm_resource_group.demo.location
  version                       = "12.0"
  minimum_tls_version           = "1.2"
  public_network_access_enabled = false

  azuread_administrator {
    login_username              = var.sql_admin_group_name
    object_id                   = var.sql_admin_group_object_id
    azuread_authentication_only = true
  }
}

resource "azurerm_mssql_server_transparent_data_encryption" "sql" {
  server_id = azurerm_mssql_server.sql.id
}

resource "azurerm_storage_account" "blob" {
  name                              = local.storage_account_name
  resource_group_name               = azurerm_resource_group.demo.name
  location                          = azurerm_resource_group.demo.location
  account_tier                      = "Standard"
  account_replication_type          = "LRS"
  min_tls_version                   = "TLS1_2"
  https_traffic_only_enabled        = true
  public_network_access_enabled     = false
  allow_nested_items_to_be_public   = false
  shared_access_key_enabled         = false
  default_to_oauth_authentication   = true
  infrastructure_encryption_enabled = true

  # Evidence versioning and 14-day recovery of deleted blobs and containers (RETENTION-01).
  blob_properties {
    versioning_enabled = true

    delete_retention_policy {
      days = 14
    }

    container_delete_retention_policy {
      days = 14
    }
  }
}

resource "azurerm_key_vault" "kv" {
  name                       = local.key_vault_name
  resource_group_name        = azurerm_resource_group.demo.name
  location                   = azurerm_resource_group.demo.location
  tenant_id                  = var.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  purge_protection_enabled   = true
  soft_delete_retention_days = 90
  # Public access stays disabled (managed-tenant policy forces it); deployer_ip_cidrs are admitted through the
  # network security perimeter below, so a Terraform runner outside the VNet can create the signing certificate.
  public_network_access_enabled = false

  network_acls {
    default_action = "Deny"
    bypass         = "AzureServices"
    ip_rules       = []
  }
}

# Enforced perimeter: with publicNetworkAccess Disabled, only these inbound rules (and private endpoints) reach the vault.
resource "azurerm_network_security_perimeter" "deployer" {
  count               = length(var.deployer_ip_cidrs) > 0 ? 1 : 0
  name                = "nsp-${local.name_prefix}-${local.name_suffix}"
  resource_group_name = azurerm_resource_group.demo.name
  location            = azurerm_resource_group.demo.location
}

resource "azurerm_network_security_perimeter_profile" "deployer" {
  count                         = length(azurerm_network_security_perimeter.deployer)
  name                          = "deployer"
  network_security_perimeter_id = azurerm_network_security_perimeter.deployer[0].id
}

resource "azurerm_network_security_perimeter_access_rule" "deployer" {
  count                                 = length(azurerm_network_security_perimeter.deployer)
  name                                  = "deployer-ip"
  network_security_perimeter_profile_id = azurerm_network_security_perimeter_profile.deployer[0].id
  direction                             = "Inbound"
  address_prefixes                      = var.deployer_ip_cidrs
}

resource "azurerm_network_security_perimeter_association" "key_vault" {
  count                                 = length(azurerm_network_security_perimeter.deployer)
  name                                  = "key-vault"
  network_security_perimeter_profile_id = azurerm_network_security_perimeter_profile.deployer[0].id
  resource_id                           = azurerm_key_vault.kv.id
  access_mode                           = "Enforced"

  depends_on = [azurerm_network_security_perimeter_access_rule.deployer]
}

# Key Vault uses RBAC, so the Terraform principal needs data-plane rights to manage the signing certificate.
resource "azurerm_role_assignment" "terraform_kv_certificates" {
  scope                = azurerm_key_vault.kv.id
  role_definition_name = "Key Vault Certificates Officer"
  principal_id         = var.terraform_principal_object_id
}

# The deployer stores the demo passcode and the runner token between the platform and application phases.
resource "azurerm_role_assignment" "terraform_kv_secrets" {
  scope                = azurerm_key_vault.kv.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = var.terraform_principal_object_id
}

resource "azurerm_cognitive_account" "model" {
  name                          = local.model_account_name
  resource_group_name           = azurerm_resource_group.demo.name
  location                      = "swedencentral"
  kind                          = "OpenAI"
  sku_name                      = "S0"
  custom_subdomain_name         = local.model_endpoint_subdomain
  public_network_access_enabled = false
  local_auth_enabled            = false

  network_acls {
    default_action = "Deny"
  }
}

resource "azurerm_api_management" "gateway" {
  name                = local.gateway_name
  location            = local.gateway_region
  resource_group_name = azurerm_resource_group.demo.name
  publisher_name      = "Airline Demo"
  publisher_email     = "owner@example.invalid"
  sku_name            = "${var.gateway_api_management_tier}_1"
  # Azure rejects publicNetworkAccess = Disabled when the service is created; Internal VNet mode
  # already gives the gateway only a private address.
  public_network_access_enabled = true
  virtual_network_type          = "Internal"

  virtual_network_configuration {
    subnet_id = azurerm_subnet.gateway.id
  }

  identity {
    type = "SystemAssigned"
  }

  depends_on = [azurerm_subnet_network_security_group_association.gateway]

  lifecycle {
    precondition {
      condition     = lower(replace(local.gateway_region, " ", "")) == lower(replace(var.location, " ", ""))
      error_message = "gateway_region must equal location: API Management can only join a virtual network in its own region."
    }
  }
}

resource "azurerm_role_assignment" "gateway_cognitive_services" {
  scope                = azurerm_cognitive_account.model.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_api_management.gateway.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

# Internal mode publishes no public DNS for the gateway, so the VNet resolves its default hostnames privately.
# This zone answers for all of azure-api.net inside the VNet; other API Management instances need records here too.
resource "azurerm_private_dns_zone" "gateway" {
  name                = "azure-api.net"
  resource_group_name = azurerm_resource_group.demo.name
}

resource "azurerm_private_dns_zone_virtual_network_link" "gateway" {
  name                  = "link-gateway"
  resource_group_name   = azurerm_resource_group.demo.name
  private_dns_zone_name = azurerm_private_dns_zone.gateway.name
  virtual_network_id    = azurerm_virtual_network.demo.id
}

resource "azurerm_private_dns_a_record" "gateway" {
  for_each            = toset(["", ".portal", ".developer", ".management", ".scm"])
  name                = "${local.gateway_name}${each.key}"
  zone_name           = azurerm_private_dns_zone.gateway.name
  resource_group_name = azurerm_resource_group.demo.name
  ttl                 = 300
  records             = azurerm_api_management.gateway.private_ip_addresses
}

resource "azurerm_api_management_api" "gateway_model" {
  name                  = "foundry-model-gateway"
  resource_group_name   = azurerm_resource_group.demo.name
  api_management_name   = azurerm_api_management.gateway.name
  revision              = "1"
  display_name          = "Foundry private model gateway"
  path                  = "openai"
  protocols             = ["https"]
  subscription_required = false
  # The public host name matches the service certificate; the linked private DNS zone resolves it to the private endpoint.
  service_url = "https://${local.model_endpoint_subdomain}.openai.azure.com/openai"
}

resource "azurerm_api_management_api_operation" "gateway_chat_completions" {
  operation_id        = "chat-completions"
  api_name            = azurerm_api_management_api.gateway_model.name
  api_management_name = azurerm_api_management.gateway.name
  resource_group_name = azurerm_resource_group.demo.name
  display_name        = "Private chat completions"
  method              = "POST"
  url_template        = "v1/chat/completions"
}

resource "azurerm_api_management_api_operation_policy" "gateway_chat_completions" {
  api_name            = azurerm_api_management_api.gateway_model.name
  api_management_name = azurerm_api_management.gateway.name
  resource_group_name = azurerm_resource_group.demo.name
  operation_id        = azurerm_api_management_api_operation.gateway_chat_completions.operation_id
  xml_content         = <<XML
<policies>
  <inbound>
    <base />
    <validate-jwt header-name="Authorization" failed-validation-httpcode="401" failed-validation-error-message="gateway caller identity rejected">
      <openid-config url="https://login.microsoftonline.com/${var.tenant_id}/v2.0/.well-known/openid-configuration" />
      <audiences>
        <audience>${local.gateway_audience}</audience>
        <audience>${azuread_application.gateway.client_id}</audience>
      </audiences>
      <required-claims>
        <claim name="oid" match="any">
          <value>${local.gateway_approved_application_identity}</value>
        </claim>
        <claim name="roles" match="any">
          <value>Gateway.Invoke</value>
        </claim>
      </required-claims>
    </validate-jwt>
    <choose>
      <when condition="@{
        var headers = new[] { &quot;x-gateway-arm&quot;, &quot;x-gateway-run&quot;, &quot;x-gateway-package&quot;, &quot;x-gateway-case&quot;, &quot;x-gateway-model-configuration&quot;, &quot;x-gateway-suite-version&quot;, &quot;x-gateway-call-identity&quot; };
        return headers.Any(name => !context.Request.Headers.ContainsKey(name) || !System.Text.RegularExpressions.Regex.IsMatch(context.Request.Headers.GetValueOrDefault(name, &quot;&quot;), &quot;^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$&quot;));
      }">
        <return-response>
          <set-status code="400" reason="Bad Request" />
          <set-body>gateway correlation rejected</set-body>
        </return-response>
      </when>
      <when condition='@{
        var model = context.Request.Body.As<Newtonsoft.Json.Linq.JObject>(preserveContent: true)?["model"]?.ToString();
        return ${join(" && ", [for allowed in local.gateway_allowed_models : "model != \"${allowed}\""])};
      }'>
        <return-response>
          <set-status code="400" reason="Unsupported model" />
          <set-body>{"error":{"code":"unsupported_model","message":"Only the configured model deployments are allowed."}}</set-body>
        </return-response>
      </when>
    </choose>
    <rewrite-uri template="/v1/chat/completions" copy-unmatched-params="false" />
    <authentication-managed-identity resource="https://ai.azure.com" />
  </inbound>
</policies>
XML
}

resource "azurerm_cognitive_deployment" "model" {
  name                 = local.model_deployment_name
  cognitive_account_id = azurerm_cognitive_account.model.id

  model {
    format  = "OpenAI"
    name    = var.model_name
    version = var.model_version
  }

  sku {
    name     = "GlobalStandard"
    capacity = var.model_capacity
  }
}

# The gateway admits the app's deployment and, when configured, the evaluation arm C deployment.
locals {
  gateway_allowed_models = compact([local.gateway_model_deployment, var.evaluation_model_name])
}

# Evaluation arm C: a larger model compared against the app's model on the frozen suite (DD-044).
resource "azurerm_cognitive_deployment" "evaluation" {
  count                = var.evaluation_model_name == null ? 0 : 1
  name                 = var.evaluation_model_name
  cognitive_account_id = azurerm_cognitive_account.model.id

  model {
    format  = "OpenAI"
    name    = var.evaluation_model_name
    version = var.evaluation_model_version
  }

  sku {
    name     = "GlobalStandard"
    capacity = var.evaluation_model_capacity
  }

  lifecycle {
    precondition {
      condition     = var.evaluation_model_version != null
      error_message = "evaluation_model_version is required when evaluation_model_name is set."
    }
  }
}

resource "azurerm_private_dns_zone" "demo" {
  for_each            = local.private_dns_zones
  name                = each.value
  resource_group_name = azurerm_resource_group.demo.name
}

resource "azurerm_private_dns_zone_virtual_network_link" "demo" {
  for_each              = local.private_dns_zones
  name                  = "link-${each.key}"
  resource_group_name   = azurerm_resource_group.demo.name
  private_dns_zone_name = azurerm_private_dns_zone.demo[each.key].name
  virtual_network_id    = azurerm_virtual_network.demo.id
}

resource "azurerm_private_endpoint" "demo" {
  for_each            = local.private_endpoints
  name                = "pe-${each.key}"
  resource_group_name = azurerm_resource_group.demo.name
  location            = azurerm_resource_group.demo.location
  subnet_id           = azurerm_subnet.private_endpoints.id

  private_service_connection {
    name                           = "psc-${each.key}"
    private_connection_resource_id = each.value.resource_id
    subresource_names              = [each.value.subresource]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "dzg-${each.key}"
    private_dns_zone_ids = [azurerm_private_dns_zone.demo[each.value.zone].id]
  }
}

resource "azurerm_role_assignment" "terraform_contributor" {
  scope                = local.rg_scope
  role_definition_name = "Contributor"
  principal_id         = var.terraform_principal_object_id

  # The scope is a literal ID, so the resource group must exist before the grant is created.
  depends_on = [azurerm_resource_group.demo]

  # Fails the plan if this existing Contributor grant would go to a workflow identity.
  lifecycle {
    precondition {
      condition     = lower(var.terraform_principal_object_id) != lower(azurerm_user_assigned_identity.build.principal_id) && lower(var.terraform_principal_object_id) != lower(azurerm_user_assigned_identity.release.principal_id)
      error_message = "terraform_principal_object_id must not be the build or release workflow identity, or the Contributor grant would hand that workflow subscription-wide rights."
    }
  }
}

resource "azurerm_consumption_budget_resource_group" "demo" {
  name              = "budget-${local.resource_group_name}"
  resource_group_id = azurerm_resource_group.demo.id
  amount            = var.budget_amount
  time_grain        = "Monthly"

  time_period {
    start_date = var.budget_start_date
  }

  notification {
    enabled        = true
    threshold      = 80
    operator       = "GreaterThan"
    contact_emails = var.budget_contact_emails
  }
}

output "model_endpoint" {
  description = "Private Foundry AI Gateway endpoint; secrets and model keys are never output."
  value       = local.model_endpoint
}
