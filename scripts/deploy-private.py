#!/usr/bin/env python3
"""Private deployment control plane. Secrets never travel in ordinary Run Command parameters."""
import argparse
import base64
import hashlib
import getpass
import io
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import sys
import tarfile
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
GUID = re.compile(r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}")


def run(*args, capture=False, **kwargs):
    return subprocess.run(args, check=True, text=True,
                          stdout=subprocess.PIPE if capture else None, **kwargs).stdout


def az(*args):
    return json.loads(run("az", *args, "--output", "json", capture=True))


def write_private(path, value):
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    fd = os.open(path, os.O_CREAT | os.O_TRUNC | os.O_WRONLY, 0o600)
    os.chmod(path, 0o600)
    with os.fdopen(fd, "w") as stream:
        stream.write(value)


def confirm(message):
    if input(message + " Type yes: ") != "yes":
        raise RuntimeError("Declined; no saved plan applied.")


class Deployment:
    def __init__(self, args):
        self.args = args
        account = az("account", "show")
        for field, expected in (("id", args.subscription_id), ("tenantId", args.tenant_id)):
            if not GUID.fullmatch(expected) or account[field].lower() != expected.lower():
                raise ValueError(f"Azure {field} mismatch; sign in/select the requested tenant/subscription first.")
        if not re.fullmatch(r"[a-z][a-z0-9]{2,9}", args.prefix):
            raise ValueError("prefix must be 3-10 lowercase letters/digits starting with a letter.")
        self.directory = Path(os.environ.get(
            "DEPLOY_STATE_DIR", str(Path.home() / ".local/state/airlinedemo"))) / args.prefix
        self.directory.mkdir(parents=True, exist_ok=True, mode=0o700)
        self.rg = f"rg-{args.prefix}-tfstate"
        self.vm = f"vm-{args.prefix}-deploy"
        suffix = hashlib.sha1(f"{args.subscription_id}/{args.prefix}".encode()).hexdigest()[:6]
        self.account = f"st{args.prefix}tf{suffix}"
        self.app_rg = f"rg-{args.prefix}"
        self.vault = f"kv-{args.prefix}-{suffix}"
        self.registry = f"acr{args.prefix}{suffix}"

    def remote(self, script):
        """Check the guest exit code, not just ARM's successful operation status."""
        name = "deploy-" + uuid.uuid4().hex[:12]
        payload = base64.b64encode(script.encode()).decode()
        try:
            az("vm", "run-command", "create", "-g", self.rg, "--vm-name", self.vm,
               "--name", name, "--location", self.args.location,
               "--script", '#!/bin/bash\nset -euo pipefail\nprintf "%s" "$PACKAGE" | base64 -d | bash\n',
               "--protected-parameters", f"PACKAGE={payload}", "--timeout-in-seconds", "7200")
            result = az("vm", "run-command", "show", "-g", self.rg,
                        "--vm-name", self.vm, "--name", name, "--expand", "instanceView")
            view = result.get("instanceView") or {}
            for key in ("output", "error"):
                if view.get(key):
                    print(view[key], file=sys.stderr if key == "error" else sys.stdout, flush=True)
            if view.get("exitCode") != 0 or view.get("executionState", "").lower() != "succeeded":
                raise RuntimeError(f"Private executor failed: state={view.get('executionState')}, exit={view.get('exitCode')}")
            return view.get("output", "")
        finally:
            run("az", "vm", "run-command", "delete", "-g", self.rg,
                "--vm-name", self.vm, "--name", name, "--yes", "--output", "none")

    def bootstrap(self, plan_only=False):
        operator = az("ad", "signed-in-user", "show")["id"]
        key = self.directory / "executor_ed25519"
        if not key.with_suffix(".pub").exists():
            run("ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", str(key))
        values = self.directory / "bootstrap.tfvars.json"
        write_private(values, json.dumps({
            "subscription_id": self.args.subscription_id, "tenant_id": self.args.tenant_id,
            "prefix": self.args.prefix, "location": self.args.location,
            "operator_id": operator, "ssh_public_key": key.with_suffix(".pub").read_text().strip(),
        }, indent=2))
        tf = ["terraform", f"-chdir={ROOT / 'infra/bootstrap'}"]
        run(*tf, "init", "-input=false", "-reconfigure",
            f"-backend-config=path={self.directory / 'bootstrap.tfstate'}")
        resources = (run(*tf, "state", "list", capture=True).splitlines()
                     if (self.directory / "bootstrap.tfstate").exists() else [])
        scope = f"/subscriptions/{self.args.subscription_id}/resourceGroups/{self.rg}"
        for address, resource_id in (
            ("azurerm_resource_group.bootstrap", scope),
            ("azapi_resource.state", f"{scope}/providers/Microsoft.Storage/storageAccounts/{self.account}"),
            ("azapi_resource.blob", f"{scope}/providers/Microsoft.Storage/storageAccounts/{self.account}/blobServices/default"),
            ("azapi_resource.container", f"{scope}/providers/Microsoft.Storage/storageAccounts/{self.account}/blobServices/default/containers/tfstate"),
        ):
            if address not in resources:
                exists = subprocess.run(
                    ["az", "resource", "show", "--ids", resource_id, "-o", "none"],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0
                if exists:
                    run(*tf, "import", f"-var-file={values}", address, resource_id)
        # All state bootstrap operations use ARM: no public data-plane exception or storage keys.
        plan = self.directory / "bootstrap.tfplan"
        if plan_only:
            run(*tf, "plan", f"-var-file={values}", f"-out={plan}")
            return
        run(*tf, "plan", f"-var-file={values}", f"-out={plan}")
        confirm("Apply the displayed private-executor plan?")
        run(*tf, "apply", str(plan))
        plan.unlink(missing_ok=True)
        run("az", "vm", "start", "-g", self.rg, "-n", self.vm, "-o", "none")
        self.remote("""set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq ca-certificates curl gnupg git unzip python3 jq
if ! command -v az >/dev/null; then curl -fsSL https://aka.ms/InstallAzureCLIDeb | bash; fi
if ! command -v terraform >/dev/null; then
  curl -fsSL https://apt.releases.hashicorp.com/gpg | gpg --dearmor -o /usr/share/keyrings/hashicorp.gpg
  echo "deb [signed-by=/usr/share/keyrings/hashicorp.gpg] https://apt.releases.hashicorp.com noble main" > /etc/apt/sources.list.d/hashicorp.list
  apt-get update -qq
  apt-get install -y -qq terraform
fi
install -d -m 700 /var/lib/airlinedemo /var/lib/airlinedemo/azure
terraform version
az version --query '"azure-cli"' -o tsv
""")
        write_private(self.directory / "executor.json", json.dumps({
            "resource_group": self.rg, "vm": self.vm, "state_account": self.account,
        }, indent=2))

    def login(self):
        tenant = shlex.quote(self.args.tenant_id)
        subscription = shlex.quote(self.args.subscription_id)
        output = self.remote(f"""set -euo pipefail
export AZURE_CONFIG_DIR=/var/lib/airlinedemo/azure
if az account show --query id -o tsv >/dev/null 2>&1 && \
   az account get-access-token --resource https://management.azure.com/ --output none 2>/dev/null; then
  az account set --subscription {subscription}
  az account show --query '{{subscription:id,tenant:tenantId}}'
  printf '\\nAIRLINEDEMO_SIGN_IN_READY\\n'
elif systemctl is-active --quiet airlinedemo-login; then
  tail -n 8 /var/lib/airlinedemo/login.log
else
  systemctl reset-failed airlinedemo-login 2>/dev/null || true
  systemd-run --unit=airlinedemo-login --setenv=AZURE_CONFIG_DIR="$AZURE_CONFIG_DIR" \
    --property=StandardOutput=append:/var/lib/airlinedemo/login.log \
    --property=StandardError=append:/var/lib/airlinedemo/login.log \
    /usr/bin/az login --tenant {tenant} --use-device-code --output none
  sleep 5
  tail -n 8 /var/lib/airlinedemo/login.log
fi
""")
        return "AIRLINEDEMO_SIGN_IN_READY" in output

    def sync(self):
        revision = run("git", "-C", str(ROOT), "rev-parse", "HEAD", capture=True).strip()
        repository = json.loads(run("gh", "repo", "view", "--json", "nameWithOwner", capture=True))["nameWithOwner"]
        if repository != "wesback/airlinedemo":
            raise ValueError("This release identity configuration is scoped to wesback/airlinedemo.")
        files = set(run("git", "-C", str(ROOT), "diff", "--name-only", "HEAD", capture=True).splitlines())
        files.update(("scripts/deploy-private.py", "scripts/verify-deployment.py", "infra/terraform/deployment-executor.tf"))
        files.update(("backend/AirlineDemo.Deployment/AirlineDemo.Deployment.csproj",
                      "backend/AirlineDemo.Deployment/Program.cs"))
        archive = io.BytesIO()
        with tarfile.open(fileobj=archive, mode="w:gz") as package:
            for name in sorted(files):
                if name.startswith(("infra/terraform/", "scripts/", "backend/AirlineDemo.Deployment/")) and (ROOT / name).is_file():
                    package.add(ROOT / name, arcname=name)
            for name in ("deploy.tfvars.json", "backend.hcl"):
                path = self.directory / name
                if path.exists():
                    package.add(path, arcname=f"operator-settings/{name}")
                fresh = self.directory / "fresh" / self.args.prefix / name
                if fresh.exists():
                    package.add(fresh, arcname=f"fresh-settings/{name}")
        encoded = base64.b64encode(archive.getvalue()).decode()
        self.source = f"/var/lib/airlinedemo/source/{revision}"
        self.remote(f"""set -euo pipefail
umask 077
if [ ! -d {self.source}/.git ]; then
  git clone --quiet --filter=blob:none --no-checkout https://github.com/{repository}.git {self.source}
  git -C {self.source} checkout --quiet --detach {revision}
fi
printf '%s' '{encoded}' | base64 -d | tar xz -C {self.source}
""")
        return revision

    def execute(self, script):
        self.sync()
        return self.remote(f"""set -euo pipefail
umask 077
export AZURE_CONFIG_DIR=/var/lib/airlinedemo/azure
export AZURE_SUBSCRIPTION_ID={shlex.quote(self.args.subscription_id)}
export AZURE_TENANT_ID={shlex.quote(self.args.tenant_id)}
export RESOURCE_GROUP_NAME={shlex.quote(self.app_rg)}
cd {shlex.quote(self.source)}
export TFVARS_FILE="$PWD/operator-settings/deploy.tfvars.json"
export BACKEND_CONFIG_FILE="$PWD/operator-settings/backend.hcl"
az account set --subscription "$AZURE_SUBSCRIPTION_ID"
{script}
""")

    def inspect(self):
        self.execute(f"""terraform -chdir=infra/terraform init -input=false -reconfigure -backend-config="$BACKEND_CONFIG_FILE"
terraform -chdir=infra/terraform state pull > /var/lib/airlinedemo/state-before-rebuild.json
az storage blob list --account-name {self.account} --container-name tfstate --auth-mode login --query '[].name'
terraform -chdir=infra/terraform state list
""")

    def connect(self):
        """Temporary private bridge for reviewing/destroying the previous stack."""
        executor = f"/subscriptions/{self.args.subscription_id}/resourceGroups/{self.rg}/providers/Microsoft.Network/virtualNetworks/vnet-{self.args.prefix}-deploy"
        application = f"/subscriptions/{self.args.subscription_id}/resourceGroups/{self.app_rg}/providers/Microsoft.Network/virtualNetworks/vnet-demo"
        resources = {}
        for group, network, name, remote in (
            (self.rg, f"vnet-{self.args.prefix}-deploy", f"{self.args.prefix}-application", application),
            (self.app_rg, "vnet-demo", "deployment-executor", executor),
        ):
            existing = [peer for peer in az("network", "vnet", "peering", "list", "-g", group, "--vnet-name", network)
                        if peer["name"] == name]
            if existing:
                if existing[0]["remoteVirtualNetwork"]["id"].lower() != remote.lower() or not existing[0]["allowVirtualNetworkAccess"]:
                    raise RuntimeError(f"Existing peering {name} has unexpected connectivity; refusing to overwrite it.")
            else:
                run("az", "network", "vnet", "peering", "create", "-g", group,
                    "--vnet-name", network, "-n", name, "--remote-vnet", remote,
                    "--allow-vnet-access", "-o", "none")
            address = ("azurerm_virtual_network_peering.executor_to_app[0]" if group == self.rg
                       else "azurerm_virtual_network_peering.app_to_executor[0]")
            network_id = executor if group == self.rg else application
            resources[address] = f"{network_id}/virtualNetworkPeerings/{name}"
        zones = {
            "privatelink.database.windows.net": ("executor", "sql"),
            "privatelink.openai.azure.com": ("executor", "model"),
            "privatelink.vaultcore.azure.net": ("executor", "kv"),
            "privatelink.azurecr.io": ("executor", "acr"),
            "privatelink.cognitiveservices.azure.com": ("executor", "contentsafety"),
            "privatelink.monitor.azure.com": ("monitor_executor", "monitor"),
            "privatelink.oms.opinsights.azure.com": ("monitor_executor", "oms"),
            "privatelink.ods.opinsights.azure.com": ("monitor_executor", "ods"),
            "privatelink.agentsvc.azure-automation.net": ("monitor_executor", "agentsvc"),
            "azure-api.net": ("gateway_executor", None),
        }
        for zone in az("network", "private-dns", "zone", "list", "-g", self.app_rg):
            if zone["name"] not in zones:
                continue
            existing = [link for link in az("network", "private-dns", "link", "vnet", "list",
                                           "-g", self.app_rg, "-z", zone["name"]) if link["name"] == "deployment-executor"]
            if existing:
                if existing[0]["virtualNetwork"]["id"].lower() != executor.lower():
                    raise RuntimeError("Existing DNS link targets an unexpected VNet; refusing to overwrite it.")
            else:
                run("az", "network", "private-dns", "link", "vnet", "create", "-g", self.app_rg,
                    "-z", zone["name"], "-n", "deployment-executor",
                    "--virtual-network", executor, "--registration-enabled", "false", "-o", "none")
            resource, key = zones[zone["name"]]
            index = "[0]" if key is None else f'["{key}"]'
            resources[f'azurerm_private_dns_zone_virtual_network_link.{resource}{index}'] = (
                zone["id"] + "/virtualNetworkLinks/deployment-executor")
        endpoint = az("network", "private-endpoint", "show", "-g", self.app_rg, "-n", "pe-blob")
        nic = az("network", "nic", "show", "--ids", endpoint["networkInterfaces"][0]["id"])
        address = nic["ipConfigurations"][0]["privateIPAddress"]
        storage = "st" + self.args.prefix + self.account[-6:]
        records = [record for record in az("network", "private-dns", "record-set", "a", "list",
                                          "-g", self.rg, "-z", "privatelink.blob.core.windows.net")
                   if record["name"] == storage]
        addresses = {entry["ipv4Address"] for record in records for entry in record.get("aRecords", [])}
        if addresses and addresses != {address}:
            raise RuntimeError("Existing Blob DNS record has unexpected addresses; refusing to overwrite it.")
        if not addresses:
            run("az", "network", "private-dns", "record-set", "a", "add-record",
                "-g", self.rg, "-z", "privatelink.blob.core.windows.net", "-n", storage,
                "-a", address, "-o", "none")
        self.execute(f"""python3 - <<'PY'
import socket
for host in ('{self.account}.blob.core.windows.net', '{self.vault}.vault.azure.net'):
    address = socket.gethostbyname(host)
    if not address.startswith('10.'):
        raise SystemExit('Not a private destination: ' + host)
    print(host + ' resolves privately')
PY
az keyvault certificate show --vault-name {self.vault} --name release-image-signing-certificate --query id -o tsv
""")
        write_private(self.directory / "bridge.json", json.dumps({
            "resources": resources,
            "executor": {"vnet_id": executor,
                         "blob_dns_zone_id": f"/subscriptions/{self.args.subscription_id}/resourceGroups/{self.rg}/providers/Microsoft.Network/privateDnsZones/privatelink.blob.core.windows.net"},
        }))

    def prepare(self):
        destination = self.directory / "fresh"
        values = destination / self.args.prefix / "deploy.tfvars.json"
        if values.exists():
            return
        environment = dict(os.environ, DEPLOY_STATE_DIR=str(destination))
        arguments = [str(ROOT / "scripts/deploy.sh"), "--prepare-only", "--prefix", self.args.prefix,
                     "--location", self.args.location, "--build-runner", "wesback/airlinedemo", "--allow-my-ip"]
        run(*arguments, env=environment)
        config = json.loads(values.read_text())
        scope = f"/subscriptions/{self.args.subscription_id}/resourceGroups/{self.rg}/providers/Microsoft.Network"
        config["deployment_executor"] = {
            "vnet_id": f"{scope}/virtualNetworks/vnet-{self.args.prefix}-deploy",
            "blob_dns_zone_id": f"{scope}/privateDnsZones/privatelink.blob.core.windows.net",
        }
        # Permit scripted smoke checks from exactly the private executor's explicit NAT address.
        nat = az("network", "public-ip", "show", "-g", self.rg, "-n", f"pip-{self.args.prefix}-deploy-nat")["ipAddress"]
        config["presenter_reviewer_cidrs"] = sorted(set(config["presenter_reviewer_cidrs"] + [nat + "/32"]))
        write_private(values, json.dumps(config, indent=2))

    def platform(self):
        self.prepare()
        values = self.directory / "fresh" / self.args.prefix / "deploy.tfvars.json"
        config = json.loads(values.read_text())
        if config["image_digest"] is None:
            apps = az("containerapp", "list", "-g", self.app_rg) if az("group", "exists", "-n", self.app_rg) else []
            if apps:
                raise RuntimeError("Existing Container App found. Refusing a platform-only apply that could remove it; complete the reviewed teardown first.")
        deleted = [vault for vault in az("keyvault", "list-deleted") if vault["name"] == self.vault]
        recovery = self.directory / "vault-recovery.json"
        if deleted or (recovery.exists() and config.get("adopt_key_vault") is None):
            write_private(recovery, json.dumps({"vault": self.vault}))
            if deleted:
                run("az", "group", "create", "-n", self.app_rg, "-l", self.args.location, "-o", "none")
                run("az", "keyvault", "recover", "-n", self.vault, "-o", "none")
            run("az", "keyvault", "update", "-n", self.vault, "--public-network-access", "Disabled", "-o", "none")
            vault_id = f"/subscriptions/{self.args.subscription_id}/resourceGroups/{self.app_rg}/providers/Microsoft.KeyVault/vaults/{self.vault}"
            principal = config["terraform_principal_object_id"]
            roles = {}
            for kind, role in (("certificates", "Key Vault Certificates Officer"), ("secrets", "Key Vault Secrets Officer")):
                existing = [assignment for assignment in az("role", "assignment", "list", "--scope", vault_id)
                            if assignment["principalId"].lower() == principal.lower()
                            and assignment["roleDefinitionName"] == role and assignment["scope"].lower() == vault_id.lower()]
                if len(existing) > 1:
                    raise RuntimeError(f"Multiple exact vault assignments for {role}; reconcile ownership before importing.")
                roles[kind] = (existing[0]["id"] if existing else az(
                    "role", "assignment", "create", "--assignee-object-id", principal,
                    "--assignee-principal-type", "User", "--role", role, "--scope", vault_id)["id"])
            def version(name):
                result = subprocess.run(["az", "resource", "show", "--ids", f"{vault_id}/secrets/{name}",
                                         "--api-version", "2023-07-01", "-o", "json"],
                                        text=True, capture_output=True)
                if result.returncode:
                    raise RuntimeError(f"Cannot inspect retained vault object {name}; do not overwrite retained signing/runner material.")
                uri = json.loads(result.stdout)["properties"]["secretUriWithVersion"]
                return uri.rsplit("/", 1)[-1]
            config["adopt_key_vault"] = {
                "certificates_role_assignment_id": roles["certificates"],
                "secrets_role_assignment_id": roles["secrets"],
                "signing_certificate_version": version("release-image-signing-certificate"),
                "build_runner_token_version": version("github-runner-token"),
            }
            write_private(values, json.dumps(config, indent=2))
        self.execute("""export TFVARS_FILE="$PWD/fresh-settings/deploy.tfvars.json"
export BACKEND_CONFIG_FILE="$PWD/fresh-settings/backend.hcl"
scripts/prebootstrap-deployment.sh --register-providers
terraform -chdir=infra/terraform init -input=false -reconfigure -backend-config="$BACKEND_CONFIG_FILE"
terraform -chdir=infra/terraform plan -var-file="$TFVARS_FILE" \
  -target=azurerm_virtual_network_peering.app_to_executor \
  -target=azurerm_virtual_network_peering.executor_to_app \
  -target=azurerm_private_dns_zone_virtual_network_link.executor \
  -target=azurerm_private_dns_zone_virtual_network_link.demo \
  -target=azurerm_private_endpoint.demo -out=/var/lib/airlinedemo/network.tfplan \
  | tee /var/lib/airlinedemo/network-plan.txt
""")
        confirm("Apply the private network foundation plan? (Full logs/plans stay on the executor.)")
        self.execute("""terraform -chdir=infra/terraform apply /var/lib/airlinedemo/network.tfplan
terraform -chdir=infra/terraform plan -var-file="$PWD/fresh-settings/deploy.tfvars.json" -out=/var/lib/airlinedemo/platform.tfplan \
  | tee /var/lib/airlinedemo/platform-plan.txt
""")
        confirm("Apply the full platform plan?")
        self.execute("terraform -chdir=infra/terraform apply /var/lib/airlinedemo/platform.tfplan")

    def database(self):
        migration = az("identity", "show", "-g", self.app_rg, "-n", "id-airline-migration")
        app = az("identity", "show", "-g", self.app_rg, "-n", "id-airline-app")
        server = az("sql", "server", "list", "-g", self.app_rg)[0]["fullyQualifiedDomainName"]
        databases = [d for d in az("sql", "db", "list", "-g", self.app_rg, "-s", server.split(".")[0])
                     if d["name"] != "master"]
        if len(databases) != 1:
            raise RuntimeError("Expected exactly one application database.")
        configuration = {
            "server": server, "database": databases[0]["name"],
            "appClientId": app["clientId"], "migrationClientId": migration["clientId"],
        }
        encoded = base64.b64encode(json.dumps(configuration).encode()).decode()
        run("az", "vm", "identity", "assign", "-g", self.rg, "-n", self.vm,
            "--identities", migration["id"], "-o", "none")
        try:
            self.execute(f"""if ! command -v dotnet >/dev/null; then
  curl -fsSL https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -o /var/lib/airlinedemo/microsoft.deb
  dpkg -i /var/lib/airlinedemo/microsoft.deb
  apt-get update -qq
  apt-get install -y dotnet-sdk-10.0
fi
terraform -chdir=infra/terraform output -json > /var/lib/airlinedemo/outputs.json
printf '%s' '{encoded}' | base64 -d > /var/lib/airlinedemo/sql.json
python3 - <<'PY'
import json
path = '/var/lib/airlinedemo/sql.json'
with open(path) as source: config = json.load(source)
with open('/var/lib/airlinedemo/outputs.json') as source: outputs = json.load(source)
config['appGrants'] = outputs['app_sql_grant_statements']['value']
config['migrationGrants'] = outputs['migration_sql_grant_statements']['value']
with open(path, 'w') as destination: json.dump(config, destination)
PY
dotnet run --project backend/AirlineDemo.Deployment -- /var/lib/airlinedemo/sql.json
""")
        finally:
            run("az", "vm", "identity", "remove", "-g", self.rg, "-n", self.vm,
                "--identities", migration["id"], "-o", "none")

    def preflight(self):
        repository = "wesback/airlinedemo"
        environment = json.loads(run("gh", "api", f"repos/{repository}/environments/production-release", capture=True))
        owner = run("gh", "variable", "get", "PRODUCT_OWNER_LOGIN", "--repo", repository, capture=True).strip()
        reviewers = [reviewer for rule in environment.get("protection_rules", [])
                     if rule["type"] == "required_reviewers" for reviewer in rule.get("reviewers", [])]
        if (len(reviewers) != 1 or reviewers[0]["type"] != "User"
                or reviewers[0]["reviewer"]["login"] != owner or not owner):
            raise RuntimeError("production-release must require exactly the PRODUCT_OWNER_LOGIN user. Configure protection before deploying.")
        secrets = json.loads(run("gh", "secret", "list", "--repo", repository, "--json", "name", capture=True))
        if "RELEASE_GOVERNANCE_TOKEN" not in {secret["name"] for secret in secrets}:
            raise RuntimeError("Store RELEASE_GOVERNANCE_TOKEN in GitHub Actions secrets before deploying; see the deployment manual.")
        oidc = json.loads(run("gh", "api", f"repos/{repository}/actions/oidc/customization/sub", capture=True))
        if not oidc.get("use_immutable_subject") or oidc.get("sub_claim_prefix") != "repo:wesback@14137481/airlinedemo@1364258050":
            raise RuntimeError("Repository OIDC subjects do not match the Terraform workflow credentials.")

    def configure(self):
        self.preflight()
        build = az("identity", "show", "-g", self.app_rg, "-n", "id-airline-build")
        release = az("identity", "show", "-g", self.app_rg, "-n", "id-airline-release")
        registry = az("acr", "list", "-g", self.app_rg)[0]
        info = self.execute(f"""az keyvault certificate show --vault-name {self.vault} \
--name release-image-signing-certificate --query '{{key:kid,subject:policy.x509CertificateProperties.subject}}' -o json
""")
        certificate = json.loads(info)
        variables = {
            "ACR_NAME": registry["name"], "ACR_LOGIN_SERVER": registry["loginServer"],
            "AZURE_CLIENT_ID": build["clientId"], "AZURE_SUBSCRIPTION_ID": self.args.subscription_id,
            "AZURE_TENANT_ID": self.args.tenant_id, "AZURE_RESOURCE_GROUP": self.app_rg,
            "AZURE_KV_NAME": self.vault, "AZURE_KV_SIGNING_CERT_NAME": "release-image-signing-certificate",
            "AZURE_KV_SIGNING_KEY_ID": certificate["key"],
            "AZURE_KV_SIGNING_CERT_SUBJECT": "C=BE, ST=Brussels, O=Airline Demo, CN=airline-demo-release-signing",
            "CONTAINER_APP_NAME": f"ca-{self.args.prefix}", "IMAGE_REPOSITORY": "airline-api", "PUBLISH_IMAGE": "true",
        }
        for name, value in variables.items():
            run("gh", "variable", "set", name, "--repo", "wesback/airlinedemo", "--body", value)
        run("gh", "variable", "set", "AZURE_CLIENT_ID", "--repo", "wesback/airlinedemo",
            "--env", "production-release", "--body", release["clientId"])
        needed = self.execute(f"""python3 - <<'PY'
import json, subprocess
def secret(name):
    result = subprocess.run(['az','keyvault','secret','show','--vault-name','{self.vault}','--name',name,'-o','json'],
                            text=True, capture_output=True)
    if result.returncode:
        if 'SecretNotFound' not in result.stderr:
            raise SystemExit('Cannot inspect vault secret: ' + name)
        return False
    return json.loads(result.stdout)['value'] not in ('', 'unset')
print(json.dumps({{'passcode': not secret('demo-passcode'), 'runner': not secret('github-runner-token')}}))
PY
""")
        for field, name, prompt in (
            ("passcode", "demo-passcode", "Choose the demo sign-in passcode (hidden): "),
            ("runner", "github-runner-token", "Repository-scoped GitHub runner PAT (hidden): "),
        ):
            if json.loads(needed)[field]:
                value = getpass.getpass(prompt)
                if not value:
                    raise ValueError(f"{name} cannot be empty.")
                payload = base64.b64encode(value.encode()).decode()
                self.execute(f"""file=$(mktemp /var/lib/airlinedemo/secret.XXXXXX)
trap 'rm -f "$file"' EXIT
printf '%s' '{payload}' | base64 -d > "$file"
az keyvault secret set --vault-name {self.vault} --name {name} --file "$file" -o none
""")
        runner_name = f"vm-{self.args.prefix}-runner"
        run("az", "vm", "start", "-g", self.app_rg, "-n", runner_name, "-o", "none")
        for _ in range(120):
            runners = json.loads(run("gh", "api", "repos/wesback/airlinedemo/actions/runners", capture=True))["runners"]
            matching = [runner for runner in runners if runner["name"] == runner_name
                        and runner["status"] == "online"
                        and "private-acr" in {label["name"] for label in runner["labels"]}]
            if len(matching) == 1:
                print("GitHub variables configured and private build runner online; release protection unchanged.")
                break
            time.sleep(10)
        else:
            raise RuntimeError("Private build runner did not register within 20 minutes. Inspect its cloud-init log; do not broaden networking.")

    def build(self):
        repository = "wesback/airlinedemo"
        head = json.loads(run("gh", "api", f"repos/{repository}/git/ref/heads/main", capture=True))["object"]["sha"]
        runs = json.loads(run("gh", "run", "list", "--repo", repository, "--workflow", "build.yml",
                              "--event", "push", "--branch", "main", "--commit", head,
                              "--limit", "3", "--json", "databaseId,status,conclusion", capture=True))
        if not runs:
            raise RuntimeError("No push-main build exists for current main. Merge the reviewed deployment PR to trigger one.")
        build = runs[0]
        if build["status"] == "completed" and build["conclusion"] != "success":
            raise RuntimeError(f"Build {build['databaseId']} failed; fix CI before retrying.")
        if build["status"] == "completed":
            available = self.execute(f"""if az acr repository show --name {self.registry} --image airline-api:{head} -o none 2>/dev/null; then
  echo IMAGE_PRESENT
else
  echo IMAGE_MISSING
fi
""")
            if "IMAGE_MISSING" in available:
                # Preserve the old attempt's evidence before CI intentionally replaces the run's named artifacts.
                prior = json.loads(run("gh", "api", f"repos/{repository}/actions/runs/{build['databaseId']}", capture=True))
                backup = self.directory / "previous-build-evidence" / f"{build['databaseId']}-{prior['run_attempt']}"
                backup.mkdir(parents=True, exist_ok=True, mode=0o700)
                run("gh", "run", "download", str(build["databaseId"]), "--repo", repository, "--dir", str(backup))
                run("gh", "run", "rerun", str(build["databaseId"]), "--repo", repository)
                for _ in range(30):
                    latest = json.loads(run("gh", "api", f"repos/{repository}/actions/runs/{build['databaseId']}", capture=True))
                    if latest["run_attempt"] > prior["run_attempt"]:
                        break
                    time.sleep(2)
                else:
                    raise RuntimeError("The replacement push-main build attempt did not start.")
        run("gh", "run", "watch", str(build["databaseId"]), "--repo", repository, "--exit-status")
        artifacts = self.directory / "build" / str(build["databaseId"])
        artifacts.mkdir(parents=True, exist_ok=True, mode=0o700)
        run("gh", "run", "download", str(build["databaseId"]), "--repo", repository,
            "--name", "image-digest", "--dir", str(artifacts))
        digest = (artifacts / "digest.txt").read_text().strip()
        if not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
            raise RuntimeError("Build artifact does not contain an exact image digest.")
        self.execute(f"""actual=$(az acr repository show --name {self.registry} \
--image airline-api@{digest} --query digest -o tsv)
[ "$actual" = {shlex.quote(digest)} ] || {{ echo 'Registry digest mismatch' >&2; exit 1; }}
""")
        write_private(self.directory / "build.json", json.dumps({
            "id": build["databaseId"], "digest": digest, "main_sha": head,
        }, indent=2))

    def release(self):
        build = json.loads((self.directory / "build.json").read_text())
        record = self.directory / "release.json"
        release = json.loads(record.read_text()) if record.exists() else {}
        if release.get("build") != build:
            previous = json.loads(run("gh", "run", "list", "--workflow", "release.yml", "--limit", "10",
                                      "--json", "databaseId", capture=True))
            before = {item["databaseId"] for item in previous}
            bootstrap_only = not (az("containerapp", "list", "-g", self.app_rg)
                                  if az("group", "exists", "-n", self.app_rg) else [])
            run("gh", "workflow", "run", "release.yml", "--ref", "main",
                "-f", f"image_digest={build['digest']}", "-f", f"build_run_id={build['id']}",
                "-f", f"bootstrap_only={str(bootstrap_only).lower()}")
            for _ in range(15):
                current = json.loads(run("gh", "run", "list", "--workflow", "release.yml",
                                         "--event", "workflow_dispatch", "--limit", "10",
                                         "--json", "databaseId,headSha", capture=True))
                candidates = [item for item in current if item["databaseId"] not in before and item["headSha"] == build["main_sha"]]
                if candidates:
                    if len(candidates) != 1:
                        raise RuntimeError("Concurrent release dispatches detected; select the exact run before proceeding.")
                    release = {"id": candidates[0]["databaseId"], "build": build}
                    write_private(record, json.dumps(release, indent=2))
                    break
                time.sleep(2)
            else:
                raise RuntimeError("Release dispatch was not visible; inspect GitHub before dispatching again.")
        status = json.loads(run("gh", "run", "view", str(release["id"]),
                                "--json", "status,conclusion,url", capture=True))
        if status["status"] != "completed":
            raise RuntimeError(f"Personal release approval/completion required: {status['url']}. Rerun --stage release after approval; no app was created.")
        if status["conclusion"] != "success":
            raise RuntimeError(f"Protected release did not succeed: {status['url']}")
        evidence = self.directory / "release-evidence" / str(release["id"])
        evidence.mkdir(parents=True, exist_ok=True, mode=0o700)
        run("gh", "run", "download", str(release["id"]), "--name", f"release-evidence-{release['id']}",
            "--dir", str(evidence))
        inspections = list(evidence.rglob("notation-inspect.txt"))
        if len(inspections) != 1 or build["digest"] not in inspections[0].read_text():
            raise RuntimeError("Protected release evidence does not identify the selected image digest.")
        print(f"Protected release {release['id']} signed and verified {build['digest']}.")

    def application(self):
        self.release()
        build = json.loads((self.directory / "build.json").read_text())
        values = self.directory / "fresh" / self.args.prefix / "deploy.tfvars.json"
        config = json.loads(values.read_text())
        config["image_digest"] = build["digest"]
        config["demo_passcode_secret_uri"] = f"https://{self.vault}.vault.azure.net/secrets/demo-passcode"
        write_private(values, json.dumps(config, indent=2))
        self.execute("""terraform -chdir=infra/terraform plan \
-var-file="$PWD/fresh-settings/deploy.tfvars.json" -out=/var/lib/airlinedemo/application.tfplan
terraform -chdir=infra/terraform show -no-color /var/lib/airlinedemo/application.tfplan > /var/lib/airlinedemo/application-plan.txt
""")
        confirm("Apply this application plan for the protected signed digest?")
        self.execute("terraform -chdir=infra/terraform apply /var/lib/airlinedemo/application.tfplan")

    def plan_destroy(self):
        networks = az("network", "vnet", "list", "-g", self.app_rg)
        if any(network["name"] == "vnet-demo" for network in networks):
            self.connect()
        else:
            # A failed destroy can leave DNS ownership after its app network has gone.
            bridge_path = self.directory / "bridge.json"
            previous = json.loads(bridge_path.read_text())
            existing = {resource["id"].lower() for resource in az("resource", "list", "-g", self.app_rg)}
            previous["resources"] = {address: resource_id for address, resource_id in previous["resources"].items()
                                     if resource_id.lower() in existing}
            for zone in az("network", "private-dns", "zone", "list", "-g", self.app_rg):
                for link in az("network", "private-dns", "link", "vnet", "list", "-g", self.app_rg, "-z", zone["name"]):
                    if link["name"] != "deployment-executor":
                        continue
                    if link["virtualNetwork"]["id"].lower() != previous["executor"]["vnet_id"].lower():
                        raise RuntimeError("Remaining DNS link targets an unexpected VNet; refusing to adopt it.")
                    if zone["name"] == "azure-api.net":
                        previous["resources"]['azurerm_private_dns_zone_virtual_network_link.gateway_executor[0]'] = link["id"]
                    elif link["id"].lower() not in {value.lower() for value in previous["resources"].values()}:
                        raise RuntimeError("Remaining DNS link has no recorded Terraform ownership; reconcile it before teardown.")
            write_private(bridge_path, json.dumps(previous))
        bridge = base64.b64encode((self.directory / "bridge.json").read_bytes()).decode()
        self.execute(f"""terraform -chdir=infra/terraform init -input=false -reconfigure -backend-config="$BACKEND_CONFIG_FILE"
backup="/var/lib/airlinedemo/state-before-destroy-$(date -u +%Y%m%dT%H%M%SZ).json"
terraform -chdir=infra/terraform state pull > "$backup"
export TFVARS_FILE=/var/lib/airlinedemo/destroy.tfvars.json
export BRIDGE='{bridge}'
python3 - <<'PY'
import base64, json, os, subprocess
bridge = json.loads(base64.b64decode(os.environ['BRIDGE']))
with open('operator-settings/deploy.tfvars.json') as source: config = json.load(source)
config['deployment_executor'] = bridge['executor']
with open(os.environ['TFVARS_FILE'], 'w') as destination: json.dump(config, destination)
addresses = set(subprocess.check_output(['terraform', '-chdir=infra/terraform', 'state', 'list'], text=True).splitlines())
for address, resource_id in bridge['resources'].items():
    if address not in addresses:
        subprocess.run(['terraform', '-chdir=infra/terraform', 'import', '-var-file=' + os.environ['TFVARS_FILE'],
                        address, resource_id], check=True)
PY
terraform -chdir=infra/terraform state list > /var/lib/airlinedemo/state-addresses.txt
for address in azurerm_key_vault_certificate.signing 'azurerm_key_vault_secret.build_runner_token[0]'; do
  if grep -Fqx "$address" /var/lib/airlinedemo/state-addresses.txt; then
    terraform -chdir=infra/terraform state rm "$address"
  fi
done
scripts/destroy-deployment.sh --plan-only /var/lib/airlinedemo/destroy.tfplan
terraform -chdir=infra/terraform show -no-color /var/lib/airlinedemo/destroy.tfplan > /var/lib/airlinedemo/destroy-plan.txt
terraform -chdir=infra/terraform show -json /var/lib/airlinedemo/destroy.tfplan | python3 -c '
import collections,json,sys
changes=json.load(sys.stdin)["resource_changes"]
retained = ("azurerm_key_vault_certificate.signing", "azurerm_key_vault_secret.build_runner_token[0]")
if any(c["address"] in retained and c["change"]["actions"] != ["no-op"] for c in changes):
    raise SystemExit("Destroy plan must not delete retained signing or runner material")
print("Destroy plan actions:",dict(collections.Counter("/".join(c["change"]["actions"]) for c in changes)))
print("Retained certificate/token are outside this destroy; full plan and pre-destroy state backup are on the executor.")
'
""")

    def destroy(self):
        self.execute(f"""test -s /var/lib/airlinedemo/destroy.tfplan
az storage blob list --account-name {self.account} --container-name tfstate --auth-mode login -o none
scripts/destroy-deployment.sh --check-plan /var/lib/airlinedemo/destroy.tfplan
""")
        confirm(f"Destroy the reviewed {self.app_rg} application stack and its database contents? The state/executor and retained vault material remain.")
        expected = f"/subscriptions/{self.args.subscription_id}/resourceGroups/{self.app_rg}/".lower()
        state_id = f"/subscriptions/{self.args.subscription_id}/resourceGroups/{self.rg}/providers/Microsoft.Storage/storageAccounts/{self.account}"
        for endpoint in az("network", "private-endpoint", "list", "-g", self.rg):
            connections = endpoint.get("privateLinkServiceConnections", [])
            if (endpoint["subnet"]["id"].lower().startswith(expected)
                    and len(connections) == 1
                    and connections[0]["privateLinkServiceId"].lower() == state_id.lower()):
                # The replacement state PE was verified before this stage. Remove only the obsolete app-VNet attachment.
                run("az", "network", "private-endpoint", "delete", "--ids", endpoint["id"], "-o", "none")
        self.execute("scripts/destroy-deployment.sh --yes --apply-plan /var/lib/airlinedemo/destroy.tfplan")
        if az("group", "exists", "-n", self.app_rg):
            raise RuntimeError("Application resource group still exists after destroy.")
        storage = f"st{self.args.prefix}{self.account[-6:]}"
        records = az("network", "private-dns", "record-set", "a", "list", "-g", self.rg,
                     "-z", "privatelink.blob.core.windows.net")
        if any(record["name"] == storage for record in records):
            run("az", "network", "private-dns", "record-set", "a", "delete", "-g", self.rg,
                "-z", "privatelink.blob.core.windows.net", "-n", storage, "--yes", "-o", "none")
        write_private(self.directory / "destroyed.json", json.dumps({"resource_group": self.app_rg, "completed": True}))

    def verify(self):
        app = az("containerapp", "show", "-g", self.app_rg, "-n", f"ca-{self.args.prefix}")
        digest = json.loads((self.directory / "build.json").read_text())["digest"]
        if app["properties"]["template"]["containers"][0]["image"] != f"{self.registry}.azurecr.io/airline-api@{digest}":
            raise RuntimeError("The deployed image is not the selected signed digest.")
        origin = "https://" + app["properties"]["configuration"]["ingress"]["fqdn"]

        def healthy():
            for _ in range(60):
                state = az("containerapp", "show", "-g", self.app_rg, "-n", f"ca-{self.args.prefix}")["properties"]
                revision = state.get("latestReadyRevisionName")
                if revision and revision == state["latestRevisionName"]:
                    health = az("containerapp", "revision", "show", "-g", self.app_rg,
                                "-n", f"ca-{self.args.prefix}", "--revision", revision)["properties"]["healthState"]
                    if health == "Healthy":
                        return revision
                time.sleep(5)
            raise RuntimeError("Application did not become healthy.")

        revision = healthy()
        command = f"python3 scripts/verify-deployment.py --origin {shlex.quote(origin)} --vault {self.vault} --snapshot /var/lib/airlinedemo/case-digests.json"
        self.execute(command)
        run("az", "containerapp", "revision", "restart", "-g", self.app_rg,
            "-n", f"ca-{self.args.prefix}", "--revision", revision, "-o", "none")
        time.sleep(10)
        healthy()
        self.execute(command + " --compare")
        # Promote only verified settings; retain the old deployment settings for recovery.
        for name in ("deploy.tfvars.json", "backend.hcl"):
            previous = self.directory / name
            if previous.exists() and not (self.directory / f"previous-{name}").exists():
                write_private(self.directory / f"previous-{name}", previous.read_text())
            write_private(previous, (self.directory / "fresh" / self.args.prefix / name).read_text())
        write_private(self.directory / "verified.json", json.dumps({"origin": origin, "digest": digest}, indent=2))
        print(f"Verified application: {origin}")

    def all(self):
        self.preflight()
        if not (self.directory / "executor.json").exists():
            self.bootstrap()
        else:
            run("az", "vm", "start", "-g", self.rg, "-n", self.vm, "-o", "none")
        if not self.login():
            raise RuntimeError("Complete the displayed private-executor sign-in, then rerun the same deployment command.")
        for stage in (self.platform, self.database, self.configure, self.build, self.release, self.application, self.verify):
            stage()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tenant-id", required=True)
    parser.add_argument("--subscription-id", required=True)
    parser.add_argument("--prefix", default="demo1")
    parser.add_argument("--location", default="swedencentral")
    parser.add_argument("--stage", choices=("preflight", "plan-bootstrap", "bootstrap", "login", "inspect", "connect",
                                           "prepare", "platform", "database", "configure", "build",
                                           "release", "application", "verify", "plan-destroy", "destroy", "all"), default="all")
    args = parser.parse_args()
    deployment = Deployment(args)
    if args.stage == "plan-bootstrap":
        deployment.bootstrap(plan_only=True)
    else:
        getattr(deployment, args.stage.replace("-", "_"))()


if __name__ == "__main__":
    try:
        main()
    except subprocess.CalledProcessError as error:
        print(f"deploy: {error.cmd[0]} failed with exit {error.returncode}", file=sys.stderr)
        sys.exit(3)
    except (ValueError, RuntimeError, EOFError) as error:
        print(f"deploy: {error}", file=sys.stderr)
        sys.exit(3)
