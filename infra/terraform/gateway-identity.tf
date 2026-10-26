# Entra resource application that the model gateway's validate-jwt policy trusts. The application identity
# requests "<audience>/.default"; only principals holding the Gateway.Invoke app role are issued a token.

locals {
  gateway_invoke_role_id = "9b7eb471-01f8-471e-81a5-b8547e222692"
  # api://<client id> is always allowed by Entra and never collides with an earlier (soft-deleted) deployment.
  gateway_audience = var.gateway_audience != null ? var.gateway_audience : "api://${azuread_application.gateway.client_id}"
}

data "azuread_client_config" "current" {}

resource "azuread_application" "gateway" {
  display_name     = "${azurerm_api_management.gateway.name} model gateway"
  sign_in_audience = "AzureADMyOrg"
  owners           = [data.azuread_client_config.current.object_id]

  api {
    # v2 tokens carry the v2 issuer that the policy's v2 openid-config expects; aud is the client ID.
    requested_access_token_version = 2
  }

  app_role {
    id                   = local.gateway_invoke_role_id
    allowed_member_types = ["Application"]
    value                = "Gateway.Invoke"
    display_name         = "Invoke the private model gateway"
    description          = "Call the chat completions operation of the private model gateway."
    enabled              = true
  }

  lifecycle {
    ignore_changes = [identifier_uris]
  }
}

resource "azuread_application_identifier_uri" "gateway" {
  application_id = azuread_application.gateway.id
  identifier_uri = local.gateway_audience
}

resource "azuread_service_principal" "gateway" {
  client_id                    = azuread_application.gateway.client_id
  app_role_assignment_required = true
  owners                       = [data.azuread_client_config.current.object_id]
}

resource "azuread_app_role_assignment" "gateway_invoke" {
  app_role_id         = local.gateway_invoke_role_id
  principal_object_id = local.gateway_approved_application_identity
  resource_object_id  = azuread_service_principal.gateway.object_id
}

output "gateway_audience" {
  description = "Token audience the application requests (ModelGateway:Audience)."
  value       = local.gateway_audience
}
