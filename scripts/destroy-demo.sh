#!/usr/bin/env bash
set -Eeuo pipefail

# Demo/mock-only teardown. It never deletes protected Terraform state or shared resources.

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
TERRAFORM_ROOT="${AIRLINEDEMO_TERRAFORM_ROOT:-$REPOSITORY_ROOT/terraform}"
CONFIG_FILE="${AIRLINEDEMO_CONFIG_FILE:-$TERRAFORM_ROOT/examples/demo.tfvars}"
OPERATOR_VARS_FILE="${AIRLINEDEMO_OPERATOR_VARS_FILE:-}"
RESOURCE_GROUP_NAME="${AIRLINEDEMO_RESOURCE_GROUP_NAME:-rg-airlinedemo-swc-demo}"
DEPLOYMENT_NAME="${AIRLINEDEMO_DEPLOYMENT_NAME:-airlinedemo-swc-demo}"
PLAN_FILE="${AIRLINEDEMO_DESTROY_PLAN_FILE:-$REPOSITORY_ROOT/.airlinedemo-destroy.tfplan}"
DRY_RUN="${AIRLINEDEMO_DRY_RUN:-${AIRLINEDEMO_MOCK:-0}}"
CONFIRMED=0

fail() {
  printf 'ERROR: %s\n' "$*" >&2
  exit 1
}

usage() {
  printf 'Usage: %s [--yes] [--dry-run] [--help]\n' "$0"
  printf '  --yes      explicit non-interactive confirmation for disposable demo resources\n'
  printf '  --dry-run  print the teardown without deleting anything\n'
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

while (($# > 0)); do
  case "$1" in
    --yes) CONFIRMED=1 ;;
    --dry-run) DRY_RUN=1 ;;
    --help|-h)
      usage
      exit 0
      ;;
    *) fail "unknown option '$1'" ;;
  esac
  shift
done

if [[ "$DRY_RUN" != "1" ]]; then
  for tool in az terraform; do
    command -v "$tool" >/dev/null 2>&1 ||
      fail "required tool '$tool' is missing; install it before teardown"
  done
  [[ -n "$OPERATOR_VARS_FILE" ]] ||
    fail "AIRLINEDEMO_OPERATOR_VARS_FILE is required and must point to private Terraform values"
  [[ -f "$CONFIG_FILE" ]] || fail "required file is missing: $CONFIG_FILE"
  [[ -f "$OPERATOR_VARS_FILE" ]] || fail "required file is missing: $OPERATOR_VARS_FILE"
  [[ -n "${AIRLINEDEMO_SUBSCRIPTION_ID:-}" ]] ||
    fail "AIRLINEDEMO_SUBSCRIPTION_ID is required"
  if ((CONFIRMED == 0)); then
    printf 'This deletes only Terraform-owned demo resources in %s (%s).\n' \
      "$RESOURCE_GROUP_NAME" "$DEPLOYMENT_NAME"
    printf 'Protected remote state, shared resources, and fixture source files remain.\n'
    printf 'Type DELETE AIRLINEDEMO DEMO to continue: '
    read -r confirmation
    [[ "$confirmation" == "DELETE AIRLINEDEMO DEMO" ]] ||
      fail "confirmation did not match; no resources were deleted"
  fi
else
  CONFIRMED=1
fi

TF_ARGS=(-var-file="$CONFIG_FILE")
if [[ -n "$OPERATOR_VARS_FILE" ]]; then
  TF_ARGS+=(-var-file="$OPERATOR_VARS_FILE")
fi

run_step azure-subscription az account set --subscription "${AIRLINEDEMO_SUBSCRIPTION_ID:-00000000-0000-0000-0000-000000000000}"
run_step terraform-init terraform -chdir="$TERRAFORM_ROOT" init -input=false
run_step terraform-destroy-plan terraform -chdir="$TERRAFORM_ROOT" plan \
  "${TF_ARGS[@]}" -destroy -out="$PLAN_FILE" -input=false
run_step terraform-destroy terraform -chdir="$TERRAFORM_ROOT" apply \
  "$PLAN_FILE" -input=false

if [[ "$DRY_RUN" != "1" ]]; then
  rm -f -- "$PLAN_FILE"
fi

printf '\nDEMO_DESTROY_SUCCESS\n'
printf 'resource_group=%s\n' "$RESOURCE_GROUP_NAME"
printf 'deployment=%s\n' "$DEPLOYMENT_NAME"
printf 'app_endpoint=%s\n' 'removed-after-destroy'
printf 'remaining=protected-remote-state-and-any-provider-retained-billing-artifacts\n'
printf 'audit=terraform-plan-and-apply-completed; verify-costs-in-Azure-Cost-Management\n'
