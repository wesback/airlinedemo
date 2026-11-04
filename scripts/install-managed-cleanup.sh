#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  printf 'Usage: %s <AirlineDemo.Deployment.dll> <protected cleanup configuration JSON>\n' "$0" >&2
  exit 2
fi

assembly=$1
configuration=$2
safe_path='^/[A-Za-z0-9._/-]+$'
if [[ ! $assembly =~ $safe_path || ! $configuration =~ $safe_path
    || $assembly =~ (^|/)\.\.(/|$) || $configuration =~ (^|/)\.\.(/|$)
    || $assembly != */AirlineDemo.Deployment.dll || $configuration != *.json
    || ! -r $assembly || ! -r $configuration ]]; then
  printf 'Cleanup assembly and configuration must be readable absolute paths with supported names.\n' >&2
  exit 2
fi

command -v systemctl >/dev/null 2>&1 || {
  printf 'systemctl is required to install managed cleanup scheduling.\n' >&2
  exit 1
}
command -v dotnet >/dev/null 2>&1 || {
  printf 'dotnet is required to run managed cleanup.\n' >&2
  exit 1
}
command -v runuser >/dev/null 2>&1 || {
  printf 'runuser is required to verify the cleanup service account.\n' >&2
  exit 1
}
systemctl --version >/dev/null
systemctl show-environment >/dev/null
dotnet_path=$(command -v dotnet)
[[ $dotnet_path =~ $safe_path ]] || {
  printf 'dotnet must resolve to an absolute path supported by systemd.\n' >&2
  exit 1
}
runuser -u azureuser -- test -r "$assembly"
runuser -u azureuser -- test -r "$configuration"

service_unit=airlinedemo-managed-cleanup.service
timer_unit=airlinedemo-managed-cleanup.timer

unit_active() {
  local unit=$1 status
  if systemctl is-active --quiet "$unit"; then
    return 0
  else
    status=$?
  fi
  if [[ $status -eq 3 || $status -eq 4 ]]; then
    return 1
  fi
  printf 'Cannot determine systemd state for %s.\n' "$unit" >&2
  return "$status"
}

if unit_active "$service_unit"; then
  printf 'Refusing to replace cleanup units while a cleanup command is running.\n' >&2
  exit 1
fi

timer_was_active=false
if unit_active "$timer_unit"; then
  timer_was_active=true
  systemctl stop "$timer_unit"
fi

if unit_active "$service_unit"; then
  if [[ $timer_was_active == true ]]; then
    systemctl start "$timer_unit"
  fi
  printf 'Refusing to replace cleanup units while a cleanup command is running.\n' >&2
  exit 1
fi

service_file=$(mktemp)
timer_file=$(mktemp)
restore_timer() {
  local status=$?
  trap - EXIT
  rm -f -- "$service_file" "$timer_file"
  if [[ $status -ne 0 && $timer_was_active == true ]]; then
    if ! systemctl start "$timer_unit"; then
      printf 'Failed to restore the previously active cleanup timer.\n' >&2
      status=1
    fi
  fi
  exit "$status"
}
trap restore_timer EXIT

cat > "$service_file" <<EOF
[Unit]
Description=Airline Demo bounded managed-conversation cleanup
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
User=azureuser
Group=azureuser
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
ExecStart=$dotnet_path $assembly managed-cleanup $configuration
TimeoutStartSec=11min
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
EOF

cat > "$timer_file" <<EOF
[Unit]
Description=Hourly Airline Demo managed-conversation cleanup

[Timer]
OnCalendar=hourly
Persistent=true
Unit=$service_unit

[Install]
WantedBy=timers.target
EOF

install -m 0644 "$service_file" "/etc/systemd/system/$service_unit"
install -m 0644 "$timer_file" "/etc/systemd/system/$timer_unit"
systemctl daemon-reload
systemctl enable --now "$timer_unit"
