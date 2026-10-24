#!/bin/bash
# One-command deployment: discovers what Azure CLI already knows, creates the Terraform state backend
# (unless --existing-backend is given), writes the variable and backend files outside the repository,
# then runs the pre-bootstrap checks and the bootstrap script (saved plan, explicit "yes" before apply).
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
REPOSITORY_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)
MODEL_MANIFEST_FILE=${AIRLINEDEMO_MODEL_MANIFEST_FILE:-"$REPOSITORY_ROOT/config/model-manifest.json"}

usage() {
  cat <<'USAGE'
Usage: scripts/deploy.sh \
         [--image-digest sha256:<hex> --passcode-secret-uri <uri>] [options]

Defaults (override with the matching option):
  --prefix demo1                3-10 lowercase letters/digits; every resource name derives from it
  --location swedencentral      Azure region
  --model-name <name>           approved model from config/model-manifest.json
  --model-version <version>     approved model version from config/model-manifest.json
  --budget-amount 250           monthly budget
Application (second phase; give both or neither):
  --image-digest <digest>       first application image digest, already pushed and signed in this deployment's registry
  --passcode-secret-uri <uri>   Key Vault secret URI of the demo passcode in this deployment's vault
  Without them the first run creates the platform only and prints the steps before the second run.
  After the app exists, the release workflow rolls out new digests.
Options:
  --presenter-cidrs <a,b>       presenter/reviewer CIDRs (default: none, which denies all ingress)
  --allow-my-ip                 add this machine's public address, looked up from api.ipify.org
  --vault-allow-my-ip           let this machine reach the Key Vault data plane (needed to create the signing
                                certificate when Terraform runs outside the VNet); the vault stays deny-by-default
  --build-runner <owner/repo>   create the self-hosted runner VM (label private-acr) registered with this repository;
                                after the first run, store a GitHub token in the vault secret github-runner-token
  --runner-ssh-key <file>       runner admin public key (default: ~/.ssh/id_ed25519.pub)
  --budget-emails <a,b>         budget contacts (default: the signed-in user)
  --ops-group-name <name>       existing Entra group for organization deployments; defaults to the signed-in user
  --ops-group-id <guid>         existing Entra group object ID; may be used without --ops-group-name
  --existing-backend <account>  use a state storage account an owner already created (container tfstate)
  --register-providers          register the Azure resource providers the deployment needs
  --prepare-only                write the files and stop before any Azure change
Values come from Azure CLI sign-in: tenant, subscription, your object id, name and email.
Files are written to $DEPLOY_STATE_DIR (default ~/.local/state/airlinedemo/<prefix>).
USAGE
}

fail() { printf 'deploy: %s\n' "$*" >&2; exit 2; }

PREFIX=demo1 LOCATION=swedencentral MODEL_NAME= MODEL_VERSION= OPS_ID= OPS_NAME= BUDGET=250 DIGEST= PASSCODE_URI=
MODEL_DEPLOYMENT_NAME=
RUNNER_REPO= RUNNER_KEY_FILE=$HOME/.ssh/id_ed25519.pub CIDRS= EMAILS= EXISTING= ALLOW_IP=false VAULT_IP=false REGISTER=false PREPARE_ONLY=false
while [ $# -gt 0 ]; do
  [ "$1" = --help ] || [ "$1" = -h ] && { usage; exit 0; }
  case "$1" in
    --allow-my-ip) ALLOW_IP=true; shift; continue ;;
    --vault-allow-my-ip) VAULT_IP=true; shift; continue ;;
    --register-providers) REGISTER=true; shift; continue ;;
    --prepare-only) PREPARE_ONLY=true; shift; continue ;;
  esac
  [ $# -ge 2 ] || fail "missing value for $1"
  case "$1" in
    --prefix) PREFIX=$2 ;; --location) LOCATION=$2 ;; --model-name) MODEL_NAME=$2 ;;
    --model-version) MODEL_VERSION=$2 ;; --ops-group-id) OPS_ID=$2 ;; --ops-group-name) OPS_NAME=$2 ;;
    --budget-amount) BUDGET=$2 ;; --image-digest) DIGEST=$2 ;; --passcode-secret-uri) PASSCODE_URI=$2 ;;
    --presenter-cidrs) CIDRS=$2 ;; --budget-emails) EMAILS=$2 ;; --existing-backend) EXISTING=$2 ;;
    --build-runner) RUNNER_REPO=$2 ;; --runner-ssh-key) RUNNER_KEY_FILE=$2 ;;
    *) usage >&2; fail "unknown argument: $1" ;;
  esac
  shift 2
done

if { [ -n "$DIGEST" ] && [ -z "$PASSCODE_URI" ]; } || { [ -z "$DIGEST" ] && [ -n "$PASSCODE_URI" ]; }; then
  fail "give --image-digest and --passcode-secret-uri together, or neither for the platform-only first run"
fi

if ! command -v python3 >/dev/null 2>&1; then
  fail "python3 is required to read the approved model manifest"
fi
if ! MANIFEST_FIELDS=$(
  python3 - "$MODEL_MANIFEST_FILE" <<'PY'
import json
import sys

path = sys.argv[1]
try:
    with open(path, encoding="utf-8") as manifest_file:
        manifest = json.load(manifest_file)
except (OSError, json.JSONDecodeError) as error:
    print(f"deploy: could not read approved model manifest '{path}': {error}", file=sys.stderr)
    raise SystemExit(2)

required = ("deploymentName", "model", "modelVersion", "region")
missing = [name for name in required if not isinstance(manifest.get(name), str) or not manifest[name].strip()]
if manifest.get("approved") is not True:
    print("deploy: model manifest is not approved", file=sys.stderr)
    raise SystemExit(2)
if missing:
    print("deploy: approved model manifest is missing " + ", ".join(missing), file=sys.stderr)
    raise SystemExit(2)
print("|".join(manifest[name] for name in required))
PY
); then
  exit 2
fi
IFS='|' read -r MANIFEST_DEPLOYMENT_NAME MANIFEST_MODEL_NAME MANIFEST_MODEL_VERSION MANIFEST_REGION <<EOF
$MANIFEST_FIELDS
EOF

if [ -n "$MODEL_NAME" ] && [ "$MODEL_NAME" != "$MANIFEST_MODEL_NAME" ]; then
  fail "--model-name must match the approved model manifest '$MANIFEST_MODEL_NAME'"
fi
if [ -n "$MODEL_VERSION" ] && [ "$MODEL_VERSION" != "$MANIFEST_MODEL_VERSION" ]; then
  fail "--model-version must match the approved model manifest '$MANIFEST_MODEL_VERSION'"
fi
MODEL_DEPLOYMENT_NAME=$MANIFEST_DEPLOYMENT_NAME
MODEL_NAME=$MANIFEST_MODEL_NAME
MODEL_VERSION=$MANIFEST_MODEL_VERSION
if [ "$LOCATION" != "$MANIFEST_REGION" ]; then
  fail "--location must match the approved model manifest region '$MANIFEST_REGION'"
fi

guid='^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
plain='^[A-Za-z0-9][A-Za-z0-9 ._@:/-]*$'
[[ $PREFIX =~ ^[a-z][a-z0-9]{2,9}$ ]] || fail "--prefix must be 3-10 lowercase letters or digits starting with a letter"
[[ $LOCATION =~ ^[a-z0-9]+$ ]] || fail "--location must be an Azure region name such as swedencentral"
[ -z "$OPS_ID" ] || [[ $OPS_ID =~ $guid ]] || fail "--ops-group-id must be a GUID"
[[ $BUDGET =~ ^[0-9]+$ ]] || fail "--budget-amount must be a whole number"
[ -z "$DIGEST" ] || [[ $DIGEST =~ ^sha256:[0-9a-f]{64}$ ]] || fail "--image-digest must be sha256: followed by 64 lowercase hex characters"
[ -z "$PASSCODE_URI" ] || [[ $PASSCODE_URI =~ ^https://[A-Za-z0-9.-]+/secrets/[A-Za-z0-9-]+(/[0-9a-f]+)?$ ]] || fail "--passcode-secret-uri must be a Key Vault secret URI"
for pair in model-name:"$MODEL_NAME" model-version:"$MODEL_VERSION"; do
  [[ ${pair#*:} =~ $plain ]] || fail "--${pair%%:*} contains characters that are not allowed"
done
runner_json='"enable_build_runner": false,'
if [ -n "$RUNNER_REPO" ]; then
  [[ $RUNNER_REPO =~ ^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$ ]] || fail "--build-runner must be owner/repo"
  [ -f "$RUNNER_KEY_FILE" ] || fail "runner SSH public key not found: $RUNNER_KEY_FILE (use --runner-ssh-key)"
  runner_key=$(head -n 1 "$RUNNER_KEY_FILE")
  [[ $runner_key =~ ^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp256)\ [A-Za-z0-9+/=]+(\ [A-Za-z0-9._@-]*)?$ ]] || fail "$RUNNER_KEY_FILE is not an OpenSSH public key"
  runner_json="\"enable_build_runner\": true,
  \"build_runner_github_repository\": \"$RUNNER_REPO\",
  \"build_runner_admin_ssh_public_key\": \"$runner_key\","
fi
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

IDENTITY_TYPE=User
if [ -n "$OPS_ID" ] || [ -n "$OPS_NAME" ]; then
  IDENTITY_TYPE=Group
  if [ -n "$OPS_ID" ]; then
    RESOLVED_OPS_NAME=$(az ad group show --group "$OPS_ID" --query displayName --output tsv 2>/dev/null || true)
    [ -n "$RESOLVED_OPS_NAME" ] || fail "could not find existing Entra group object ID '$OPS_ID'"
    if [ -n "$OPS_NAME" ] && [ "$(printf '%s' "$OPS_NAME" | tr '[:upper:]' '[:lower:]')" != "$(printf '%s' "$RESOLVED_OPS_NAME" | tr '[:upper:]' '[:lower:]')" ]; then
      fail "existing Entra group name '$OPS_NAME' does not match object ID '$OPS_ID' ('$RESOLVED_OPS_NAME')"
    fi
    OPS_NAME=$RESOLVED_OPS_NAME
  else
    OPS_ID=$(az ad group show --group "$OPS_NAME" --query id --output tsv 2>/dev/null || true)
    [ -n "$OPS_ID" ] || fail "could not find existing Entra group '$OPS_NAME'"
  fi
  [[ $OPS_ID =~ $guid ]] || fail "approved Entra group object ID must be a GUID"
else
  OPS_NAME=$USER_NAME
  OPS_ID=$PRINCIPAL
fi

my_ip=
if [ "$ALLOW_IP" = true ] || [ "$VAULT_IP" = true ]; then
  my_ip=$(curl -fsS --max-time 10 https://api.ipify.org) || fail "could not look up the public address; use --presenter-cidrs"
  [[ $my_ip =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]] || fail "unexpected answer from the address lookup"
fi
[ "$ALLOW_IP" = true ] && CIDRS="${CIDRS:+$CIDRS,}$my_ip/32"
DEPLOYER_CIDRS=
[ "$VAULT_IP" = true ] && DEPLOYER_CIDRS="$my_ip/32"
EMAILS=${EMAILS:-$USER_MAIL}
cidr='^[0-9]{1,3}(\.[0-9]{1,3}){3}/[0-9]{1,2}$'
json_list() {
  local out= item
  local -a items=()
  IFS=, read -ra items <<<"$1"
  # ${items[@]+...} keeps an empty list from tripping set -u on macOS bash 3.2.
  for item in ${items[@]+"${items[@]}"}; do
    [[ -z $item ]] && continue
    [[ $item =~ $2 ]] || fail "invalid list entry: $item"
    out="$out${out:+,}\"$item\""
  done
  printf '[%s]' "$out"
}
cidr_json=$(json_list "$CIDRS" "$cidr")
deployer_json=$(json_list "$DEPLOYER_CIDRS" "$cidr")
email_json=$(json_list "$EMAILS" '^[^@[:space:]"\\]+@[^@[:space:]"\\]+$')

sha1() { if command -v sha1sum >/dev/null 2>&1; then sha1sum; else shasum -a 1; fi; }
suffix=$(printf '%s/%s' "$SUBSCRIPTION" "$PREFIX" | sha1 | cut -c1-6)
STATE_RG="rg-$PREFIX-tfstate"
STATE_ACCOUNT=${EXISTING:-"st${PREFIX}tf${suffix}"}
DIR=${DEPLOY_STATE_DIR:-${XDG_STATE_HOME:-$HOME/.local/state}/airlinedemo}/$PREFIX
umask 077
mkdir -p "$DIR"
TFVARS="$DIR/deploy.tfvars.json"
BACKEND="$DIR/backend.hcl"
# Keep the first budget start date: a new start date would make Terraform replace the budget every month.
BUDGET_START=
if [ -f "$TFVARS" ]; then
  BUDGET_START=$(sed -n 's/^ *"budget_start_date": "\([0-9]\{4\}-[0-9]\{2\}-01T00:00:00Z\)",$/\1/p' "$TFVARS" | head -n 1)
fi
BUDGET_START=${BUDGET_START:-$(date -u +%Y-%m-01T00:00:00Z)}
VAULT="kv-$PREFIX-$suffix"
ADOPT="$DIR/adopt-key-vault.json"
adopt_json=null
[ ! -f "$ADOPT" ] || adopt_json=$(cat "$ADOPT")
write_tfvars() {
cat >"$TFVARS" <<JSON
{
  "subscription_id": "$SUBSCRIPTION",
  "tenant_id": "$TENANT",
  "location": "$LOCATION",
  "name_prefix": "$PREFIX",
  "presenter_reviewer_cidrs": $cidr_json,
  "deployer_ip_cidrs": $deployer_json,
  $runner_json
  "sql_admin_group_name": "$OPS_NAME",
  "sql_admin_group_object_id": "$OPS_ID",
  "telemetry_reader_group_id": "$OPS_ID",
  "telemetry_reader_principal_type": "$IDENTITY_TYPE",
  "model_deployment_name": "$MODEL_DEPLOYMENT_NAME",
  "model_name": "$MODEL_NAME",
  "model_version": "$MODEL_VERSION",
  "terraform_principal_object_id": "$PRINCIPAL",
  "budget_amount": $BUDGET,
  "budget_start_date": "$BUDGET_START",
  "budget_contact_emails": $email_json,
  "security_response_owner": { "name": "$USER_NAME", "email": "$USER_MAIL" },
  "image_digest": $( [ -n "$DIGEST" ] && printf '"%s"' "$DIGEST" || printf null ),
  "demo_passcode_secret_uri": $( [ -n "$PASSCODE_URI" ] && printf '"%s"' "$PASSCODE_URI" || printf null ),
  "adopt_key_vault": $adopt_json,
  "terraform_state_backend": { "storage_account_name": "$STATE_ACCOUNT" }
}
JSON
}
write_tfvars
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

# A destroy soft-deletes the vault and purge protection keeps its name for 90 days. Recover it into a recreated
# resource group, grant the deployer's vault roles again (RBAC assignments are not recovered) and let Terraform
# import what exists (adopt_key_vault). The file is kept so a rerun after a failed apply still adopts the vault.
VAULT_ID="/subscriptions/$SUBSCRIPTION/resourceGroups/rg-$PREFIX/providers/Microsoft.KeyVault/vaults/$VAULT"
object_version() {
  az resource show --ids "$VAULT_ID/secrets/$1" --api-version 2023-07-01 \
    --query properties.secretUriWithVersion --output tsv 2>/dev/null | sed -n 's|^https://.*/\([0-9a-f]\{32\}\)$|\1|p'
}
json_or_null() { if [ -n "$1" ]; then printf '"%s"' "$1"; else printf null; fi; }
if [ "$(az keyvault show-deleted --name "$VAULT" --query name --output tsv 2>/dev/null)" = "$VAULT" ]; then
  printf 'Recovering the soft-deleted Key Vault %s; Terraform adopts it instead of creating a new one\n' "$VAULT"
  az group create --name "rg-$PREFIX" --location "$LOCATION" --output none
  az keyvault recover --name "$VAULT" --output none
  if [ "$VAULT_IP" = true ]; then
    az keyvault update --name "$VAULT" --public-network-access Enabled --default-action Deny --output none
    az keyvault network-rule add --name "$VAULT" --ip-address "$my_ip/32" --output none
  fi
  role_id() {
    az role assignment create --assignee-object-id "$PRINCIPAL" --assignee-principal-type User \
      --role "$1" --scope "$VAULT_ID" --query id --output tsv
  }
  cert_role=$(role_id "Key Vault Certificates Officer")
  secret_role=$(role_id "Key Vault Secrets Officer")
  [[ $cert_role == "$VAULT_ID"/providers/Microsoft.Authorization/roleAssignments/* ]] &&
    [[ $secret_role == "$VAULT_ID"/providers/Microsoft.Authorization/roleAssignments/* ]] ||
    { printf 'deploy: could not grant your Key Vault roles on the recovered vault %s\n' "$VAULT" >&2; exit 3; }
  cert_version=$(object_version release-image-signing-certificate)
  runner_version=
  [ -z "$RUNNER_REPO" ] || runner_version=$(object_version github-runner-token)
  cat >"$ADOPT" <<JSON
{ "certificates_role_assignment_id": "$cert_role", "secrets_role_assignment_id": "$secret_role", "signing_certificate_version": $(json_or_null "$cert_version"), "build_runner_token_version": $(json_or_null "$runner_version") }
JSON
  # Terraform reads the certificate through the data plane while planning the import; wait for the new role.
  reachable=false
  for _ in 1 2 3 4 5 6 7 8 9 10 11 12; do
    if [ -z "$cert_version" ] || az keyvault certificate show --vault-name "$VAULT" --name release-image-signing-certificate \
      --query id --output none 2>/dev/null; then reachable=true; break; fi
    sleep "${DEPLOY_ROLE_WAIT_SECONDS:-10}"
  done
  [ "$reachable" = true ] || printf 'deploy: warning: cannot read the recovered vault yet; outside the virtual network pass --vault-allow-my-ip\n' >&2
elif [ -f "$ADOPT" ] && [ "$(az keyvault show --name "$VAULT" --query name --output tsv 2>/dev/null)" != "$VAULT" ]; then
  rm -f "$ADOPT"
fi
adopt_json=null
[ ! -f "$ADOPT" ] || adopt_json=$(cat "$ADOPT")
write_tfvars

scripts/bootstrap-deployment.sh

if [ -z "$DIGEST" ]; then
  vault=$VAULT
  cat <<NEXT

Platform deployed without the application. Next:
  1. Store the demo passcode (from a network that can reach the vault, or after a run with --vault-allow-my-ip):
       az keyvault secret set --vault-name $vault --name demo-passcode --value '<passcode>'
NEXT
  [ -z "$RUNNER_REPO" ] || cat <<NEXT
  2. Store a fine-grained GitHub token for $RUNNER_REPO (Administration: read and write); the runner registers within a minute:
       az keyvault secret set --vault-name $vault --name github-runner-token --value '<token>'
NEXT
  cat <<NEXT
  3. Run the build workflow (PUBLISH_IMAGE=true) and the release workflow on the private-acr runner to push and sign the image.
  4. Rerun this command with --image-digest <released digest> --passcode-secret-uri https://$vault.vault.azure.net/secrets/demo-passcode
NEXT
fi
