#!/bin/bash
set -euo pipefail

resource_group=${RESOURCE_GROUP_NAME:-}
if [ -z "$resource_group" ]; then
  printf 'Missing required deployment configuration: RESOURCE_GROUP_NAME\n' >&2
  exit 2
fi

confirmed=false
for argument in "$@"; do
  if [ "$argument" = "--yes" ]; then
    confirmed=true
  fi
done
if [ "$confirmed" != true ]; then
  printf 'Destroy requires explicit confirmation: pass --yes\n' >&2
  exit 2
fi

for name in AZURE_SUBSCRIPTION_ID AZURE_TENANT_ID; do
  eval "value=\${${name}-}"
  if [ -z "$value" ]; then
    printf 'Missing required deployment configuration: %s\n' "$name" >&2
    exit 2
  fi
done

for command_name in terraform az; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    printf 'Required command is not available: %s\n' "$command_name" >&2
    exit 3
  fi
done

logged_tenant=$(az account show --query tenantId --output tsv) || exit 3
logged_subscription=$(az account show --query id --output tsv) || exit 3

if [ "$(printf '%s' "$logged_tenant" | tr '[:upper:]' '[:lower:]')" != "$(printf '%s' "$AZURE_TENANT_ID" | tr '[:upper:]' '[:lower:]')" ]; then
  printf 'Azure tenant mismatch: expected %s, logged in to %s\n' "$AZURE_TENANT_ID" "$logged_tenant" >&2
  exit 3
fi
if [ "$(printf '%s' "$logged_subscription" | tr '[:upper:]' '[:lower:]')" != "$(printf '%s' "$AZURE_SUBSCRIPTION_ID" | tr '[:upper:]' '[:lower:]')" ]; then
  printf 'Azure subscription mismatch: expected %s, logged in to %s\n' "$AZURE_SUBSCRIPTION_ID" "$logged_subscription" >&2
  exit 3
fi

if [ -z "${BACKEND_CONFIG_FILE:-}" ]; then
  printf 'Missing required deployment configuration: BACKEND_CONFIG_FILE\n' >&2
  exit 2
fi
if [ -z "${TFVARS_FILE:-}" ]; then
  printf 'Missing required deployment configuration: TFVARS_FILE\n' >&2
  exit 2
fi

terraform -chdir=infra/terraform init -backend-config="$BACKEND_CONFIG_FILE"
# The backend file selects the state; make sure that state is the deployment named by RESOURCE_GROUP_NAME
# before anything is destroyed, so a backend that points at another deployment is refused.
state_output=$(terraform -chdir=infra/terraform state show -no-color azurerm_resource_group.demo) || {
  printf 'Cannot read the resource group from the Terraform state selected by %s\n' "$BACKEND_CONFIG_FILE" >&2
  exit 3
}
state_group=$(printf '%s\n' "$state_output" | awk '$1 == "name" && $2 == "=" { gsub(/"/, "", $3); print $3; exit }')
if [ "$state_group" != "$resource_group" ]; then
  printf 'Terraform state mismatch: the selected state belongs to resource group "%s", not "%s"; refusing to destroy\n' "$state_group" "$resource_group" >&2
  exit 3
fi

terraform -chdir=infra/terraform destroy -var-file="$TFVARS_FILE" -var="resource_group_name=$resource_group" -auto-approve

printf 'Destroyed deployment resources for %s.\n' "$resource_group"
