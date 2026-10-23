#!/usr/bin/env bash
set -Eeuo pipefail

# Demo/mock-only preflight. It does not deploy resources or change Azure state
# unless --register-providers is explicitly supplied.

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
TERRAFORM_ROOT="${AIRLINEDEMO_TERRAFORM_ROOT:-$REPOSITORY_ROOT/terraform}"
CONFIG_FILE="${AIRLINEDEMO_CONFIG_FILE:-$TERRAFORM_ROOT/examples/demo.tfvars}"
OPERATOR_VARS_FILE="${AIRLINEDEMO_OPERATOR_VARS_FILE:-}"
FIXTURE_ROOT="${AIRLINEDEMO_FIXTURE_ROOT:-$REPOSITORY_ROOT/.airlinedemo-fixtures}"
RUN_ID="${AIRLINEDEMO_RUN_ID:-RUN-DEMO-001}"
REGISTER_PROVIDERS=0
GENERATE_FIXTURES=0
ISSUES=0

usage() {
  cat <<'EOF'
Usage: ./scripts/prebootstrap-demo.sh [--register-providers] [--generate-fixtures] [--help]
  --register-providers  explicitly register required Azure resource providers
  --generate-fixtures   regenerate the baseline synthetic fixture package
  --help                print this help
EOF
}

issue() {
  printf 'MISSING: %s\n' "$*" >&2
  ISSUES=$((ISSUES + 1))
}

active_var_is_configured() {
  local key="$1"
  local file="$2"
  local value
  value="$(
    awk -v key="$key" '
      /^[[:space:]]*#/ { next }
      $0 ~ /^[[:space:]]*[A-Za-z_][A-Za-z0-9_-]*[[:space:]]*=/ {
        line=$0
        sub(/^[[:space:]]*/, "", line)
        split(line, parts, /=/)
        name=parts[1]
        sub(/[[:space:]]*$/, "", name)
        if (name == key) {
          sub(/^[^=]*=[[:space:]]*/, "", line)
          gsub(/^[[:space:]"]+|[[:space:]"]+$/, "", line)
          print line
          exit
        }
      }
    ' "$file" 2>/dev/null
  )" || return 1
  [[ -n "$value" ]]
}

while (($# > 0)); do
  case "$1" in
    --register-providers)
      REGISTER_PROVIDERS=1
      ;;
    --generate-fixtures)
      GENERATE_FIXTURES=1
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      printf 'ERROR: unknown option %q\n' "$1" >&2
      usage >&2
      exit 2
      ;;
  esac
  shift
done

printf 'AirlineDemo pre-bootstrap checks\n'

for tool in az terraform docker dotnet jq curl sqlcmd; do
  if command -v "$tool" >/dev/null 2>&1; then
    printf 'OK: tool %s is installed\n' "$tool"
  else
    issue "required tool '$tool' is not installed or is not in PATH"
  fi
done

if command -v docker >/dev/null 2>&1; then
  if docker info >/dev/null 2>&1; then
    printf 'OK: Docker daemon is reachable\n'
  else
    issue "Docker daemon is not reachable; start Docker Desktop and rerun docker info"
  fi
fi

CURRENT_SUBSCRIPTION=""
CURRENT_TENANT=""
if command -v az >/dev/null 2>&1 && az account show --only-show-errors >/dev/null 2>&1; then
  CURRENT_SUBSCRIPTION="$(
    az account show --only-show-errors --query id --output tsv 2>/dev/null || true
  )"
  CURRENT_TENANT="$(
    az account show --only-show-errors --query tenantId --output tsv 2>/dev/null || true
  )"
  printf 'OK: Azure CLI is authenticated\n'
  printf 'INFO: current subscription=%s\n' "$CURRENT_SUBSCRIPTION"
  printf 'INFO: current tenant=%s\n' "$CURRENT_TENANT"
else
  issue "Azure CLI is not authenticated; run az login --tenant <tenant-id>"
fi

if [[ -z "${AIRLINEDEMO_SUBSCRIPTION_ID:-}" ]]; then
  issue "AIRLINEDEMO_SUBSCRIPTION_ID is not set"
elif [[ -n "$CURRENT_SUBSCRIPTION" &&
        "$AIRLINEDEMO_SUBSCRIPTION_ID" != "$CURRENT_SUBSCRIPTION" ]]; then
  issue "AIRLINEDEMO_SUBSCRIPTION_ID does not match the Azure CLI subscription"
fi

if [[ -z "${AIRLINEDEMO_TENANT_ID:-}" ]]; then
  issue "AIRLINEDEMO_TENANT_ID is not set"
elif [[ -n "$CURRENT_TENANT" &&
        "$AIRLINEDEMO_TENANT_ID" != "$CURRENT_TENANT" ]]; then
  issue "AIRLINEDEMO_TENANT_ID does not match the Azure CLI tenant"
fi

if [[ -f "$CONFIG_FILE" ]]; then
  printf 'OK: checked-in Terraform config exists: %s\n' "$CONFIG_FILE"
else
  issue "checked-in Terraform config is missing: $CONFIG_FILE"
fi

if [[ -z "$OPERATOR_VARS_FILE" ]]; then
  issue "AIRLINEDEMO_OPERATOR_VARS_FILE is not set"
else
  if [[ -f "$OPERATOR_VARS_FILE" ]]; then
    printf 'OK: private Terraform values file exists: %s\n' "$OPERATOR_VARS_FILE"

    for required_var in \
      azure_openai_model \
      azure_openai_model_version \
      sql_aad_admin_login \
      sql_aad_admin_object_id \
      container_allowed_source_ranges; do
      if ! active_var_is_configured "$required_var" "$OPERATOR_VARS_FILE"; then
        issue "private Terraform value '$required_var' is missing or empty"
      fi
    done

    if grep -Fq '203.0.113.10/32' "$OPERATOR_VARS_FILE"; then
      issue "container_allowed_source_ranges still contains the documentation-only IP 203.0.113.10/32"
    fi
    if grep -Eq 'private-[A-Za-z-]*admin|confirmed-model-name|confirmed-model-version|00000000-0000-0000-0000-000000000000' \
      "$OPERATOR_VARS_FILE"; then
      issue "private Terraform values contain an example placeholder"
    fi
    if command -v terraform >/dev/null 2>&1 &&
      ! terraform fmt -check -no-color "$OPERATOR_VARS_FILE" >/dev/null 2>&1; then
      issue "private Terraform values file has invalid Terraform syntax or formatting"
    fi
  else
    issue "AIRLINEDEMO_OPERATOR_VARS_FILE points to a missing file: $OPERATOR_VARS_FILE"
  fi
fi

if command -v az >/dev/null 2>&1 && [[ -n "$CURRENT_SUBSCRIPTION" ]]; then
  required_providers=(
    Microsoft.App
    Microsoft.Authorization
    Microsoft.CognitiveServices
    Microsoft.ContainerRegistry
    Microsoft.Insights
    Microsoft.ManagedIdentity
    Microsoft.OperationalInsights
    Microsoft.Resources
    Microsoft.Sql
    Microsoft.Storage
  )
  for provider in "${required_providers[@]}"; do
    state="$(
      az provider show --namespace "$provider" --query registrationState \
        --output tsv 2>/dev/null || true
    )"
    if [[ "$state" == "Registered" ]]; then
      printf 'OK: provider %s is Registered\n' "$provider"
    elif [[ "$REGISTER_PROVIDERS" == "1" ]]; then
      printf 'ACTION: registering provider %s\n' "$provider"
      if az provider register --namespace "$provider" --only-show-errors >/dev/null; then
        printf 'SUBMITTED: provider registration for %s\n' "$provider"
        ISSUES=$((ISSUES + 1))
      else
        issue "Azure provider registration failed for $provider"
      fi
    else
      issue "Azure provider $provider is not Registered"
    fi
  done

  if az extension show --name containerapp --only-show-errors >/dev/null 2>&1 ||
    az extension list --query "[?name=='containerapp'].name" --output tsv 2>/dev/null |
      grep -Fxq containerapp; then
    printf 'OK: Azure Container Apps CLI extension is installed\n'
  else
    issue "Azure Container Apps CLI extension 'containerapp' is not installed"
  fi

  if [[ -z "${AIRLINEDEMO_PREBOOTSTRAP_SKIP_BACKEND_CHECK:-}" ]]; then
    backend_name="$(
      az storage container show \
        --account-name stairlinedemostate \
        --name tfstate \
        --auth-mode login \
        --only-show-errors \
        --query name \
        --output tsv 2>/dev/null || true
    )"
    if [[ "$backend_name" == "tfstate" ]]; then
      printf 'OK: protected Terraform backend container is accessible\n'
    else
      issue "protected Terraform backend container tfstate is not accessible"
    fi
  fi
fi

if [[ "$GENERATE_FIXTURES" == "1" ]]; then
  if command -v dotnet >/dev/null 2>&1; then
    printf 'ACTION: generating baseline fixtures with run ID %s\n' "$RUN_ID"
    if ! (
      cd "$REPOSITORY_ROOT" &&
      dotnet run --project src/AirlineDemo.Generator -- \
        --output "$FIXTURE_ROOT" \
        --run-id "$RUN_ID" \
        --profile baseline
    ); then
      issue "fixture generation failed"
    fi
  else
    issue "dotnet is required to generate fixtures"
  fi
fi

MANIFEST="$FIXTURE_ROOT/application-inputs/package-001/manifest.json"
if [[ -f "$MANIFEST" ]] && command -v jq >/dev/null 2>&1; then
  manifest_run_id="$(jq -er '.runId' "$MANIFEST" 2>/dev/null || true)"
  airline_id="$(jq -er '.airlineId' "$MANIFEST" 2>/dev/null || true)"
  aircraft_id="$(jq -er '.aircraftId' "$MANIFEST" 2>/dev/null || true)"
  lease_id="$(jq -er '.leaseId' "$MANIFEST" 2>/dev/null || true)"
  if [[ -n "$manifest_run_id" && -n "$airline_id" &&
        -n "$aircraft_id" && -n "$lease_id" ]]; then
    auth_header="Bearer run=$manifest_run_id;airline=$airline_id;aircraft=$aircraft_id;lease=$lease_id"
    printf 'OK: fixture package scope is available\n'
    printf 'INFO: export AIRLINEDEMO_RUN_ID=%q\n' "$manifest_run_id"
    printf 'INFO: export AIRLINEDEMO_FIXTURE_ROOT=%q\n' "$FIXTURE_ROOT"
    printf 'INFO: export AIRLINEDEMO_AUTH_HEADER=%q\n' "$auth_header"
  else
    issue "fixture manifest does not contain runId, airlineId, aircraftId, and leaseId"
  fi
else
  issue "fixture manifest is missing; rerun with --generate-fixtures: $MANIFEST"
fi

if [[ "$ISSUES" -eq 0 ]]; then
  printf '\nDEMO_PREBOOTSTRAP_READY\n'
  exit 0
fi

printf '\nDEMO_PREBOOTSTRAP_BLOCKED issues=%d\n' "$ISSUES"
exit 1
