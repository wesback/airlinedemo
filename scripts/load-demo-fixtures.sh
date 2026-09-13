#!/usr/bin/env bash
set -Eeuo pipefail

# Demo/mock-only fixture loader. It submits synthetic data only.

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
FIXTURE_ROOT="${AIRLINEDEMO_FIXTURE_ROOT:-$REPOSITORY_ROOT/.airlinedemo-fixtures}"
API_BASE_URL="${AIRLINEDEMO_API_BASE_URL:-https://<container-app-fqdn>}"
RESOURCE_GROUP_NAME="${AIRLINEDEMO_RESOURCE_GROUP_NAME:-rg-airlinedemo-swc-demo}"
DRY_RUN="${AIRLINEDEMO_DRY_RUN:-${AIRLINEDEMO_MOCK:-0}}"

fail() {
  printf 'ERROR: %s\n' "$*" >&2
  exit 1
}

while (($# > 0)); do
  case "$1" in
    --dry-run) DRY_RUN=1 ;;
    --help|-h)
      printf 'Usage: %s [--dry-run]\n' "$0"
      exit 0
      ;;
    *) fail "unknown option '$1'" ;;
  esac
  shift
done

if [[ "$DRY_RUN" != "1" ]]; then
  command -v curl >/dev/null 2>&1 ||
    fail "required tool 'curl' is missing; install it before loading fixtures"
  command -v jq >/dev/null 2>&1 ||
    fail "required tool 'jq' is missing; install it before loading fixtures"
  [[ -n "${AIRLINEDEMO_AUTH_HEADER:-}" ]] ||
    fail "AIRLINEDEMO_AUTH_HEADER is required for the authenticated fixture load"
  [[ -d "$FIXTURE_ROOT" ]] || fail "fixture directory is missing: $FIXTURE_ROOT"
  [[ -f "$FIXTURE_ROOT/generator-contract.json" ]] ||
    fail "generator-contract.json is missing from $FIXTURE_ROOT"
  [[ -f "$FIXTURE_ROOT/application-inputs/package-001/manifest.json" ]] ||
    fail "package-001/manifest.json is missing from $FIXTURE_ROOT"
  jq -e '.runId and (.manifest | length > 0)' \
    "$FIXTURE_ROOT/application-inputs/package-001/manifest.json" >/dev/null ||
    fail "fixture package manifest is invalid"
fi

if [[ "${AIRLINEDEMO_FAIL_STEP:-}" == "fixtures" ]]; then
  fail "step 'fixtures' failed (simulated failure)"
fi

if [[ "$DRY_RUN" == "1" ]]; then
  RUN_ID="${AIRLINEDEMO_RUN_ID:-RUN-DEMO-001}"
  printf '[dry-run] fixture package: %s/application-inputs/package-001/manifest.json\n' \
    "$FIXTURE_ROOT"
  printf '[dry-run] POST %s/api/packages (authenticated synthetic package)\n' "$API_BASE_URL"
else
  manifest="$FIXTURE_ROOT/application-inputs/package-001/manifest.json"
  contract="$FIXTURE_ROOT/generator-contract.json"
  run_id="$(jq -er '.runId' "$manifest")"
  jq -e --arg run "$run_id" '.configuration.runId == $run' "$contract" >/dev/null ||
    fail "fixture package runId does not match generator-contract.json"
  request_file="$(mktemp)"
  response_file="$(mktemp)"
  status_file="$(mktemp)"
  cleanup() { rm -f "$request_file" "$response_file" "$status_file"; }
  trap cleanup EXIT
  jq --arg eventId "EVT-$run_id-INITIAL" \
    --arg correlationId "CORR-$run_id-INITIAL" '
    {
      event: {
        schemaVersion: .schemaVersion, eventId: $eventId,
        type: "package.submitted", runId: .runId, caseId: .caseId,
        airlineId: .airlineId, aircraftId: .aircraftId, leaseId: .leaseId,
        occurredAt: .submittedAt, scenarioEffectiveAt: .scenarioEffectiveAt,
        correlationId: $correlationId, payload: { packageId: .packageId }
      },
      package: .
    }' "$manifest" > "$request_file"
  curl --silent --show-error --fail-with-body \
    --request POST "$API_BASE_URL/api/packages" \
    --header "Authorization: $AIRLINEDEMO_AUTH_HEADER" \
    --header 'Content-Type: application/json' \
    --data-binary "@$request_file" > "$response_file" ||
    fail "fixture package submission failed"
  operation_id="$(jq -er '.operationId' "$response_file")" ||
    fail "fixture response did not contain operationId"
  jq -e '.caseId and .receiptId' "$response_file" >/dev/null ||
    fail "fixture response did not contain caseId and receiptId"
  curl --silent --show-error --fail-with-body \
    --request POST "$API_BASE_URL/api/operations/$operation_id/process" \
    --header "Authorization: $AIRLINEDEMO_AUTH_HEADER" \
    --header 'Content-Type: application/json' > "$status_file" ||
    fail "fixture operation processing failed"
  RUN_ID="$run_id"
fi

printf '\nDEMO_FIXTURES_SUCCESS\n'
printf 'resource_group=%s\n' "$RESOURCE_GROUP_NAME"
printf 'app_endpoint=%s\n' "$API_BASE_URL"
printf 'fixture_run=%s\n' "${RUN_ID:-${AIRLINEDEMO_RUN_ID:-RUN-DEMO-001}}"
printf 'fixture_scope=synthetic-application-inputs-only\n'
