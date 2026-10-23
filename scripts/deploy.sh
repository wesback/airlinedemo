#!/bin/bash
# One-command deployment: discovers what Azure CLI already knows, creates the Terraform state backend
# (unless --existing-backend is given), writes the variable and backend files outside the repository,
# then runs the pre-bootstrap checks and the bootstrap script (saved plan, explicit "yes" before apply).
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/deploy.sh --prefix <name> --location <region> --model-name <name> --model-version <version> \
         --ops-group-id <guid> --ops-group-name <name> --budget-amount <number> \
         --image-digest sha256:<hex> --passcode-secret-uri <uri> [options]

Required:
  --prefix <name>               3-10 lowercase letters/digits; every resource name derives from it
  --location <region>           Azure region, for example swedencentral
  --model-name, --model-version approved Azure OpenAI model and version
  --ops-group-id, --ops-group-name  Entra group that administers SQL and reads diagnostics
  --budget-amount <number>      monthly budget
  --image-digest <digest>       immutable application image digest
  --passcode-secret-uri <uri>   Key Vault secret URI of the demo passcode
Options:
  --presenter-cidrs <a,b>       presenter/reviewer CIDRs (default: none, which denies all ingress)
  --allow-my-ip                 add this machine's public address, looked up from api.ipify.org
  --budget-emails <a,b>         budget contacts (default: the signed-in user)
  --existing-backend <account>  use a state storage account an owner already created (container tfstate)
  --register-providers          register the Azure resource providers the deployment needs
  --prepare-only                write the files and stop before any Azure change
Values come from Azure CLI sign-in: tenant, subscription, your object id, name and email.
Files are written to $DEPLOY_STATE_DIR (default ~/.local/state/airlinedemo/<prefix>).
USAGE
}

fail() { printf 'deploy: %s\n' "$*" >&2; exit 2; }

PREFIX= LOCATION= MODEL_NAME= MODEL_VERSION= OPS_ID= OPS_NAME= BUDGET= DIGEST= PASSCODE_URI=
CIDRS= EMAILS= EXISTING= ALLOW_IP=false REGISTER=false PREPARE_ONLY=false
while [ $# -gt 0 ]; do
  [ "$1" = --help ] || [ "$1" = -h ] && { usage; exit 0; }
  case "$1" in
    --allow-my-ip) ALLOW_IP=true; shift; continue ;;
    --register-providers) REGISTER=true; shift; continue ;;
    --prepare-only) PREPARE_ONLY=true; shift; continue ;;
  esac
  [ $# -ge 2 ] || fail "missing value for $1"
  case "$1" in
    --prefix) PREFIX=$2 ;; --location) LOCATION=$2 ;; --model-name) MODEL_NAME=$2 ;;
    --model-version) MODEL_VERSION=$2 ;; --ops-group-id) OPS_ID=$2 ;; --ops-group-name) OPS_NAME=$2 ;;
    --budget-amount) BUDGET=$2 ;; --image-digest) DIGEST=$2 ;; --passcode-secret-uri) PASSCODE_URI=$2 ;;
    --presenter-cidrs) CIDRS=$2 ;; --budget-emails) EMAILS=$2 ;; --existing-backend) EXISTING=$2 ;;
    *) usage >&2; fail "unknown argument: $1" ;;
  esac
  shift 2
done

missing=
for pair in prefix:"$PREFIX" location:"$LOCATION" model-name:"$MODEL_NAME" model-version:"$MODEL_VERSION" \
            ops-group-id:"$OPS_ID" ops-group-name:"$OPS_NAME" budget-amount:"$BUDGET" \
            image-digest:"$DIGEST" passcode-secret-uri:"$PASSCODE_URI"; do
  [ -n "${pair#*:}" ] || missing="$missing --${pair%%:*}"
done
[ -z "$missing" ] || { usage >&2; fail "missing required options:$missing"; }

guid='^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
plain='^[A-Za-z0-9][A-Za-z0-9 ._@:/-]*$'
[[ $PREFIX =~ ^[a-z][a-z0-9]{2,9}$ ]] || fail "--prefix must be 3-10 lowercase letters or digits starting with a letter"
[[ $LOCATION =~ ^[a-z0-9]+$ ]] || fail "--location must be an Azure region name such as swedencentral"
[[ $OPS_ID =~ $guid ]] || fail "--ops-group-id must be a GUID"
[[ $BUDGET =~ ^[0-9]+$ ]] || fail "--budget-amount must be a whole number"
[[ $DIGEST =~ ^sha256:[0-9a-f]{64}$ ]] || fail "--image-digest must be sha256: followed by 64 lowercase hex characters"
[[ $PASSCODE_URI =~ ^https://[A-Za-z0-9.-]+/secrets/[A-Za-z0-9-]+(/[0-9a-f]+)?$ ]] || fail "--passcode-secret-uri must be a Key Vault secret URI"
for pair in model-name:"$MODEL_NAME" model-version:"$MODEL_VERSION" ops-group-name:"$OPS_NAME"; do
  [[ ${pair#*:} =~ $plain ]] || fail "--${pair%%:*} contains characters that are not allowed"
done
[ -z "$EXISTING" ] || [[ $EXISTING =~ ^[a-z0-9]{3,24}$ ]] || fail "--existing-backend must be a storage account name"

for tool in terraform az; do command -v "$tool" >/dev/null 2>&1 || { printf 'deploy: required tool %s is not installed\n' "$tool" >&2; exit 3; }; done
az account show >/dev/null 2>&1 || { printf 'deploy: Azure CLI is not signed in; run az login\n' >&2; exit 3; }
TENANT=$(az account show --query tenantId --output tsv)
SUBSCRIPTION=$(az account show --query id --output tsv)
PRINCIPAL=$(az ad signed-in-user show --query id --output tsv)
USER_NAME=$(az ad signed-in-user show --query displayName --output tsv)
USER_MAIL=$(az ad signed-in-user show --query "mail || userPrincipalName" --output tsv)
[[ $PRINCIPAL =~ $guid ]] || fail "could not read your object id from az ad signed-in-user show"
[[ $USER_NAME =~ $plain ]] || fail "your Azure display name has characters that are not allowed; pass a simpler account"
[[ $USER_MAIL =~ ^[^@[:space:]\"\\]+@[^@[:space:]\"\\]+$ ]] || fail "could not read a valid email address for the signed-in user"

if [ "$ALLOW_IP" = true ]; then
  my_ip=$(curl -fsS --max-time 10 https://api.ipify.org) || fail "could not look up the public address; use --presenter-cidrs"
  [[ $my_ip =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]] || fail "unexpected answer from the address lookup"
  CIDRS="${CIDRS:+$CIDRS,}$my_ip/32"
fi
EMAILS=${EMAILS:-$USER_MAIL}
cidr='^[0-9]{1,3}(\.[0-9]{1,3}){3}/[0-9]{1,2}$'
json_list() {
  local out= item
  IFS=, read -ra items <<<"$1"
  for item in "${items[@]}"; do
    [[ -z $item ]] && continue
    [[ $item =~ $2 ]] || fail "invalid list entry: $item"
    out="$out${out:+,}\"$item\""
  done
  printf '[%s]' "$out"
}
cidr_json=$(json_list "$CIDRS" "$cidr")
email_json=$(json_list "$EMAILS" '^[^@[:space:]"\\]+@[^@[:space:]"\\]+$')

suffix=$(printf '%s/%s' "$SUBSCRIPTION" "$PREFIX" | sha1sum | cut -c1-6)
STATE_RG="rg-$PREFIX-tfstate"
STATE_ACCOUNT=${EXISTING:-"st${PREFIX}tf${suffix}"}
DIR=${DEPLOY_STATE_DIR:-${XDG_STATE_HOME:-$HOME/.local/state}/airlinedemo}/$PREFIX
umask 077
mkdir -p "$DIR"
TFVARS="$DIR/deploy.tfvars.json"
BACKEND="$DIR/backend.hcl"
cat >"$TFVARS" <<JSON
{
  "subscription_id": "$SUBSCRIPTION",
  "tenant_id": "$TENANT",
  "location": "$LOCATION",
  "name_prefix": "$PREFIX",
  "presenter_reviewer_cidrs": $cidr_json,
  "sql_admin_group_name": "$OPS_NAME",
  "sql_admin_group_object_id": "$OPS_ID",
  "telemetry_reader_group_id": "$OPS_ID",
  "model_name": "$MODEL_NAME",
  "model_version": "$MODEL_VERSION",
  "terraform_principal_object_id": "$PRINCIPAL",
  "budget_amount": $BUDGET,
  "budget_start_date": "$(date -u +%Y-%m-01T00:00:00Z)",
  "budget_contact_emails": $email_json,
  "security_response_owner": { "name": "$USER_NAME", "email": "$USER_MAIL" },
  "image_digest": "$DIGEST",
  "demo_passcode_secret_uri": "$PASSCODE_URI",
  "terraform_state_backend": { "storage_account_name": "$STATE_ACCOUNT" }
}
JSON
cat >"$BACKEND" <<HCL
resource_group_name  = "$STATE_RG"
storage_account_name = "$STATE_ACCOUNT"
container_name       = "tfstate"
key                  = "demo.tfstate"
use_azuread_auth     = true
HCL
printf 'Wrote %s and %s\n' "$TFVARS" "$BACKEND"
if [ "$PREPARE_ONLY" = true ]; then exit 0; fi

if [ -z "$EXISTING" ]; then
  printf 'Creating the protected Terraform state backend %s in %s (Entra-only, no shared keys, versioned)\n' "$STATE_ACCOUNT" "$STATE_RG"
  az group create --name "$STATE_RG" --location "$LOCATION" --output none
  az storage account create --name "$STATE_ACCOUNT" --resource-group "$STATE_RG" --location "$LOCATION" \
    --sku Standard_LRS --allow-shared-key-access false --allow-blob-public-access false --min-tls-version TLS1_2 --output none
  az storage account blob-service-properties update --account-name "$STATE_ACCOUNT" --resource-group "$STATE_RG" \
    --enable-versioning true --output none
  account_id=$(az storage account show --name "$STATE_ACCOUNT" --resource-group "$STATE_RG" --query id --output tsv)
  az role assignment create --assignee-object-id "$PRINCIPAL" --assignee-principal-type User \
    --role "Storage Blob Data Owner" --scope "$account_id" --output none
  created=false
  for _ in 1 2 3 4 5 6 7 8 9 10 11 12; do
    if az storage container create --name tfstate --account-name "$STATE_ACCOUNT" --auth-mode login --output none 2>/dev/null; then
      created=true; break
    fi
    sleep "${DEPLOY_ROLE_WAIT_SECONDS:-10}"
  done
  [ "$created" = true ] || { printf 'deploy: the state container could not be created; the role assignment may still be propagating, rerun the command\n' >&2; exit 3; }
fi

export AZURE_SUBSCRIPTION_ID=$SUBSCRIPTION AZURE_TENANT_ID=$TENANT RESOURCE_GROUP_NAME="rg-$PREFIX"
export TFVARS_FILE=$TFVARS BACKEND_CONFIG_FILE=$BACKEND
here=$(cd "$(dirname "$0")" && pwd)
cd "$here/.."
if [ "$REGISTER" = true ]; then scripts/prebootstrap-deployment.sh --register-providers; else scripts/prebootstrap-deployment.sh; fi
scripts/bootstrap-deployment.sh
