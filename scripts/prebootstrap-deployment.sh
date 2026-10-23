#!/bin/bash
set -uo pipefail

REGISTER_PROVIDERS=false
ISSUES=0

usage() {
  cat <<'EOF'
Usage: scripts/prebootstrap-deployment.sh [--register-providers]
  --register-providers  explicitly register the Azure resource providers needed for deployment
EOF
}

issue() {
  printf 'MISSING: %s\n' "$*" >&2
  ISSUES=$((ISSUES + 1))
}

for argument in "$@"; do
  case "$argument" in
    --register-providers) REGISTER_PROVIDERS=true ;;
    --help|-h) usage; exit 0 ;;
    *) printf 'Unknown argument: %s\n' "$argument" >&2; usage >&2; exit 2 ;;
  esac
done

printf 'Airline Demo deployment pre-bootstrap checks\n'

for name in AZURE_SUBSCRIPTION_ID AZURE_TENANT_ID RESOURCE_GROUP_NAME TFVARS_FILE BACKEND_CONFIG_FILE; do
  eval "value=\${${name}-}"
  if [ -z "$value" ]; then
    issue "required deployment environment variable $name is not set"
  fi
done

for command_name in terraform az; do
  if command -v "$command_name" >/dev/null 2>&1; then
    printf 'OK: tool %s is installed\n' "$command_name"
  else
    issue "required tool $command_name is not installed or not in PATH"
  fi
done

logged_tenant=""
logged_subscription=""
if command -v az >/dev/null 2>&1 && az account show >/dev/null 2>&1; then
  logged_tenant=$(az account show --query tenantId --output tsv 2>/dev/null || true)
  logged_subscription=$(az account show --query id --output tsv 2>/dev/null || true)
  printf 'OK: Azure CLI is authenticated\n'
  printf 'INFO: current subscription=%s\n' "$logged_subscription"
  printf 'INFO: current tenant=%s\n' "$logged_tenant"

  if [ -n "${AZURE_TENANT_ID:-}" ] &&
     [ "$(printf '%s' "${AZURE_TENANT_ID}" | tr '[:upper:]' '[:lower:]')" !=
       "$(printf '%s' "$logged_tenant" | tr '[:upper:]' '[:lower:]')" ]; then
    issue "AZURE_TENANT_ID does not match the Azure CLI tenant"
  fi
  if [ -n "${AZURE_SUBSCRIPTION_ID:-}" ] &&
     [ "$(printf '%s' "${AZURE_SUBSCRIPTION_ID}" | tr '[:upper:]' '[:lower:]')" !=
       "$(printf '%s' "$logged_subscription" | tr '[:upper:]' '[:lower:]')" ]; then
    issue "AZURE_SUBSCRIPTION_ID does not match the Azure CLI subscription"
  fi
else
  issue "Azure CLI is not authenticated; run az login --tenant <tenant-id>"
fi

if [ -n "${TFVARS_FILE:-}" ]; then
  if [ -f "$TFVARS_FILE" ]; then
    printf 'OK: Terraform values file exists: %s\n' "$TFVARS_FILE"
  else
    issue "TFVARS_FILE does not exist: $TFVARS_FILE"
  fi
fi

if [ -n "${BACKEND_CONFIG_FILE:-}" ]; then
  if [ -f "$BACKEND_CONFIG_FILE" ]; then
    printf 'OK: protected Terraform backend configuration exists: %s\n' "$BACKEND_CONFIG_FILE"
  else
    issue "BACKEND_CONFIG_FILE does not exist: $BACKEND_CONFIG_FILE"
  fi
fi

if command -v az >/dev/null 2>&1 && [ -n "$logged_subscription" ]; then
  required_providers='Microsoft.App Microsoft.Authorization Microsoft.CognitiveServices Microsoft.ContainerRegistry Microsoft.Insights Microsoft.KeyVault Microsoft.ManagedIdentity Microsoft.Network Microsoft.OperationalInsights Microsoft.Resources Microsoft.Sql Microsoft.Storage'
  for provider in $required_providers; do
    [ -n "$provider" ] || continue
    state=$(az provider show --namespace "$provider" --query registrationState --output tsv 2>/dev/null || true)
    if [ "$state" = "Registered" ]; then
      printf 'OK: provider %s is Registered\n' "$provider"
    elif [ "$REGISTER_PROVIDERS" = true ]; then
      printf 'ACTION: registering provider %s\n' "$provider"
      az provider register --namespace "$provider" >/dev/null || issue "Azure provider registration failed for $provider"
    else
      issue "Azure provider $provider is not Registered"
    fi
  done
fi

if command -v terraform >/dev/null 2>&1; then
  if terraform -chdir=infra/terraform fmt -check -recursive >/dev/null 2>&1; then
    printf 'OK: Terraform formatting check passed\n'
  else
    issue "Terraform formatting check failed; run terraform -chdir=infra/terraform fmt -recursive"
  fi
fi

if [ "$ISSUES" -eq 0 ]; then
  printf '\nDEPLOYMENT_PREBOOTSTRAP_READY\n'
  exit 0
fi

printf '\nDEPLOYMENT_PREBOOTSTRAP_BLOCKED issues=%d\n' "$ISSUES"
exit 1
