#!/usr/bin/env bash
set -Eeuo pipefail

# Demo/mock-only migration hook. It never accepts a SQL password or connection string.

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
MIGRATIONS_ROOT="${AIRLINEDEMO_MIGRATIONS_ROOT:-$REPOSITORY_ROOT/database/migrations}"
DRY_RUN="${AIRLINEDEMO_DRY_RUN:-${AIRLINEDEMO_MOCK:-0}}"
RESOURCE_GROUP_NAME="${AIRLINEDEMO_RESOURCE_GROUP_NAME:-rg-airlinedemo-swc-demo}"
API_BASE_URL="${AIRLINEDEMO_API_BASE_URL:-https://<container-app-fqdn>}"

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

[[ -d "$MIGRATIONS_ROOT" ]] ||
  fail "migration directory is missing: $MIGRATIONS_ROOT"
mapfile -t MIGRATIONS < <(find "$MIGRATIONS_ROOT" -maxdepth 1 -type f -name '*.sql' -print | sort)
((${#MIGRATIONS[@]} > 0)) || fail "no versioned SQL migrations found in $MIGRATIONS_ROOT"

if [[ "$DRY_RUN" != "1" ]]; then
  command -v sqlcmd >/dev/null 2>&1 ||
    fail "required tool 'sqlcmd' is missing; install sqlcmd before migrating"
  [[ -n "${AIRLINEDEMO_SQL_SERVER:-}" ]] ||
    fail "AIRLINEDEMO_SQL_SERVER is required"
  [[ -n "${AIRLINEDEMO_SQL_DATABASE:-}" ]] ||
    fail "AIRLINEDEMO_SQL_DATABASE is required"
fi

for migration in "${MIGRATIONS[@]}"; do
  if [[ "${AIRLINEDEMO_FAIL_STEP:-}" == "migrate" ]]; then
    fail "step 'migrate' failed (simulated failure)"
  fi
  if [[ "$DRY_RUN" == "1" ]]; then
    printf '[dry-run] migrate: sqlcmd -G -S %q -d %q -i %q\n' \
      "${AIRLINEDEMO_SQL_SERVER:-<sql-server>}" \
      "${AIRLINEDEMO_SQL_DATABASE:-<sql-database>}" "$migration"
  elif ! sqlcmd -G -C -S "$AIRLINEDEMO_SQL_SERVER" \
      -d "$AIRLINEDEMO_SQL_DATABASE" -i "$migration" -b; then
    fail "migration failed: $(basename "$migration")"
  fi
done

printf '\nDEMO_MIGRATION_SUCCESS\n'
printf 'resource_group=%s\n' "$RESOURCE_GROUP_NAME"
printf 'app_endpoint=%s\n' "$API_BASE_URL"
printf 'migration_version=%s\n' "$(basename "${MIGRATIONS[${#MIGRATIONS[@]} - 1]}")"
