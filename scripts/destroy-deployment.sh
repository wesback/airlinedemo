#!/bin/bash
set -euo pipefail

for name in RESOURCE_GROUP_NAME TFVARS_FILE BACKEND_CONFIG_FILE AZURE_SUBSCRIPTION_ID AZURE_TENANT_ID; do
  eval "value=\${${name}-}"
  if [ -z "$value" ]; then
    printf 'Missing required deployment configuration: %s\n' "$name" >&2
    exit 2
  fi
done

resource_group=$RESOURCE_GROUP_NAME
plan_only=
apply_plan=
validate_only=false
confirmed=false
while [ $# -gt 0 ]; do
  case "$1" in
    --yes) confirmed=true; shift ;;
    --plan-only|--apply-plan|--check-plan)
      [ $# -ge 2 ] || { printf 'Missing plan path for %s\n' "$1" >&2; exit 2; }
      if [ "$1" = --plan-only ]; then plan_only=$2; else apply_plan=$2; fi
      if [ "$1" = --check-plan ]; then validate_only=true; fi
      shift 2 ;;
    *) printf 'Unknown destroy argument: %s\n' "$1" >&2; exit 2 ;;
  esac
done
[ -z "$plan_only" ] || [ -z "$apply_plan" ] || { printf 'Choose --plan-only or --apply-plan, not both\n' >&2; exit 2; }
if [ "$confirmed" != true ] && [ -z "$plan_only" ] && [ "$validate_only" != true ]; then
  printf 'Destroy requires explicit confirmation: pass --yes\n' >&2
  exit 2
fi

case "$TFVARS_FILE" in /*) ;; *) TFVARS_FILE="$PWD/$TFVARS_FILE" ;; esac
case "$BACKEND_CONFIG_FILE" in /*) ;; *) BACKEND_CONFIG_FILE="$PWD/$BACKEND_CONFIG_FILE" ;; esac
case "$plan_only" in ""|/*) ;; *) plan_only="$PWD/$plan_only" ;; esac
case "$apply_plan" in ""|/*) ;; *) apply_plan="$PWD/$apply_plan" ;; esac
cd "$(dirname "$0")/.."

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

terraform -chdir=infra/terraform init -backend-config="$BACKEND_CONFIG_FILE"
# The backend file selects the state; make sure that state is the deployment named by RESOURCE_GROUP_NAME
# before anything is destroyed, so a backend that points at another deployment is refused.
state_output=$(terraform -chdir=infra/terraform state show -no-color azurerm_resource_group.demo) || {
  printf 'Cannot read the resource group from the Terraform state selected by %s\n' "$BACKEND_CONFIG_FILE" >&2
  exit 3
}
state_id=$(printf '%s\n' "$state_output" | awk '$1 == "id" && $2 == "=" { $1=""; $2=""; sub(/^ +/, "", $0); gsub(/^"|"$/, "", $0); print $0; exit }')
if [ -z "$state_id" ]; then
  printf 'Cannot read the resource group ID from the Terraform state selected by %s\n' "$BACKEND_CONFIG_FILE" >&2
  exit 3
fi
expected_id="/subscriptions/$AZURE_SUBSCRIPTION_ID/resourceGroups/$resource_group"
lower_state_id=$(printf '%s' "$state_id" | tr '[:upper:]' '[:lower:]')
lower_expected_id=$(printf '%s' "$expected_id" | tr '[:upper:]' '[:lower:]')
if [ "$lower_state_id" != "$lower_expected_id" ]; then
  printf 'Terraform state mismatch: resource ID "%s" does not match "%s"; refusing to destroy\n' "$state_id" "$expected_id" >&2
  exit 3
fi

if [ -n "$plan_only" ]; then
  umask 077
  terraform -chdir=infra/terraform plan -destroy -var-file="$TFVARS_FILE" -var="resource_group_name=$resource_group" -out="$plan_only"
  printf 'Saved destroy plan for %s: %s\n' "$resource_group" "$plan_only"
  exit 0
fi

if [ -n "$apply_plan" ]; then
  terraform -chdir=infra/terraform show -json "$apply_plan" | python3 -c '
import json, sys
plan = json.load(sys.stdin)
expected = sys.argv[1].lower()
changes = plan.get("resource_changes", [])
group = [c for c in changes if c["address"] == "azurerm_resource_group.demo"]
if len(group) != 1 or group[0]["change"]["actions"] != ["delete"] or group[0]["change"]["before"]["id"].lower() != expected:
    raise SystemExit("Saved plan is not a destroy of the confirmed resource group")
if any(c["change"]["actions"] not in (["delete"], ["no-op"], ["read"]) for c in changes):
    raise SystemExit("Saved destroy plan contains a non-destroy change")
' "$expected_id"
  if [ "$validate_only" = true ]; then
    printf 'Verified saved destroy plan for %s; nothing applied.\n' "$resource_group"
    exit 0
  fi
  terraform -chdir=infra/terraform apply "$apply_plan"
else
  terraform -chdir=infra/terraform destroy -var-file="$TFVARS_FILE" -var="resource_group_name=$resource_group" -auto-approve
fi

printf 'Destroyed deployment resources for %s.\n' "$resource_group"
