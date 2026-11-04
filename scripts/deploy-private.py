#!/usr/bin/env python3
"""Private deployment control plane. Secrets never travel in ordinary Run Command parameters."""
import argparse
import base64
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation
import hashlib
import getpass
import io
import json
import os
from pathlib import Path
import re
import shlex
import ssl
import subprocess
import sys
import tarfile
import tempfile
import time
import uuid
import zlib
from urllib.parse import urlencode, urlparse
from urllib.error import HTTPError, URLError
from urllib.request import urlopen

ROOT = Path(__file__).resolve().parents[1]
GUID = re.compile(r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}")


def run(*args, capture=False, **kwargs):
    return subprocess.run(args, check=True, text=True,
                          stdout=subprocess.PIPE if capture else None, **kwargs).stdout


def az(*args, private=False):
    return json.loads(run("az", *args, "--output", "json", capture=True,
                          **({"stderr": subprocess.PIPE} if private else {})))


def write_private(path, value):
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    fd = os.open(path, os.O_CREAT | os.O_TRUNC | os.O_WRONLY, 0o600)
    os.chmod(path, 0o600)
    with os.fdopen(fd, "w") as stream:
        stream.write(value)


def confirm(message):
    if input(message + " Type yes: ") != "yes":
        raise RuntimeError("Declined; no saved plan applied.")


def canonical_sha256(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"),
                                     allow_nan=False).encode()).hexdigest()


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def read_retail_price_page(url):
    for attempt in range(4):
        try:
            with urlopen(url, timeout=30) as response:
                return json.load(response)
        except HTTPError as error:
            if error.code not in (429, 500, 502, 503, 504) or attempt == 3:
                raise
        except (URLError, TimeoutError, ConnectionError) as error:
            if isinstance(getattr(error, "reason", error), ssl.SSLError) or attempt == 3:
                raise
        time.sleep(2 ** attempt)


def foundry_network_import(state, scope, read_subnet):
    address = "azurerm_subnet_network_security_group_association.foundry_agent[0]"
    resources = state.get("values", {}).get("root_module", {}).get("resources", [])
    if any(resource["address"] == address for resource in resources):
        return None
    subnets = [resource for resource in resources if resource["address"] == "azurerm_subnet.foundry_agent[0]"]
    if not subnets:
        return None
    require_evidence(len(subnets) == 1)
    subnet_id = subnets[0]["values"]["id"]
    require_evidence(re.fullmatch(
        re.escape(scope) + r"/providers/Microsoft\.Network/virtualNetworks/[^/]+/subnets/snet-foundry-agent",
        subnet_id, flags=re.IGNORECASE) is not None)
    subnet = read_subnet(subnet_id)
    require_evidence(subnet["id"].lower() == subnet_id.lower())
    group = subnet.get("networkSecurityGroup")
    if group is None:
        return None
    require_evidence(re.fullmatch(
        re.escape(scope) + r"/providers/Microsoft\.Network/networkSecurityGroups/[^/]+",
        group["id"], flags=re.IGNORECASE) is not None)
    return address, subnet_id


def import_foundry_network(adoption, backend_config, subscription_id, tenant_id):
    """Import only the existing association, without evaluating uncreated project stores."""
    with tempfile.TemporaryDirectory(prefix="foundry-network-import-") as directory:
        root = Path(directory)
        (root / ".terraform.lock.hcl").write_bytes(Path("infra/terraform/.terraform.lock.hcl").read_bytes())
        configuration = {
            "terraform": {
                "backend": {"azurerm": {"use_azuread_auth": True}},
                "required_providers": {"azurerm": {"source": "hashicorp/azurerm", "version": "~> 4.0"}},
            },
            "provider": {"azurerm": {
                "subscription_id": subscription_id, "tenant_id": tenant_id,
                "resource_provider_registrations": "none", "features": {},
            }},
            "resource": {"azurerm_subnet_network_security_group_association": {"foundry_agent": {
                "count": 1, "subnet_id": adoption[1],
                "network_security_group_id": adoption[1].split("/providers/")[0] +
                "/providers/Microsoft.Network/networkSecurityGroups/nsg-foundry-agent",
            }}},
        }
        write_private(root / "main.tf.json", json.dumps(configuration))
        subprocess.run(["terraform", "-chdir=" + directory, "init", "-input=false",
                        "-lockfile=readonly", "-backend-config=" + str(Path(backend_config).resolve())], check=True)
        subprocess.run(["terraform", "-chdir=" + directory, "import", "-input=false", *adoption], check=True)


def evidence_time(value):
    instant = datetime.fromisoformat(value[:-1] + "+00:00" if value.endswith("Z") else value)
    if instant.tzinfo is None or instant.utcoffset() is None:
        raise ValueError("UTC offset required")
    return instant


def evidence_number(value, minimum=0):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError("Numeric evidence required")
    number = Decimal(str(value))
    if not number.is_finite() or number < minimum:
        raise ValueError("Invalid numeric evidence")
    return number


def require_evidence(condition):
    if not condition:
        raise ValueError()


def foundry_plan_fingerprint(plan, plan_bytes, source_commit, scope=None):
    """Inspect the actual saved plan without returning its potentially secret values."""
    resources = []

    def collect(module):
        resources.extend(module.get("resources", []))
        for child in module.get("child_modules", []):
            collect(child)

    collect(plan["planned_values"]["root_module"])
    wrappers = [resource["values"] for resource in resources
                if resource["address"] == "azurerm_resource_group_template_deployment.foundry[0]"]
    require_evidence(len(wrappers) == 1)
    require_evidence(not any("delete" in change["change"]["actions"]
                             for change in plan["resource_changes"]))
    wrapper = wrappers[0]
    require_evidence(wrapper["deployment_mode"] == "Incremental" and wrapper["debug_level"] == "none")

    def model_profile(values):
        return {"name": values["model"][0]["name"], "version": values["model"][0]["version"],
                "sku": values["sku"][0]["name"], "capacity": values["sku"][0]["capacity"]}

    changes = {row["address"]: row["change"] for row in plan["resource_changes"] if "address" in row}
    retained = []
    for resource in resources:
        change = changes.get(resource["address"], {})
        if resource.get("type") == "azurerm_cognitive_deployment" and change.get("actions") == ["no-op"]:
            require_evidence(all(change["before"].get(key) == change["after"].get(key) ==
                                 resource["values"].get(key) for key in ("id", "model", "sku")))
            require_evidence(resource["values"]["model"][0]["format"] == "OpenAI")
            retained.append({"deploymentId": resource["values"]["id"], "model": model_profile(resource["values"])})
    result = {
        "sourceCommit": source_commit,
        "planSha256": hashlib.sha256(plan_bytes).hexdigest(),
        "configurationSha256": canonical_sha256(plan["planned_values"]),
        "templateSha256": canonical_sha256(json.loads(wrapper["template_content"])),
        "parametersSha256": canonical_sha256(json.loads(wrapper["parameters_content"])),
        "models": sorted([model_profile(resource["values"])
                          for resource in resources if resource.get("type") == "azurerm_cognitive_deployment"],
                         key=canonical_sha256),
        "retainedModels": sorted(retained, key=canonical_sha256),
    }
    search = [resource["values"] for resource in resources
              if resource["address"] == "azurerm_search_service.foundry[0]"]
    if search:
        require_evidence(len(search) == 1)
        result["search"] = {key: search[0][key] for key in ("location", "sku")}
    if scope is not None:
        template, parameters = json.loads(wrapper["template_content"]), json.loads(wrapper["parameters_content"])
        validate_foundry_template(template, parameters, scope)
        require_evidence(wrapper["resource_group_name"] == scope.rsplit("/", 1)[-1])
        result.update(template=template, parameters=parameters)
    return result


def foundry_template_outputs():
    reference = "reference(resourceId('Microsoft.CognitiveServices/accounts/projects', parameters('accountName'), parameters('projectName')), '2026-09-15-preview'"
    return {
        "projectPrincipalId": {"type": "string", "value": "[" + reference + ", 'Full').identity.principalId]"},
        "projectInternalId": {"type": "string", "value": "[" + reference + ").internalId]"},
    }


def validate_foundry_template(template, parameters, scope):
    """Admit only the documented two-resource account/project template profile."""
    require_evidence(set(template) == {"$schema", "contentVersion", "parameters", "resources", "outputs"})
    require_evidence(template["$schema"] == "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#")
    require_evidence(template["contentVersion"] == "1.0.0.0")
    require_evidence(template["parameters"] == {"accountName": {"type": "string"}, "projectName": {"type": "string"}})
    require_evidence(set(parameters) == {"accountName", "projectName"})
    for parameter in parameters.values():
        require_evidence(set(parameter) == {"value"})
        require_evidence(re.fullmatch(r"[a-z][a-z0-9-]{2,62}", parameter["value"]) is not None)
    resources = template["resources"]
    require_evidence(len(resources) == 2)
    account, project = resources
    require_evidence(set(account) == {"type", "apiVersion", "name", "location", "kind", "sku", "identity", "properties"})
    require_evidence(set(project) == {"type", "apiVersion", "name", "location", "identity", "dependsOn", "properties"})
    for resource, suffix in ((account, ""), (project, "/projects")):
        require_evidence(resource["type"] == "Microsoft.CognitiveServices/accounts" + suffix)
        require_evidence(resource["apiVersion"] == "2026-09-15-preview")
        require_evidence(resource["location"] == "swedencentral")
        require_evidence(resource["identity"] == {"type": "SystemAssigned"})
    require_evidence(account["name"] == "[parameters('accountName')]")
    require_evidence(account["kind"] == "AIServices" and account["sku"] == {"name": "S0"})
    properties = account["properties"]
    require_evidence(set(properties) == {"allowProjectManagement", "customSubDomainName", "disableLocalAuth",
                                        "publicNetworkAccess", "networkInjections"})
    require_evidence(properties["allowProjectManagement"] is True and properties["disableLocalAuth"] is True)
    require_evidence(properties["publicNetworkAccess"] == "Disabled")
    require_evidence(properties["customSubDomainName"] == "[parameters('accountName')]")
    expected_stores = {"blobStore": "Microsoft.Storage/storageAccounts",
                       "documentStore": "Microsoft.DocumentDB/databaseAccounts",
                       "vectorStore": "Microsoft.Search/searchServices"}
    project_properties = project["properties"]
    require_evidence(set(project_properties) == {"displayName", "capabilitySettings"})
    stores = project_properties["capabilitySettings"]
    require_evidence(set(stores) == set(expected_stores))
    for key, provider in expected_stores.items():
        require_evidence(re.fullmatch(re.escape(scope + "/providers/" + provider) + r"/[A-Za-z0-9._-]+",
                                     stores[key]) is not None)
    require_evidence(template["outputs"] == foundry_template_outputs())
    require_evidence(len(properties["networkInjections"]) == 1)
    injection = properties["networkInjections"][0]
    require_evidence(set(injection) == {"scenario", "subnetArmId", "useMicrosoftManagedNetwork"})
    require_evidence(injection["scenario"] == "agent" and injection["useMicrosoftManagedNetwork"] is False)
    require_evidence(re.fullmatch(re.escape(scope + "/providers/Microsoft.Network/virtualNetworks/") +
                                 r"[A-Za-z0-9._-]+/subnets/[A-Za-z0-9._-]+", injection["subnetArmId"]) is not None)
    require_evidence(project["name"] == "[format('{0}/{1}', parameters('accountName'), parameters('projectName'))]")
    require_evidence(project["dependsOn"] == ["[resourceId('Microsoft.CognitiveServices/accounts', parameters('accountName'))]"])
    require_evidence(project_properties["displayName"] == "[parameters('projectName')]")


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

    def foundry_plan_evidence(self, candidate=False):
        scope = f"/subscriptions/{self.args.subscription_id}/resourceGroups/{self.app_rg}" if candidate else None
        script = """python3 - <<'PY'
import base64, json, pathlib, runpy, subprocess, zlib
helpers = runpy.run_path('scripts/deploy-private.py')
path = pathlib.Path('/var/lib/airlinedemo/foundry.tfplan')
plan_bytes = path.read_bytes()
import os, tempfile
with tempfile.NamedTemporaryFile(dir=path.parent, prefix='foundry-inspect.') as snapshot:
    snapshot.write(plan_bytes)
    snapshot.flush()
    os.chmod(snapshot.name, 0o400)
    plan = json.loads(subprocess.check_output(['terraform', '-chdir=infra/terraform', 'show', '-json', snapshot.name]))
    revision = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
    projection = helpers['foundry_plan_fingerprint'](plan, plan_bytes, revision, scope=SCOPE)
    encoded = base64.b64encode(zlib.compress(json.dumps(projection).encode())).decode()
    helpers['require_evidence'](len(encoded) <= 3500)
    print(encoded)
PY
"""
        encoded = self.execute(script.replace("scope=SCOPE", "scope=" + repr(scope))).strip()
        try:
            require_evidence(0 < len(encoded) <= 3500)
            return json.loads(zlib.decompress(base64.b64decode(encoded, validate=True)))
        except (ValueError, TypeError, zlib.error):
            raise RuntimeError("foundry_plan_evidence_invalid") from None

    def foundry_plan(self):
        (self.directory / "foundry-candidate.json").unlink(missing_ok=True)
        (self.directory / "foundry-preflight.json").unlink(missing_ok=True)
        try:
            require_evidence(self.args.location == "swedencentral")
            estimate = json.loads((self.directory / "foundry-estimate-inputs.json").read_text())
            require_evidence(set(estimate) == {"quotaRequests", "usage", "meterSkus"})
            require_evidence(bool(estimate["usage"]) and set(estimate["usage"]) == set(estimate["meterSkus"]))
            self.execute("""export ARM_RESOURCE_PROVIDER_REGISTRATIONS=none
if [ -d "$PWD/fresh-settings" ]; then
  export TFVARS_FILE="$PWD/fresh-settings/deploy.tfvars.json"
  export BACKEND_CONFIG_FILE="$PWD/fresh-settings/backend.hcl"
  test -r "$TFVARS_FILE"
  test -r "$BACKEND_CONFIG_FILE"
fi
terraform -chdir=infra/terraform init -input=false -reconfigure -backend-config="$BACKEND_CONFIG_FILE" > /var/lib/airlinedemo/foundry-plan.log 2>&1
python3 - <<'PY'
import json, os, runpy, subprocess
helpers = runpy.run_path("scripts/deploy-private.py")
state = json.loads(subprocess.check_output(["terraform", "-chdir=infra/terraform", "show", "-json"]))
scope = f"/subscriptions/{os.environ['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{os.environ['RESOURCE_GROUP_NAME']}"
adoption = helpers["foundry_network_import"](state, scope, lambda resource_id:
    helpers["az"]("network", "vnet", "subnet", "show", "--ids", resource_id, private=True))
if adoption is not None:
    helpers["import_foundry_network"](adoption, os.environ["BACKEND_CONFIG_FILE"],
                                     os.environ["AZURE_SUBSCRIPTION_ID"], os.environ["AZURE_TENANT_ID"])
PY
terraform -chdir=infra/terraform plan -input=false -var-file="$TFVARS_FILE" -out=/var/lib/airlinedemo/foundry.tfplan >> /var/lib/airlinedemo/foundry-plan.log 2>&1
""")
            projection = self.foundry_plan_evidence(candidate=True)
            require_evidence(projection["sourceCommit"] == run("git", "-C", str(ROOT), "rev-parse", "HEAD", capture=True).strip())
            subscription = ("--subscription", self.args.subscription_id)
            scope = f"/subscriptions/{self.args.subscription_id}/resourceGroups/{self.app_rg}"
            assignments = sorted(az("policy", "assignment", "list", "--scope", scope, "--filter", "atScope()",
                                    "--expand", "LatestDefinitionVersion,EffectiveDefinitionVersion",
                                    *subscription, private=True), key=lambda row: row["id"])
            inventory = sorted(az("resource", "list", "--resource-group", self.app_rg, *subscription, private=True),
                               key=lambda row: row["id"])
            candidate = {**estimate, "meterIds": sorted(estimate["usage"]),
                         **{key: projection[key] for key in ("models", "template", "parameters")},
                         "bindings": {**{key: projection[key] for key in (
                             "sourceCommit", "configurationSha256", "planSha256", "templateSha256", "parametersSha256")},
                             "assignmentSha256": canonical_sha256(assignments),
                             "retainedResourceSha256": canonical_sha256(inventory)}}
            write_private(self.directory / "foundry-candidate.json", json.dumps(candidate, indent=2))
            return self.foundry_preflight(candidate)
        except (LookupError, TypeError, ValueError, AttributeError, ArithmeticError, RuntimeError,
                OSError, EOFError, subprocess.CalledProcessError):
            (self.directory / "foundry-candidate.json").unlink(missing_ok=True)
            (self.directory / "foundry-preflight.json").unlink(missing_ok=True)
            raise RuntimeError("foundry_preflight_refused") from None

    def foundry_arm_evidence(self, request):
        scope = f"/subscriptions/{self.args.subscription_id}/resourceGroups/{self.app_rg}"
        template, parameters = request["template"], request["parameters"]
        validate_foundry_template(template, parameters, scope)
        require_evidence(canonical_sha256(template) == request["bindings"]["templateSha256"])
        require_evidence(canonical_sha256(parameters) == request["bindings"]["parametersSha256"])
        with tempfile.TemporaryDirectory(prefix="foundry-arm-", dir=self.directory) as directory:
            template_file, parameters_file = Path(directory) / "template.json", Path(directory) / "parameters.json"
            write_private(template_file, json.dumps(template))
            write_private(parameters_file, json.dumps(parameters))
            arguments = ("--resource-group", self.app_rg, "--template-file", str(template_file),
                         "--parameters", "@" + str(parameters_file), "--mode", "Incremental",
                         "--subscription", self.args.subscription_id)
            validation = az("deployment", "group", "validate", *arguments, private=True)
            write_private(self.directory / "foundry-native-arm.json",
                          json.dumps({"runId": request["runId"], "validation": validation}))
            require_evidence(validation.get("error") is None)
            require_evidence(validation["properties"]["provisioningState"] == "Succeeded")
            what_if = az("deployment", "group", "what-if", *arguments, "--no-pretty-print",
                         "--result-format", "FullResourcePayloads", "--exclude-change-types", "Ignore", private=True)
            write_private(self.directory / "foundry-native-arm.json",
                          json.dumps({"runId": request["runId"], "validation": validation, "whatIf": what_if}))
            require_evidence(what_if["status"] == "Succeeded" and not what_if.get("error"))
            account_id = scope + "/providers/Microsoft.CognitiveServices/accounts/" + parameters["accountName"]["value"]
            allowed = {account_id.lower(), (account_id + "/projects/" + parameters["projectName"]["value"]).lower()}
            require_evidence(len(what_if["changes"]) == len(allowed))
            require_evidence({change["resourceId"].lower() for change in what_if["changes"]} == allowed)
            for change in what_if["changes"]:
                require_evidence(change["resourceId"].lower() in allowed)
                require_evidence(change["changeType"] in ("Create", "Modify", "NoChange"))
        return {"validate": "Succeeded", "whatIf": "Succeeded",
                "templateSha256": canonical_sha256(template), "parametersSha256": canonical_sha256(parameters),
                "changeSha256": canonical_sha256(sorted(what_if["changes"], key=lambda change: change["resourceId"].lower()))}

    def foundry_discovery(self, request):
        require_evidence(self.args.location == "swedencentral")
        account = az("account", "show", private=True)
        require_evidence(account["tenantId"] == self.args.tenant_id and account["id"] == self.args.subscription_id)
        fingerprint = self.foundry_plan_evidence()
        for key in ("sourceCommit", "configurationSha256", "planSha256", "templateSha256", "parametersSha256"):
            require_evidence(fingerprint[key] == request["bindings"][key])
        require_evidence(fingerprint["models"] == sorted(request["models"], key=canonical_sha256))
        require_evidence(bool(request["models"]))
        subscription = ("--subscription", self.args.subscription_id)
        scope = f"/subscriptions/{self.args.subscription_id}/resourceGroups/{self.app_rg}"
        assignments = sorted(az("policy", "assignment", "list", "--scope", scope, "--filter", "atScope()",
                                "--expand", "LatestDefinitionVersion,EffectiveDefinitionVersion", *subscription, private=True),
                             key=lambda row: row["id"])
        inventory = sorted(az("resource", "list", "--resource-group", self.app_rg, *subscription, private=True),
                           key=lambda row: row["id"])
        require_evidence(canonical_sha256(assignments) == request["bindings"]["assignmentSha256"])
        require_evidence(canonical_sha256(inventory) == request["bindings"]["retainedResourceSha256"])
        providers = []
        search = fingerprint.get("search")
        search_region = search["location"] if search else self.args.location
        require_evidence(search_region in ("swedencentral", "germanywestcentral", "francecentral"))
        for namespace, types in (
                ("Microsoft.CognitiveServices", ("accounts", "accounts/projects")),
                ("Microsoft.DocumentDB", ("databaseAccounts",)),
                ("Microsoft.Storage", ("storageAccounts",)),
                ("Microsoft.Search", ("searchServices",)),
                ("Microsoft.Network", ("virtualNetworks",)),
                ("Microsoft.App", ("managedEnvironments",))):
            provider = az("provider", "show", "--namespace", namespace, *subscription, private=True)
            require_evidence(provider["registrationState"] == "Registered")
            for name in types:
                matches = [row for row in provider["resourceTypes"] if row["resourceType"] == name]
                require_evidence(len(matches) == 1)
                region = search_region if namespace == "Microsoft.Search" else self.args.location
                require_evidence(region in {location.replace(" ", "").lower()
                                           for location in matches[0]["locations"]})
                if namespace == "Microsoft.CognitiveServices":
                    require_evidence("2026-09-15-preview" in matches[0]["apiVersions"])
            providers.append(provider)
        search_usage = None
        if search:
            search_usage = az("rest", "--method", "get", "--url",
                              f"https://management.azure.com/subscriptions/{self.args.subscription_id}"
                              f"/providers/Microsoft.Search/locations/{search_region}/usages?api-version=2025-05-01",
                              *subscription, private=True)
            matches = [row for row in search_usage["value"] if row["name"]["value"] == search["sku"]]
            require_evidence(len(matches) == 1)
            require_evidence(evidence_number(matches[0]["limit"]) -
                             evidence_number(matches[0]["currentValue"]) >= 1)
        skus = az("cognitiveservices", "account", "list-skus", "--kind", "AIServices",
                  "--location", self.args.location, *subscription, private=True)
        require_evidence(any(row["name"] == "S0" and row["kind"] == "AIServices" and not row["restrictions"]
                             for row in skus))
        models = az("cognitiveservices", "model", "list", "--location", self.args.location, *subscription, private=True)
        usages = az("cognitiveservices", "usage", "list", "--location", self.args.location, *subscription, private=True)
        required_quota, retained_readbacks = {}, []
        for required in request["models"]:
            matches = list({canonical_sha256(row["model"]): row["model"] for row in models
                            if row["model"]["name"] == required["name"]
                            and row["model"]["version"] == required["version"]
                            and row["model"]["format"] == "OpenAI"}.values())
            require_evidence(len(matches) == 1)
            matching_skus = [row for row in matches[0]["skus"]
                             if row["name"] == required["sku"] and row["usageName"] in request["quotaRequests"]]
            require_evidence(len(matching_skus) == 1)
            for retirement in (matches[0].get("deprecation", {}).get("inference"),
                               matching_skus[0].get("deprecationDate")):
                if retirement:
                    require_evidence(evidence_time(retirement) > evidence_time(request["startedAt"]))
            quota_name = matching_skus[0]["usageName"]
            require_evidence(quota_name in request["quotaRequests"])
            capacity = matching_skus[0]["capacity"]
            value = evidence_number(required["capacity"], minimum=1)
            require_evidence(value == value.to_integral_value())
            for bound, comparison in (("minimum", lambda limit: value >= limit),
                                      ("maximum", lambda limit: value <= limit)):
                if capacity.get(bound) is not None:
                    require_evidence(comparison(evidence_number(capacity[bound])))
            if capacity.get("minimum") is None or capacity.get("step") is None:
                if capacity.get("default") is None or value != evidence_number(capacity["default"], minimum=1):
                    retained = [row for row in fingerprint.get("retainedModels", []) if row["model"] == required]
                    require_evidence(len(retained) == sum(model == required for model in fingerprint["models"]))
                    require_evidence(len({row["deploymentId"].lower() for row in retained}) == len(retained))
                    for row in retained:
                        deployment_id = row["deploymentId"]
                        require_evidence(re.fullmatch(
                            re.escape(scope) + r"/providers/Microsoft\.CognitiveServices/accounts/[A-Za-z0-9._-]+/deployments/[A-Za-z0-9._-]+",
                            deployment_id, flags=re.IGNORECASE) is not None)
                        live = az("resource", "show", "--ids", deployment_id, "--api-version", "2024-10-01",
                                  *subscription, private=True)
                        require_evidence(live["id"].lower() == deployment_id.lower())
                        require_evidence(live["properties"]["provisioningState"] == "Succeeded")
                        require_evidence({key: live["properties"]["model"][key] for key in ("format", "name", "version")} ==
                                         {"format": "OpenAI", "name": required["name"], "version": required["version"]})
                        require_evidence(live["sku"]["name"] == required["sku"] and
                                         evidence_number(live["sku"]["capacity"], minimum=1) == value)
                        retained_readbacks.append(live)
            else:
                minimum, step = evidence_number(capacity["minimum"]), evidence_number(capacity["step"])
                require_evidence(step > 0 and (value - minimum) % step == 0)
            required_quota[quota_name] = required_quota.get(quota_name, Decimal(0)) + value
        for name, requested in required_quota.items():
            require_evidence(evidence_number(request["quotaRequests"][name]["requested"]) >= requested)
        quota = []
        for name, requested in request["quotaRequests"].items():
            matches = [row for row in usages if row["name"]["value"] == name]
            require_evidence(len(matches) == 1 and matches[0]["unit"] == requested["unit"])
            available = evidence_number(matches[0]["limit"]) - evidence_number(matches[0]["currentValue"])
            require_evidence(available >= evidence_number(requested["requested"]) > 0)
            quota.append({"name": name, **requested, "available": float(available)})
        raw = {"runId": request["runId"], "assignments": assignments, "inventory": inventory,
               "providers": providers, "skus": skus, "models": models, "usages": usages,
               "retainedModels": retained_readbacks, "search": search, "searchUsage": search_usage}
        write_private(self.directory / "foundry-discovery.json", json.dumps(raw, indent=2))
        return {"provider": {"status": "available", "sourceSha256": canonical_sha256(
                    {"providers": providers, "skus": skus, "models": models, "retainedModels": retained_readbacks,
                     "search": search, "searchUsage": search_usage})},
                "quota": sorted(quota, key=lambda row: row["name"])}

    def foundry_quote(self, request):
        items, sources = [], []
        for meter_id in request["meterIds"]:
            require_evidence(GUID.fullmatch(meter_id) is not None)
            usage = request["usage"][meter_id]
            region = usage.get("region", "swedencentral")
            require_evidence(region in ("swedencentral", "Global", "") or
                             (region in ("germanywestcentral", "francecentral") and usage["productName"] == "Azure AI Search"))
            url = "https://prices.azure.com/api/retail/prices?" + urlencode({
                "currencyCode": "'USD'", "$filter": f"armRegionName eq '{region}' and meterId eq '{meter_id}'"})
            rows, seen = [], set()
            while url:
                parsed = urlparse(url)
                require_evidence(parsed.scheme == "https" and parsed.netloc == "prices.azure.com"
                                 and parsed.path == "/api/retail/prices" and url not in seen)
                seen.add(url)
                page = read_retail_price_page(url)
                sources.append(page)
                rows.extend(row for row in page["Items"] if row["meterId"] == meter_id
                            and row["armRegionName"] == region and row["currencyCode"] == "USD"
                            and row["type"] == "Consumption"
                            and row["skuName"] == request["meterSkus"][meter_id]
                            and row["productName"] == usage["productName"] and row["unitOfMeasure"] == usage["unit"]
                            and evidence_time(row["effectiveStartDate"]) <= evidence_time(request["startedAt"])
                            and (not row.get("effectiveEndDate") or
                                 evidence_time(row["effectiveEndDate"]) > evidence_time(request["startedAt"])))
                url = page.get("NextPageLink")
            require_evidence(bool(rows))
            latest = max(evidence_time(row["effectiveStartDate"]) for row in rows)
            rows = [row for row in rows if evidence_time(row["effectiveStartDate"]) == latest]
            quantity = evidence_number(usage["quantity"], minimum=0)
            tiers = [evidence_number(row["tierMinimumUnits"]) for row in rows]
            require_evidence(all(quantity < tier for tier in tiers if tier > 0))
            rows = [row for row in rows if evidence_number(row["tierMinimumUnits"]) == 0]
            require_evidence(len(rows) == 1)
            row = rows[0]
            require_evidence(evidence_number(row["tierMinimumUnits"]) == 0)
            require_evidence(row["skuName"] == request["meterSkus"][meter_id])
            require_evidence(row["unitOfMeasure"] == usage["unit"] and row["productName"] == usage["productName"])
            price = evidence_number(row["retailPrice"])
            items.append({"meterId": meter_id, "sku": row["skuName"], "unit": row["unitOfMeasure"],
                          "unitPrice": row["retailPrice"], "quantity": usage["quantity"],
                          "usageAssumption": usage["usageAssumption"], "estimatedCost": float(price * quantity)})
        write_private(self.directory / "foundry-prices.json", json.dumps({"runId": request["runId"], "pages": sources}))
        return {"source": "https://prices.azure.com/api/retail/prices", "currency": "USD", "items": items,
                "estimatedCost": float(sum((evidence_number(item["estimatedCost"]) for item in items), Decimal(0)))}

    def acquire_foundry_evidence(self, request):
        discovery = self.foundry_discovery(request)
        quote, arm = self.foundry_quote(request), self.foundry_arm_evidence(request)
        assessment_file = self.directory / "foundry-assessments.json"
        assessment_file.unlink(missing_ok=True)
        confirm(f"Acquire current authorized policy and New-runtime capacity assessments for run {request['runId']} "
                f"using the private request/discovery files, and write {assessment_file}. Evidence ready?")
        assessments = json.loads(assessment_file.read_text())
        require_evidence(assessments["capacity"]["quotaRequestsSha256"] == canonical_sha256(request["quotaRequests"]))
        return {"schemaVersion": 1, "runId": request["runId"], "identity": request["identity"],
                "bindings": request["bindings"], "capturedAt": utc_now(), **discovery,
                "quote": quote, "arm": arm, "policy": assessments["policy"], "capacity": assessments["capacity"]}

    def validate_foundry_evidence(self, request, evidence):
        """Validate one invocation's evidence; callers must gather it at the cloud edge."""
        try:
            require = require_evidence
            require(request["schemaVersion"] == evidence["schemaVersion"] == 1)
            require(evidence["runId"] == request["runId"])
            started, captured = evidence_time(request["startedAt"]), evidence_time(evidence["capturedAt"])
            require(started <= captured <= evidence_time(utc_now()))
            identity = {"tenantId": self.args.tenant_id, "subscriptionId": self.args.subscription_id,
                        "region": self.args.location}
            require(identity["region"] == "swedencentral")
            require(request["identity"] == evidence["identity"] == identity)
            bindings = request["bindings"]
            require(set(bindings) == {"sourceCommit", "configurationSha256", "planSha256",
                                      "assignmentSha256", "retainedResourceSha256",
                                      "templateSha256", "parametersSha256"})
            require(re.fullmatch(r"[0-9a-f]{40}", bindings["sourceCommit"]) is not None)
            for name, value in bindings.items():
                if name != "sourceCommit":
                    require(re.fullmatch(r"[0-9a-f]{64}", value) is not None)
            require(evidence["bindings"] == bindings)
            require(evidence["provider"]["status"] == "available")
            require(re.fullmatch(r"[0-9a-f]{64}", evidence["provider"]["sourceSha256"]) is not None)
            quota = evidence["quota"]
            require(bool(quota) and len({row["name"] for row in quota}) == len(quota))
            require(canonical_sha256({row["name"]: {"unit": row["unit"], "requested": row["requested"]}
                                      for row in quota}) == canonical_sha256(request["quotaRequests"]))
            for row in quota:
                require(evidence_number(row["requested"]) > 0)
                require(evidence_number(row["available"]) >= evidence_number(row["requested"]))
            capacity, policy = evidence["capacity"], evidence["policy"]
            require(capacity["status"] == "verified" and policy["status"] == "approved")
            for assessment in (capacity, policy):
                require(assessment["runId"] == request["runId"])
                require(started <= evidence_time(assessment["capturedAt"]) <= captured)
                require(assessment["configurationSha256"] == bindings["configurationSha256"])
                require(isinstance(assessment["sourceReference"], str) and bool(assessment["sourceReference"].strip()))
                require(re.fullmatch(r"[0-9a-f]{64}", assessment["sourceSha256"]) is not None)
            require(policy["assignmentSha256"] == bindings["assignmentSha256"])
            arm = evidence["arm"]
            require(arm["validate"] == arm["whatIf"] == "Succeeded")
            require(arm["templateSha256"] == bindings["templateSha256"])
            require(arm["parametersSha256"] == bindings["parametersSha256"])
            require(re.fullmatch(r"[0-9a-f]{64}", arm["changeSha256"]) is not None)
            quote = evidence["quote"]
            require(quote["source"] == "https://prices.azure.com/api/retail/prices")
            require(quote["currency"] == "USD" and bool(quote["items"]))
            require(len({item["meterId"] for item in quote["items"]}) == len(quote["items"]))
            require(sorted(item["meterId"] for item in quote["items"]) == sorted(request["meterIds"]))
            total = Decimal(0)
            for item in quote["items"]:
                require(item["sku"] == request["meterSkus"][item["meterId"]])
                require(isinstance(item["unit"], str) and bool(item["unit"].strip()))
                require(isinstance(item["usageAssumption"], str) and bool(item["usageAssumption"].strip()))
                require(evidence_number(item["quantity"]) > 0)
                cost = evidence_number(item["unitPrice"]) * evidence_number(item["quantity"])
                require(cost == evidence_number(item["estimatedCost"]))
                total += cost
            require(total == evidence_number(quote["estimatedCost"]))
            canonical_sha256(evidence)
        except (KeyError, TypeError, ValueError, AttributeError, InvalidOperation, OverflowError):
            raise RuntimeError("foundry_preflight_refused") from None
        return evidence

    def foundry_preflight(self, candidate=None, acquire=None):
        """The acquisition callback is a read-only cloud/policy edge, never an apply."""
        receipt = self.directory / "foundry-preflight.json"
        receipt.unlink(missing_ok=True)
        try:
            if candidate is None:
                candidate = json.loads((self.directory / "foundry-candidate.json").read_text())
            if acquire is None:
                acquire = self.acquire_foundry_evidence
            request = json.loads(json.dumps(candidate, allow_nan=False))
            request.update(schemaVersion=1, runId=uuid.uuid4().hex, startedAt=utc_now(),
                           identity={"tenantId": self.args.tenant_id,
                                     "subscriptionId": self.args.subscription_id, "region": self.args.location})
            write_private(self.directory / "foundry-preflight-request.json", json.dumps(request, indent=2))
            if run("git", "-C", str(ROOT), "rev-parse", "HEAD", capture=True).strip() != request["bindings"]["sourceCommit"]:
                raise ValueError()
            evidence = json.loads(json.dumps(acquire(json.loads(json.dumps(request))), allow_nan=False))
            self.validate_foundry_evidence(request, evidence)
            write_private(receipt, json.dumps(evidence, indent=2, allow_nan=False))
            return evidence
        except (LookupError, TypeError, ValueError, AttributeError, ArithmeticError, RuntimeError,
                OSError, EOFError, subprocess.CalledProcessError):
            raise RuntimeError("foundry_preflight_refused") from None

    def foundry_apply(self, candidate=None, acquire=None):
        evidence = self.foundry_preflight(candidate, acquire)
        try:
            confirm(f"Apply Foundry plan {evidence['bindings']['planSha256']} with estimated additional "
                    f"cost USD {evidence['quote']['estimatedCost']} for the recorded usage assumptions?")
        except (RuntimeError, EOFError):
            (self.directory / "foundry-preflight.json").unlink(missing_ok=True)
            raise
        try:
            refreshed = self.foundry_preflight(candidate, acquire)
            for name in ("identity", "bindings", "provider", "quota", "arm", "quote"):
                if canonical_sha256(refreshed[name]) != canonical_sha256(evidence[name]):
                    raise ValueError()
            for name in ("capacity", "policy"):
                for field in ("status", "sourceReference", "sourceSha256", "configurationSha256"):
                    if refreshed[name][field] != evidence[name][field]:
                        raise ValueError()
        except (KeyError, TypeError, ValueError, RuntimeError):
            (self.directory / "foundry-preflight.json").unlink(missing_ok=True)
            raise RuntimeError("deployment_prerequisite_failed") from None
        digest = refreshed["bindings"]["planSha256"]
        self.execute(f"""set -euo pipefail
plan=$(mktemp /var/lib/airlinedemo/foundry-approved.XXXXXX)
trap 'rm -f "$plan"' EXIT
cp /var/lib/airlinedemo/foundry.tfplan "$plan"
chmod 400 "$plan"
printf '%s  %s\\n' '{digest}' "$plan" | sha256sum --check --status
terraform -chdir=infra/terraform apply -input=false "$plan"
""")

    def foundry_agents(self):
        setup = json.loads(self.execute("terraform -chdir=infra/terraform output -json foundry_agent_setup"))
        if not isinstance(setup, dict):
            raise RuntimeError("foundry_agents_not_ready")
        publisher_resource_id = setup.get("publisher_identity_resource_id")
        if not isinstance(publisher_resource_id, str) or not publisher_resource_id.startswith(
                f"/subscriptions/{self.args.subscription_id}/resourceGroups/{self.app_rg}/"):
            raise RuntimeError("foundry_publisher_identity_invalid")
        publisher = az("identity", "show", "--ids", publisher_resource_id)
        publisher_client_id = publisher.get("clientId")
        if not isinstance(publisher_client_id, str) or not GUID.fullmatch(publisher_client_id):
            raise RuntimeError("foundry_publisher_identity_invalid")
        executor = az("vm", "show", "-g", self.rg, "-n", self.vm)
        assigned = (executor.get("identity") or {}).get("userAssignedIdentities") or {}
        if publisher_resource_id.lower() in {identity.lower() for identity in assigned}:
            raise RuntimeError("foundry_publisher_identity_already_attached")

        config = {
            "projectEndpoint": setup.get("project_endpoint"),
            "projectName": setup.get("project_name"),
            "publisherClientId": publisher_client_id,
            "modelDeployment": setup.get("runtime_deployment"),
            "modelName": setup.get("runtime_model_name"),
            "modelVersion": setup.get("runtime_model_version"),
            "filterPolicy": setup.get("filter_policy"),
            "manifestPath": "/var/lib/airlinedemo/managed-agent-release.json",
            "stateDirectory": "/var/lib/airlinedemo/managed-agent-setup",
        }
        encoded = base64.b64encode(json.dumps(config, separators=(",", ":")).encode()).decode()
        output = self.execute(f"""umask 077
printf '%s' '{encoded}' | base64 -d > /var/lib/airlinedemo/managed-agent-setup.json
chmod 600 /var/lib/airlinedemo/managed-agent-setup.json
dotnet run --project backend/AirlineDemo.Deployment -- managed-agents plan /var/lib/airlinedemo/managed-agent-setup.json
""")
        preview_line = next((line for line in output.splitlines()
                             if line.startswith("MANAGED_AGENT_PREVIEW:")), None)
        if preview_line is None:
            raise RuntimeError("foundry_agent_preview_missing")
        try:
            preview = json.loads(preview_line.removeprefix("MANAGED_AGENT_PREVIEW:"))
            agents = preview["agents"]
            if (preview["projectName"] != setup["project_name"] or len(agents) != 2 or
                    {agent["workload"] for agent in agents} != {"copilot", "investigator"} or
                    any(not re.fullmatch(r"[0-9a-f]{64}", agent["configurationId"]) for agent in agents)):
                raise ValueError()
        except (KeyError, TypeError, ValueError, json.JSONDecodeError):
            raise RuntimeError("foundry_agent_preview_invalid") from None
        confirm(f"Publish the two immutable Foundry agents to {setup['project_name']} "
                f"using definition preview {canonical_sha256(preview)}?")

        run("az", "vm", "identity", "assign", "-g", self.rg, "-n", self.vm,
            "--identities", publisher_resource_id, "-o", "none")
        try:
            publish_output = self.remote(f"""set -euo pipefail
export AZURE_CONFIG_DIR=/var/lib/airlinedemo/azure
cd {shlex.quote(str(self.source))}
az account set --subscription {shlex.quote(self.args.subscription_id)}
dotnet run --project backend/AirlineDemo.Deployment -- managed-agents publish /var/lib/airlinedemo/managed-agent-setup.json
""")
        finally:
            run("az", "vm", "identity", "remove", "-g", self.rg, "-n", self.vm,
                "--identities", publisher_resource_id, "-o", "none")

        manifest_line = next((line for line in publish_output.splitlines()
                              if line.startswith("MANAGED_AGENT_MANIFEST:")), None)
        if manifest_line is None:
            raise RuntimeError("foundry_agent_manifest_missing")
        try:
            manifest = json.loads(manifest_line.removeprefix("MANAGED_AGENT_MANIFEST:"))
            entries = manifest["agents"]
            preview_agents = {agent["workload"]: agent for agent in agents}
            if (manifest["schemaVersion"] != 1 or manifest["projectName"] != setup["project_name"] or
                    manifest["projectEndpoint"] != setup["project_endpoint"] or len(entries) != 2 or
                    {entry["workload"] for entry in entries} != {"copilot", "investigator"} or
                    any(not re.fullmatch(r"[0-9]+", entry["version"]) or
                        not re.fullmatch(r"[0-9a-f]{64}", entry["configurationId"]) or
                        entry["configurationId"] != preview_agents[entry["workload"]]["configurationId"] or
                        entry["name"] != preview_agents[entry["workload"]]["name"] or
                        entry["definitionSha256"] != preview_agents[entry["workload"]]["definitionSha256"] or
                        any(entry[field] != preview_agents[entry["workload"]][field]
                            for field in ("modelDeployment", "modelName", "modelVersion", "filterPolicy"))
                        for entry in entries)):
                raise ValueError()
        except (KeyError, TypeError, ValueError, json.JSONDecodeError):
            raise RuntimeError("foundry_agent_manifest_invalid") from None
        write_private(self.directory / "managed-agent-release.json", json.dumps(manifest, indent=2))
        print("Saved the exact managed-agent release manifest locally; runtime gateway profile remains deny-only.")

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
        if not re.fullmatch(r"[0-9a-f]{40}", revision):
            raise RuntimeError("deployment_revision_invalid")
        deployable = ("scripts", "infra", "backend", "frontend", "Dockerfile")
        if run("git", "-C", str(ROOT), "status", "--porcelain", "--untracked-files=no", "--",
               *deployable, capture=True).strip():
            raise RuntimeError("deployment_source_dirty")
        repository = json.loads(run("gh", "repo", "view", "--json", "nameWithOwner", capture=True))["nameWithOwner"]
        if repository != "wesback/airlinedemo":
            raise ValueError("This release identity configuration is scoped to wesback/airlinedemo.")
        try:
            published = json.loads(run("gh", "api", f"repos/{repository}/commits/{revision}", capture=True))["sha"]
        except (subprocess.CalledProcessError, KeyError, ValueError):
            raise RuntimeError("deployment_revision_unpublished") from None
        if published != revision:
            raise RuntimeError("deployment_revision_unpublished")
        with tempfile.TemporaryDirectory() as directory:
            snapshot = Path(directory) / "source.tar"
            run("git", "-C", str(ROOT), "archive", "--format=tar", f"--output={snapshot}", revision)
            source_digest = hashlib.sha256(snapshot.read_bytes()).hexdigest()
        archive = io.BytesIO()
        with tarfile.open(fileobj=archive, mode="w:gz") as package:
            for name in ("deploy.tfvars.json", "backend.hcl"):
                path = self.directory / name
                if path.exists():
                    package.add(path, arcname=f"operator-settings/{name}")
                fresh = self.directory / "fresh" / self.args.prefix / name
                if fresh.exists():
                    package.add(fresh, arcname=f"fresh-settings/{name}")
        encoded = base64.b64encode(archive.getvalue()).decode()
        self.source = f"/var/lib/airlinedemo/source/{revision}-{source_digest[:12]}"
        self.remote(f"""set -euo pipefail
umask 077
if [ ! -d {self.source}/.git ]; then
  git clone --quiet --filter=blob:none --no-checkout https://github.com/{repository}.git {self.source}
  git -C {self.source} checkout --quiet --detach {revision}
fi
test "$(git -C {self.source} rev-parse HEAD)" = '{revision}'
git -C {self.source} diff --quiet HEAD --
test "$(git -C {self.source} archive --format=tar HEAD | sha256sum | cut -d ' ' -f 1)" = '{source_digest}'
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
                                           "prepare", "foundry-plan", "foundry-preflight", "foundry-apply", "foundry-agents",
                                           "platform", "database", "configure", "build",
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
