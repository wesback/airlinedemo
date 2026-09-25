#!/usr/bin/env bash
set -Eeuo pipefail

# Demo/mock-only bootstrap. This is not a production deployment workflow.

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
TERRAFORM_ROOT="${AIRLINEDEMO_TERRAFORM_ROOT:-$REPOSITORY_ROOT/terraform}"
CONFIG_FILE="${AIRLINEDEMO_CONFIG_FILE:-$TERRAFORM_ROOT/examples/demo.tfvars}"
OPERATOR_VARS_FILE="${AIRLINEDEMO_OPERATOR_VARS_FILE:-}"
RESOURCE_GROUP_NAME="${AIRLINEDEMO_RESOURCE_GROUP_NAME:-rg-airlinedemo-swc-demo}"
DEPLOYMENT_NAME="${AIRLINEDEMO_DEPLOYMENT_NAME:-airlinedemo-swc-demo}"
REGISTRY_NAME="${AIRLINEDEMO_REGISTRY_NAME:-acrairlinedemoswcdemo}"
IMAGE_REPOSITORY="${AIRLINEDEMO_IMAGE_REPOSITORY:-airlinedemo}"
FIXTURE_ROOT="${AIRLINEDEMO_FIXTURE_ROOT:-$REPOSITORY_ROOT/.airlinedemo-fixtures}"
DRY_RUN="${AIRLINEDEMO_DRY_RUN:-${AIRLINEDEMO_MOCK:-0}}"
RUN_ID="${AIRLINEDEMO_RUN_ID:-}"

usage() {
  printf 'Usage: %s [--dry-run] [--help]\n' "$0"
  printf '  --dry-run  print the complete demo flow without Azure or Docker calls\n'
}

fail() {
  printf 'ERROR: %s\n' "$*" >&2
  exit 1
}

run_step() {
  local step="$1"
  shift
  if [[ "${AIRLINEDEMO_FAIL_STEP:-}" == "$step" ]]; then
    fail "step '$step' failed (simulated failure)"
  fi
  if [[ "$DRY_RUN" == "1" ]]; then
    printf '[dry-run] %s:' "$step"
    printf ' %q' "$@"
    printf '\n'
    return 0
  fi
  if ! "$@"; then
    fail "step '$step' failed"
  fi
}

require_tool() {
  command -v "$1" >/dev/null 2>&1 ||
    fail "required tool '$1' is missing; install it before bootstrapping the demo"
}

require_file() {
  [[ -f "$1" ]] || fail "required file is missing: $1"
}

while (($# > 0)); do
  case "$1" in
    --dry-run)
      DRY_RUN=1
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      fail "unknown option '$1'"
      ;;
  esac
  shift
done

if [[ -z "$RUN_ID" && -f "$FIXTURE_ROOT/generator-contract.json" ]]; then
  command -v jq >/dev/null 2>&1 ||
    fail "required tool 'jq' is missing; install it before reading the fixture run ID"
  RUN_ID="$(jq -er '.configuration.runId' "$FIXTURE_ROOT/generator-contract.json")" ||
    fail "unable to read configuration.runId from $FIXTURE_ROOT/generator-contract.json"
fi
RUN_ID="${RUN_ID:-RUN-DEMO-001}"
IMAGE_TAG="${AIRLINEDEMO_IMAGE_TAG:-demo-$RUN_ID}"
IMAGE_REF="${REGISTRY_NAME}.azurecr.io/${IMAGE_REPOSITORY}:${IMAGE_TAG}"
API_BASE_URL="${AIRLINEDEMO_API_BASE_URL:-https://<container-app-fqdn>}"

if [[ "$DRY_RUN" != "1" ]]; then
  [[ -n "$OPERATOR_VARS_FILE" ]] ||
    fail "AIRLINEDEMO_OPERATOR_VARS_FILE is required and must point to private Terraform values"
  require_file "$CONFIG_FILE"
  require_file "$OPERATOR_VARS_FILE"
  [[ -n "${AIRLINEDEMO_SUBSCRIPTION_ID:-}" ]] ||
    fail "AIRLINEDEMO_SUBSCRIPTION_ID is required"
  [[ -n "${AIRLINEDEMO_TENANT_ID:-}" ]] ||
    fail "AIRLINEDEMO_TENANT_ID is required"
  [[ -n "${AIRLINEDEMO_AUTH_HEADER:-}" ]] ||
    fail "AIRLINEDEMO_AUTH_HEADER is required for the authenticated fixture load"
  for tool in az terraform docker dotnet jq curl sqlcmd; do
    require_tool "$tool"
  done
fi

TF_ARGS=(-var-file="$CONFIG_FILE")
if [[ -n "$OPERATOR_VARS_FILE" ]]; then
  TF_ARGS+=(-var-file="$OPERATOR_VARS_FILE")
fi
TF_IMAGE_ARGS=(-var="container_image=$IMAGE_REF")

run_step azure-subscription az account set --subscription "${AIRLINEDEMO_SUBSCRIPTION_ID:-00000000-0000-0000-0000-000000000000}"
run_step terraform-init terraform -chdir="$TERRAFORM_ROOT" init -input=false

# The registry is the only target created before the image exists. The full
# apply therefore never creates a Container App pointing at an unpushed image.
run_step terraform-registry terraform -chdir="$TERRAFORM_ROOT" apply \
  "${TF_ARGS[@]}" "${TF_IMAGE_ARGS[@]}" \
  -target=module.container_apps.azurerm_container_registry.application \
  -auto-approve -input=false
run_step image-build docker build --pull --file "$REPOSITORY_ROOT/Dockerfile" \
  --tag "$IMAGE_REF" "$REPOSITORY_ROOT"
run_step registry-login az acr login --name "$REGISTRY_NAME"
run_step image-push docker push "$IMAGE_REF"
run_step terraform-apply terraform -chdir="$TERRAFORM_ROOT" apply \
  "${TF_ARGS[@]}" "${TF_IMAGE_ARGS[@]}" -auto-approve -input=false

if [[ "$DRY_RUN" == "1" ]]; then
  API_BASE_URL="https://<container-app-fqdn>"
else
  SQL_SERVER_OUTPUT="$(
    terraform -chdir="$TERRAFORM_ROOT" output -raw sql_server_fully_qualified_domain_name
  )" || fail "unable to read Terraform output sql_server_fully_qualified_domain_name"
  SQL_DATABASE_ID="$(
    terraform -chdir="$TERRAFORM_ROOT" output -raw sql_database_id
  )" || fail "unable to read Terraform output sql_database_id"
  [[ -n "$SQL_SERVER_OUTPUT" ]] ||
    fail "Terraform returned an empty SQL server FQDN"
  [[ "$SQL_DATABASE_ID" == */databases/* && -n "${SQL_DATABASE_ID##*/}" ]] ||
    fail "Terraform returned an invalid SQL database resource ID"
  export AIRLINEDEMO_SQL_SERVER="${AIRLINEDEMO_SQL_SERVER:-$SQL_SERVER_OUTPUT}"
  export AIRLINEDEMO_SQL_DATABASE="${AIRLINEDEMO_SQL_DATABASE:-${SQL_DATABASE_ID##*/}}"

  API_HOST="$(
    az containerapp show \
      --name "ca-$DEPLOYMENT_NAME" \
      --resource-group "$RESOURCE_GROUP_NAME" \
      --query properties.configuration.ingress.fqdn \
      --output tsv
  )" || fail "unable to resolve the deployed Container App endpoint"
  [[ -n "$API_HOST" ]] || fail "Azure returned an empty Container App endpoint"
  API_BASE_URL="https://$API_HOST"
fi

export AIRLINEDEMO_API_BASE_URL="$API_BASE_URL"
export AIRLINEDEMO_FIXTURE_ROOT="$FIXTURE_ROOT"
if [[ "$DRY_RUN" == "1" ]]; then
  run_step migrate "$SCRIPT_DIR/migrate-demo.sh" --dry-run
  run_step fixtures "$SCRIPT_DIR/load-demo-fixtures.sh" --dry-run
  run_step smoke curl --fail --silent --show-error "$API_BASE_URL/"
else
  run_step migrate "$SCRIPT_DIR/migrate-demo.sh"
  run_step fixtures "$SCRIPT_DIR/load-demo-fixtures.sh"
  run_step smoke curl --fail --silent --show-error "$API_BASE_URL/"
fi

printf '\nDEMO_BOOTSTRAP_SUCCESS\n'
printf 'mode=%s\n' "$([[ "$DRY_RUN" == "1" ]] && printf 'dry-run' || printf 'live')"
printf 'resource_group=%s\n' "$RESOURCE_GROUP_NAME"
printf 'deployment=%s\n' "$DEPLOYMENT_NAME"
printf 'image=%s\n' "$IMAGE_REF"
printf 'app_endpoint=%s\n' "$API_BASE_URL"
printf 'fixture_root=%s\n' "$FIXTURE_ROOT"
printf 'next_check=Open app_endpoint and submit the seeded synthetic package.\n'
