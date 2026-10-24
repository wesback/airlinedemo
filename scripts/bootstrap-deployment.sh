#!/bin/bash
set -euo pipefail

if [ -z "${AZURE_SUBSCRIPTION_ID:-}" ]; then
  printf 'Missing required deployment configuration: AZURE_SUBSCRIPTION_ID\n' >&2; exit 2
fi
if [ -z "${AZURE_TENANT_ID:-}" ]; then
  printf 'Missing required deployment configuration: AZURE_TENANT_ID\n' >&2; exit 2
fi
if [ -z "${RESOURCE_GROUP_NAME:-}" ]; then
  printf 'Missing required deployment configuration: RESOURCE_GROUP_NAME\n' >&2; exit 2
fi
if [ -z "${TFVARS_FILE:-}" ]; then
  printf 'Missing required deployment configuration: TFVARS_FILE\n' >&2; exit 2
fi
if [ -z "${BACKEND_CONFIG_FILE:-}" ]; then
  printf 'Missing required deployment configuration: BACKEND_CONFIG_FILE\n' >&2; exit 2
fi

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

# terraform -chdir resolves relative paths against infra/terraform, so make them absolute first.
case "$TFVARS_FILE" in /*) ;; *) TFVARS_FILE="$PWD/$TFVARS_FILE" ;; esac
case "$BACKEND_CONFIG_FILE" in /*) ;; *) BACKEND_CONFIG_FILE="$PWD/$BACKEND_CONFIG_FILE" ;; esac
cd "$(dirname "$0")/.."
terraform -chdir=infra/terraform init -backend-config="$BACKEND_CONFIG_FILE"
# The X's must end the template for BSD (macOS) mktemp; the plan holds sensitive values, so it is removed on exit.
plan_dir=$(mktemp -d /tmp/airline-deployment.XXXXXX)
trap 'rm -rf "$plan_dir"' EXIT
plan_file="$plan_dir/deployment.tfplan"
terraform -chdir=infra/terraform plan -var-file="$TFVARS_FILE" -out="$plan_file"
printf 'Saved Terraform plan: %s\n' "$plan_file"
terraform -chdir=infra/terraform show "$plan_file"

printf 'Apply this saved Terraform plan? Type yes to continue: '
IFS= read -r confirmation || confirmation=
if [ "$confirmation" != "yes" ]; then
  printf 'Terraform apply declined; no changes were applied.\n' >&2
  exit 4
fi

terraform -chdir=infra/terraform apply "$plan_file"
