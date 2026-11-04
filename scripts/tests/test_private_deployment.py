import importlib.util
import copy
import contextlib
import io
import json
import os
import runpy
from pathlib import Path
import subprocess
import tempfile
import unittest
import tarfile
from unittest.mock import Mock, patch
from urllib.parse import parse_qs

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("private_deployment", ROOT / "scripts/deploy-private.py")
deployment = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deployment)


class FoundryRecoveryTests(unittest.TestCase):
    scope = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo"
    subnet_id = scope + "/providers/Microsoft.Network/virtualNetworks/vnet-demo/subnets/snet-foundry-agent"
    association = "azurerm_subnet_network_security_group_association.foundry_agent[0]"

    def test_association_import_isolates_graph_and_never_applies(self):
        for fail_init in (False, True):
            with self.subTest(fail_init=fail_init):
                paths = []

                def terraform(command, check):
                    self.assertTrue(check)
                    root = Path(command[1].removeprefix("-chdir="))
                    paths.append(root)
                    config = json.loads((root / "main.tf.json").read_text())
                    self.assertEqual({"azurerm": {"use_azuread_auth": True}}, config["terraform"]["backend"])
                    self.assertEqual({"resource", "provider", "terraform"}, set(config))
                    self.assertEqual({"azurerm_subnet_network_security_group_association"}, set(config["resource"]))
                    self.assertEqual((ROOT / "infra/terraform/.terraform.lock.hcl").read_bytes(),
                                     (root / ".terraform.lock.hcl").read_bytes())
                    self.assertEqual("none", config["provider"]["azurerm"]["resource_provider_registrations"])
                    if command[2] == "init":
                        self.assertEqual(["init", "-input=false", "-lockfile=readonly",
                                          "-backend-config=" + str(Path("backend.hcl").resolve())], command[2:])
                        if fail_init:
                            raise subprocess.CalledProcessError(1, command)
                    else:
                        self.assertEqual(["import", "-input=false", self.association, self.subnet_id], command[2:])

                with patch.object(subprocess, "run", side_effect=terraform) as run:
                    if fail_init:
                        with self.assertRaises(subprocess.CalledProcessError):
                            deployment.import_foundry_network((self.association, self.subnet_id),
                                                              "backend.hcl", "subscription", "tenant")
                    else:
                        deployment.import_foundry_network((self.association, self.subnet_id),
                                                          "backend.hcl", "subscription", "tenant")
                    self.assertEqual(1 if fail_init else 2, run.call_count)
                self.assertTrue(all(not path.exists() for path in paths))

    def test_price_read_retries_only_transient_transport_failures(self):
        failures = [deployment.URLError(ConnectionResetError("reset")), TimeoutError("timeout")]
        failures.extend(deployment.HTTPError("https://prices.azure.com", status, "unavailable", None, None)
                        for status in (429, 500, 502, 503, 504))
        for error in failures:
            with self.subTest(error=error), patch.object(deployment, "urlopen", side_effect=[
                    error, io.StringIO('{"Items": []}')]) as read, \
                    patch.object(deployment.time, "sleep") as sleep:
                self.assertEqual({"Items": []}, deployment.read_retail_price_page("https://prices.azure.com"))
                self.assertEqual(2, read.call_count)
                read.assert_called_with("https://prices.azure.com", timeout=30)
                sleep.assert_called_once_with(1)

    def test_price_read_stops_after_four_attempts(self):
        error = deployment.URLError(ConnectionResetError("reset"))
        with patch.object(deployment, "urlopen", side_effect=error) as read, \
                patch.object(deployment.time, "sleep") as sleep:
            with self.assertRaises(deployment.URLError):
                deployment.read_retail_price_page("https://prices.azure.com")
            self.assertEqual(4, read.call_count)
            self.assertEqual([1, 2, 4], [call.args[0] for call in sleep.call_args_list])

    def test_price_read_does_not_retry_denial_certificate_or_invalid_json(self):
        for error in (deployment.HTTPError("https://prices.azure.com", 403, "denied", None, None),
                      deployment.URLError(deployment.ssl.SSLCertVerificationError("untrusted"))):
            with self.subTest(error=error), patch.object(deployment, "urlopen", side_effect=error) as read, \
                    patch.object(deployment.time, "sleep") as sleep:
                with self.assertRaises(type(error)):
                    deployment.read_retail_price_page("https://prices.azure.com")
                self.assertEqual(1, read.call_count)
                sleep.assert_not_called()
        with patch.object(deployment, "urlopen", return_value=io.StringIO("{")) as read, \
                patch.object(deployment.time, "sleep") as sleep:
            with self.assertRaises(json.JSONDecodeError):
                deployment.read_retail_price_page("https://prices.azure.com")
            self.assertEqual(1, read.call_count)
            sleep.assert_not_called()

    def state(self):
        return {"values": {"root_module": {"resources": [
            {"address": "azurerm_subnet.foundry_agent[0]", "values": {"id": self.subnet_id}}]}}}

    def test_network_import_reuses_only_exact_existing_agent_subnet(self):
        read = Mock(return_value={"id": self.subnet_id, "networkSecurityGroup": {
            "id": self.scope + "/providers/Microsoft.Network/networkSecurityGroups/tenant-nsg"}})
        self.assertEqual((self.association, self.subnet_id),
                         deployment.foundry_network_import(self.state(), self.scope, read))
        read.assert_called_once_with(self.subnet_id)
        for state in ({}, {"values": {"root_module": {"resources": [{"address": self.association}]}}}):
            read.reset_mock()
            self.assertIsNone(deployment.foundry_network_import(state, self.scope, read))
            read.assert_not_called()
        read.return_value = {"id": self.subnet_id}
        self.assertIsNone(deployment.foundry_network_import(self.state(), self.scope, read))

    def test_network_import_rejects_foreign_subnet_readback_and_nsg_scope(self):
        for changed in ("state", "readback", "nsg"):
            with self.subTest(changed=changed):
                state = self.state()
                live = {"id": self.subnet_id, "networkSecurityGroup": {
                    "id": self.scope + "/providers/Microsoft.Network/networkSecurityGroups/tenant-nsg"}}
                if changed == "state":
                    state["values"]["root_module"]["resources"][0]["values"]["id"] = self.subnet_id.replace(
                        "/rg-demo/", "/rg-foreign/")
                elif changed == "readback":
                    live["id"] = self.subnet_id.replace("snet-foundry-agent", "another-subnet")
                else:
                    live["networkSecurityGroup"]["id"] = live["networkSecurityGroup"]["id"].replace(
                        "/rg-demo/", "/rg-foreign/")
                read = Mock(return_value=live)
                with self.assertRaises(ValueError):
                    deployment.foundry_network_import(state, self.scope, read)
                self.assertEqual(0 if changed == "state" else 1, read.call_count)


class PrivateDeploymentTests(unittest.TestCase):
    def foundry_template(self, scope):
        settings = {name: scope + "/providers/" + kind + "/synthetic"
                    for name, kind in (("blobStore", "Microsoft.Storage/storageAccounts"),
                                       ("documentStore", "Microsoft.DocumentDB/databaseAccounts"),
                                       ("vectorStore", "Microsoft.Search/searchServices"))}
        template = {
            "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
            "contentVersion": "1.0.0.0",
            "parameters": {"accountName": {"type": "string"}, "projectName": {"type": "string"}},
            "outputs": deployment.foundry_template_outputs(),
            "resources": [
                {"type": "Microsoft.CognitiveServices/accounts", "apiVersion": "2026-07-15-preview",
                 "name": "[parameters('accountName')]", "location": "swedencentral",
                 "kind": "AIServices", "sku": {"name": "S0"}, "identity": {"type": "SystemAssigned"},
                 "properties": {"allowProjectManagement": True, "customSubDomainName": "[parameters('accountName')]",
                                "disableLocalAuth": True, "publicNetworkAccess": "Disabled",
                                "capabilitySettings": settings,
                                "networkInjections": [{"scenario": "agent", "useMicrosoftManagedNetwork": False,
                                                       "subnetArmId": scope + "/providers/Microsoft.Network/virtualNetworks/synthetic/subnets/agents"}]}},
                {"type": "Microsoft.CognitiveServices/accounts/projects", "apiVersion": "2026-07-15-preview",
                 "name": "[format('{0}/{1}', parameters('accountName'), parameters('projectName'))]",
                 "location": "swedencentral", "identity": {"type": "SystemAssigned"},
                 "dependsOn": ["[resourceId('Microsoft.CognitiveServices/accounts', parameters('accountName'))]"],
                 "properties": {"displayName": "[parameters('projectName')]", "capabilitySettings": settings}},
            ],
        }
        return template, {"accountName": {"value": "synthetic-account"}, "projectName": {"value": "synthetic-project"}}

    def test_foundry_plan_fingerprint_uses_real_wrapper_and_refuses_deletes(self):
        template, parameters = self.foundry_template("/synthetic")
        wrapper = {"address": "azurerm_resource_group_template_deployment.foundry[0]",
                   "values": {"deployment_mode": "Incremental", "debug_level": "none",
                              "template_content": json.dumps(template), "parameters_content": json.dumps(parameters)}}
        plan = {"planned_values": {"root_module": {"resources": [wrapper]}},
                "resource_changes": [{"change": {"actions": ["create"]}}]}
        result = deployment.foundry_plan_fingerprint(plan, b"saved plan", "a" * 40)
        self.assertEqual({"sourceCommit": "a" * 40,
                          "planSha256": deployment.hashlib.sha256(b"saved plan").hexdigest(),
                          "configurationSha256": deployment.canonical_sha256(plan["planned_values"]),
                          "templateSha256": deployment.canonical_sha256(template),
                          "parametersSha256": deployment.canonical_sha256(parameters),
                          "models": [], "retainedModels": []}, result)
        for path, value in ((("resource_changes", 0, "change", "actions"), ["delete", "create"]),
                            (("planned_values", "root_module", "resources"), []),
                            (("planned_values", "root_module", "resources", 0, "values", "deployment_mode"), "Complete"),
                            (("planned_values", "root_module", "resources", 0, "values", "debug_level"), "requestContent")):
            with self.subTest(path=path):
                broken = copy.deepcopy(plan)
                target = broken
                for part in path[:-1]:
                    target = target[part]
                target[path[-1]] = value
                with self.assertRaises(ValueError):
                    deployment.foundry_plan_fingerprint(broken, b"saved plan", "a" * 40)
        scope = "/synthetic"
        wrapper["values"]["resource_group_name"] = "synthetic"
        projected = deployment.foundry_plan_fingerprint(plan, b"saved plan", "a" * 40, scope)
        self.assertEqual(template, projected["template"])
        self.assertEqual(parameters, projected["parameters"])
        wrapper["values"]["resource_group_name"] = "different"
        with self.assertRaises(ValueError):
            deployment.foundry_plan_fingerprint(plan, b"saved plan", "a" * 40, scope)
        plan["planned_values"]["root_module"]["resources"].append({
            "address": "azurerm_cognitive_deployment.synthetic", "type": "azurerm_cognitive_deployment",
            "values": {"model": [{"format": "OpenAI", "name": "synthetic-model", "version": "2026-01-01"}],
                       "sku": [{"name": "Standard", "capacity": 2}]}})
        self.assertEqual([{"name": "synthetic-model", "version": "2026-01-01", "sku": "Standard", "capacity": 2}],
                         deployment.foundry_plan_fingerprint(plan, b"saved plan", "a" * 40)["models"])
        model = plan["planned_values"]["root_module"]["resources"][-1]
        model["values"]["id"] = "/synthetic/deployments/retained"
        plan["resource_changes"] = [{"address": model["address"], "change": {
            "actions": ["no-op"], "before": copy.deepcopy(model["values"]), "after": copy.deepcopy(model["values"])}}]
        self.assertEqual([{"deploymentId": model["values"]["id"],
                           "model": {"name": "synthetic-model", "version": "2026-01-01", "sku": "Standard", "capacity": 2}}],
                         deployment.foundry_plan_fingerprint(plan, b"saved plan", "a" * 40)["retainedModels"])
        for action in ("create", "update"):
            plan["resource_changes"][0]["change"]["actions"] = [action]
            self.assertEqual([], deployment.foundry_plan_fingerprint(plan, b"saved plan", "a" * 40)["retainedModels"])
        plan["resource_changes"][0]["change"]["actions"] = ["no-op"]
        plan["resource_changes"][0]["change"]["after"]["sku"][0]["capacity"] = 3
        with self.assertRaises(ValueError):
            deployment.foundry_plan_fingerprint(plan, b"saved plan", "a" * 40)

    def test_foundry_plan_evidence_roundtrips_complete_candidate_through_output_limit(self):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.foundry_executor(directory)
            instance.app_rg = "rg-demo1"
            scope = "/subscriptions/" + instance.args.subscription_id + "/resourceGroups/rg-demo1"
            template, parameters = self.foundry_template(scope)
            plan = {"planned_values": {"root_module": {"resources": [{
                "address": "azurerm_resource_group_template_deployment.foundry[0]",
                "values": {"resource_group_name": "rg-demo1", "deployment_mode": "Incremental",
                           "debug_level": "none", "template_content": json.dumps(template),
                           "parameters_content": json.dumps(parameters)}}]}}, "resource_changes": []}
            plan["planned_values"]["root_module"]["resources"].extend({
                "address": "azurerm_cognitive_deployment.synthetic_" + str(index),
                "type": "azurerm_cognitive_deployment",
                "values": {"model": [{"name": "synthetic-" + str(index), "version": "2026-01-01"}],
                           "sku": [{"name": "Standard", "capacity": 1}]}
            } for index in range(40))
            expected = deployment.foundry_plan_fingerprint(plan, b"saved plan", "a" * 40, scope)
            self.assertGreater(len(json.dumps(expected).encode()), 4096)
            temporary_file = tempfile.NamedTemporaryFile

            def execute(script):
                producer = script.split("<<'PY'\n", 1)[1].rsplit("\nPY", 1)[0]
                output = io.StringIO()
                with patch("runpy.run_path", return_value={
                    "foundry_plan_fingerprint": deployment.foundry_plan_fingerprint,
                    "require_evidence": deployment.require_evidence
                }), patch("pathlib.Path.read_bytes", return_value=b"saved plan"), \
                        patch("tempfile.NamedTemporaryFile", side_effect=lambda **kwargs: temporary_file(
                            dir=directory, prefix=kwargs["prefix"])), \
                        patch("subprocess.check_output", side_effect=[json.dumps(plan).encode(), "a" * 40]), \
                        contextlib.redirect_stdout(output):
                    exec(compile(producer, "<saved-plan-producer>", "exec"), {})
                return output.getvalue()[-4096:]

            instance.execute.side_effect = execute
            self.assertEqual(expected, instance.foundry_plan_evidence(candidate=True))

    def test_foundry_plan_evidence_refuses_invalid_or_truncated_transport(self):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.foundry_executor(directory)
            for output in ("not-base64", "eJw=", "x" * 4097):
                with self.subTest(output_length=len(output)):
                    instance.execute.return_value = output
                    with self.assertRaisesRegex(RuntimeError, "^foundry_plan_evidence_invalid$"):
                        instance.foundry_plan_evidence()

    @patch.object(deployment, "az")
    def test_foundry_native_arm_verifies_only_the_exact_closed_candidate(self, az):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.foundry_executor(directory)
            instance.app_rg = "rg-demo1"
            scope = "/subscriptions/" + instance.args.subscription_id + "/resourceGroups/rg-demo1"
            template, parameters = self.foundry_template(scope)
            request = {**self.foundry_candidate(), "runId": "current-run",
                       "template": template, "parameters": parameters}
            request["bindings"].update(templateSha256=deployment.canonical_sha256(template),
                                       parametersSha256=deployment.canonical_sha256(parameters))
            validation = {"properties": {"provisioningState": "Succeeded"}}
            account_id = scope + "/providers/Microsoft.CognitiveServices/accounts/synthetic-account"
            what_if = {"status": "Succeeded", "changes": [
                {"resourceId": account_id, "changeType": "Create"},
                {"resourceId": account_id + "/projects/synthetic-project", "changeType": "Create"}]}
            az.side_effect = [validation, what_if]
            result = instance.foundry_arm_evidence(request)
            self.assertEqual({"validate": "Succeeded", "whatIf": "Succeeded",
                              "templateSha256": request["bindings"]["templateSha256"],
                              "parametersSha256": request["bindings"]["parametersSha256"],
                              "changeSha256": deployment.canonical_sha256(what_if["changes"])}, result)
            self.assertEqual(("deployment", "group", "validate"), az.call_args_list[0].args[:3])
            self.assertEqual(("deployment", "group", "what-if"), az.call_args_list[1].args[:3])
            self.assertIn("Incremental", az.call_args.args)
            self.assertEqual("Ignore", az.call_args.args[az.call_args.args.index("--exclude-change-types") + 1])
            self.assertEqual(0o600, (instance.directory / "foundry-native-arm.json").stat().st_mode & 0o777)
            self.assertFalse(any(instance.directory.glob("foundry-arm-*")))
            for change_type, resource_id in (("Delete", account_id), ("Unsupported", account_id), ("Ignore", account_id),
                                             ("Modify", scope + "/unrelated")):
                with self.subTest(change_type=change_type, resource_id=resource_id):
                    changes = copy.deepcopy(what_if["changes"])
                    changes[0] = {"resourceId": resource_id, "changeType": change_type}
                    az.side_effect = [validation, {"status": "Succeeded", "changes": changes}]
                    with self.assertRaises(ValueError):
                        instance.foundry_arm_evidence(request)
                    instance.execute.assert_not_called()

            for path, value in ((("properties", "publicNetworkAccess"), "Enabled"),
                                (("properties", "disableLocalAuth"), False),
                                (("properties", "unexpected"), "not-admitted"),
                                (("properties", "capabilitySettings", "documentStore"), 42),
                                (("properties", "networkInjections", 0, "useMicrosoftManagedNetwork"), True),
                                (("apiVersion",), "2026-05-15-preview")):
                with self.subTest(path=path):
                    changed = copy.deepcopy(request)
                    target = changed["template"]["resources"][0]
                    for part in path[:-1]:
                        target = target[part]
                    target[path[-1]] = value
                    changed["bindings"]["templateSha256"] = deployment.canonical_sha256(changed["template"])
                    az.reset_mock()
                    with self.assertRaises((ValueError, TypeError)):
                        instance.foundry_arm_evidence(changed)
                    az.assert_not_called()

            for name, value in (("projectInternalId", "[listKeys('unapproved', '2026-07-15-preview')]"),
                                ("projectPrincipalId", "[parameters('accountName')]"),
                                ("extraOutput", "[parameters('accountName')]")):
                with self.subTest(output=name):
                    changed = copy.deepcopy(request)
                    changed["template"]["outputs"][name] = {"type": "string", "value": value}
                    changed["bindings"]["templateSha256"] = deployment.canonical_sha256(changed["template"])
                    az.reset_mock()
                    with self.assertRaises(ValueError):
                        instance.foundry_arm_evidence(changed)
                    az.assert_not_called()

            self.assertEqual({"projectPrincipalId", "projectInternalId"}, set(template["outputs"]))
            changed = copy.deepcopy(request)
            changed["template"]["outputs"]["projectStoreScopes"] = {"type": "object", "value": {
                "containers": {"files": {"id": "[parameters('accountName')]", "access": "None"}},
                "cosmosDatabaseId": "[listKeys('unapproved', '2024-08-15')]"}}
            changed["bindings"]["templateSha256"] = deployment.canonical_sha256(changed["template"])
            az.reset_mock()
            with self.assertRaises(ValueError):
                instance.foundry_arm_evidence(changed)
            az.assert_not_called()

    def foundry_cloud(self, instance):
        candidate = self.foundry_candidate()
        scope = "/subscriptions/" + instance.args.subscription_id + "/resourceGroups/rg-demo1"
        instance.app_rg = "rg-demo1"
        candidate["template"], candidate["parameters"] = self.foundry_template(scope)
        candidate["bindings"].update(templateSha256=deployment.canonical_sha256(candidate["template"]),
                                     parametersSha256=deployment.canonical_sha256(candidate["parameters"]))
        candidate["models"] = [{"name": "synthetic-model", "version": "2026-01-01", "sku": "Standard", "capacity": 2}]
        candidate["bindings"]["assignmentSha256"] = deployment.canonical_sha256([])
        inventory = [{"id": scope + "/retained", "type": "synthetic"}]
        candidate["bindings"]["retainedResourceSha256"] = deployment.canonical_sha256(inventory)
        meter = "33333333-3333-3333-3333-333333333333"
        candidate.update(meterIds=[meter], meterSkus={meter: "Standard"},
                         usage={meter: {"unit": "1 Hour", "productName": "Synthetic", "quantity": 3,
                                        "usageAssumption": "three additional synthetic instance hours"}})
        instance.execute.return_value = deployment.base64.b64encode(deployment.zlib.compress(json.dumps({
            **{name: candidate["bindings"][name] for name in
               ("sourceCommit", "configurationSha256", "planSha256", "templateSha256", "parametersSha256")},
            "models": candidate["models"]}).encode())).decode()
        providers = {}
        for namespace, names in (("Microsoft.CognitiveServices", ["accounts", "accounts/projects"]),
                                 ("Microsoft.Storage", ["storageAccounts"]),
                                 ("Microsoft.Search", ["searchServices"]),
                                 ("Microsoft.Network", ["virtualNetworks"]),
                                 ("Microsoft.App", ["managedEnvironments"]),
                                 ("Microsoft.DocumentDB", ["databaseAccounts"])):
            providers[namespace] = {"registrationState": "Registered", "resourceTypes": [
                {"resourceType": name, "apiVersions": ["2026-07-15-preview"], "locations": ["Sweden Central"]}
                for name in names]}
        sources = {
            "account": {"id": instance.args.subscription_id, "tenantId": instance.args.tenant_id},
            "policy": [], "resource": inventory,
            "list-skus": [{"name": "S0", "kind": "AIServices", "restrictions": []}],
            "model": [{"model": {"name": "synthetic-model", "version": "2026-01-01", "format": "OpenAI",
                                  "skus": [{"name": "Standard", "usageName": "model",
                                            "capacity": {"minimum": 1, "maximum": 10, "step": 1}}]}}],
            "usage": [{"name": {"value": "model"}, "unit": "Count", "limit": 5, "currentValue": 2}],
        }

        def cloud(*args, **kwargs):
            if args[0] == "provider":
                return copy.deepcopy(providers[args[args.index("--namespace") + 1]])
            if args[0] == "cognitiveservices":
                return copy.deepcopy(sources["list-skus" if args[1] == "account" else args[1]])
            if args[:3] == ("deployment", "group", "validate"):
                return {"properties": {"provisioningState": "Succeeded"}}
            if args[:3] == ("deployment", "group", "what-if"):
                account_id = scope + "/providers/Microsoft.CognitiveServices/accounts/synthetic-account"
                return {"status": "Succeeded", "changes": [
                    {"resourceId": account_id + suffix, "changeType": "Create"}
                    for suffix in ("", "/projects/synthetic-project")]}
            return copy.deepcopy(sources[args[0]])

        page = {"Items": [{"meterId": meter, "armRegionName": "swedencentral", "currencyCode": "USD",
                           "type": "Consumption", "isPrimaryMeterRegion": True, "tierMinimumUnits": 0,
                           "effectiveStartDate": "2026-01-01T00:00:00Z", "skuName": "Standard",
                           "unitOfMeasure": "1 Hour", "productName": "Synthetic", "retailPrice": 0.1}],
                "NextPageLink": None}
        return candidate, sources, providers, cloud, page

    def test_foundry_fingerprint_projects_actual_search_location_and_sku(self):
        template, parameters = self.foundry_template("/synthetic")
        wrapper = {"address": "azurerm_resource_group_template_deployment.foundry[0]", "values": {
            "deployment_mode": "Incremental", "debug_level": "none",
            "template_content": json.dumps(template), "parameters_content": json.dumps(parameters)}}
        search = {"address": "azurerm_search_service.foundry[0]", "values": {
            "location": "germanywestcentral", "sku": "standard"}}
        plan = {"planned_values": {"root_module": {"resources": [wrapper, search]}}, "resource_changes": []}
        self.assertEqual(search["values"], deployment.foundry_plan_fingerprint(plan, b"saved", "a" * 40)["search"])

    def test_foundry_french_search_uses_saved_plan_region_and_rejects_missing_capacity(self):
        for scenario in ("available", "no-quota", "unsupported"):
            with self.subTest(scenario=scenario), tempfile.TemporaryDirectory() as directory:
                instance = self.foundry_executor(directory)
                candidate, _, providers, cloud, _ = self.foundry_cloud(instance)
                candidate.update(startedAt=deployment.utc_now(), runId="current")
                projection = json.loads(deployment.zlib.decompress(
                    deployment.base64.b64decode(instance.execute.return_value)))
                projection["search"] = {"location": "francecentral", "sku": "standard"}
                instance.execute.return_value = deployment.base64.b64encode(
                    deployment.zlib.compress(json.dumps(projection).encode())).decode()
                if scenario != "unsupported":
                    providers["Microsoft.Search"]["resourceTypes"][0]["locations"] = ["France Central"]
                observed = []

                def regional_cloud(*args, **kwargs):
                    if args[0] == "rest":
                        observed.append(args[args.index("--url") + 1])
                        return {"value": [{"name": {"value": "standard"}, "currentValue": 0,
                                           "limit": 0 if scenario == "no-quota" else 12}]}
                    return cloud(*args, **kwargs)

                with patch.object(deployment, "az", side_effect=regional_cloud):
                    if scenario == "available":
                        instance.foundry_discovery(candidate)
                    else:
                        with self.assertRaises(ValueError):
                            instance.foundry_discovery(candidate)
                self.assertEqual([] if scenario == "unsupported" else [
                    "https://management.azure.com/subscriptions/" + instance.args.subscription_id +
                    "/providers/Microsoft.Search/locations/francecentral/usages?api-version=2025-05-01"], observed)

    def test_french_price_meter_is_allowed_only_for_search(self):
        for product in ("Azure AI Search", "Synthetic"):
            with self.subTest(product=product), tempfile.TemporaryDirectory() as directory:
                instance = self.foundry_executor(directory)
                candidate, _, _, _, page = self.foundry_cloud(instance)
                meter = candidate["meterIds"][0]
                candidate.update(startedAt=deployment.utc_now(), runId="current")
                candidate["usage"][meter].update(region="francecentral", productName=product)
                page["Items"][0].update(armRegionName="francecentral", productName=product)
                with patch.object(deployment, "urlopen", return_value=io.StringIO(json.dumps(page))) as prices:
                    if product == "Azure AI Search":
                        self.assertEqual(0.3, instance.foundry_quote(candidate)["estimatedCost"])
                        self.assertEqual(["armRegionName eq 'francecentral' and meterId eq '" + meter + "'"],
                                         parse_qs(deployment.urlparse(prices.call_args.args[0]).query)["$filter"])
                    else:
                        with self.assertRaises(ValueError):
                            instance.foundry_quote(candidate)
                        prices.assert_not_called()

    @patch.object(deployment, "run", return_value="a" * 40)
    def test_foundry_cli_preflight_acquires_authentication_inventory_prices_and_native_checks(self, run):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.foundry_executor(directory)
            candidate, sources, providers, cloud, page = self.foundry_cloud(instance)
            deployment.write_private(instance.directory / "foundry-candidate.json", json.dumps(candidate))

            def assess(message):
                request = json.loads((instance.directory / "foundry-preflight-request.json").read_text())
                evidence = self.foundry_evidence(request)
                evidence["capacity"]["quotaRequestsSha256"] = deployment.canonical_sha256(request["quotaRequests"])
                deployment.write_private(instance.directory / "foundry-assessments.json",
                                         json.dumps({key: evidence[key] for key in ("capacity", "policy")}))

            with patch.object(deployment, "az", side_effect=cloud) as az, \
                    patch.object(deployment, "urlopen", side_effect=lambda *a, **k: io.StringIO(json.dumps(page))) as prices, \
                    patch.object(deployment, "confirm", side_effect=assess):
                result = instance.foundry_preflight()
            self.assertEqual(0.3, result["quote"]["estimatedCost"])
            self.assertEqual([{"name": "model", "unit": "Count", "requested": 2, "available": 3.0}], result["quota"])
            self.assertEqual(["Succeeded", "Succeeded"], [result["arm"][key] for key in ("validate", "whatIf")])
            self.assertEqual(14, az.call_count)
            prices.assert_called_once()
            self.assertEqual("prices.azure.com", deployment.urlparse(prices.call_args.args[0]).netloc)
            self.assertEqual(1, instance.execute.call_count)
            for name in ("foundry-discovery.json", "foundry-prices.json", "foundry-preflight.json"):
                self.assertEqual(0o600, (instance.directory / name).stat().st_mode & 0o777)

    @patch.object(deployment, "run", return_value="a" * 40)
    def test_foundry_discovery_accepts_identical_model_records_and_explicit_default_capacity(self, run):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.foundry_executor(directory)
            candidate, sources, providers, cloud, page = self.foundry_cloud(instance)
            model = sources["model"][0]["model"]
            model["skus"][0]["capacity"] = {"minimum": None, "maximum": 10, "step": None, "default": 2}
            sources["model"].append(copy.deepcopy(sources["model"][0]))
            candidate["startedAt"] = deployment.utc_now()
            candidate["runId"] = "current-run"
            with patch.object(deployment, "az", side_effect=cloud):
                result = instance.foundry_discovery(candidate)
            self.assertEqual("available", result["provider"]["status"])
            self.assertEqual([{"name": "model", "unit": "Count", "requested": 2, "available": 3.0}],
                             result["quota"])
            self.assertEqual(0o600, (instance.directory / "foundry-discovery.json").stat().st_mode & 0o777)

    @patch.object(deployment, "run", return_value="a" * 40)
    def test_foundry_discovery_refuses_conflicting_models_unknown_bounds_and_understated_quota(self, run):
        for scenario in ("conflicting-model", "nondefault", "missing-default", "default-over-maximum",
                         "missing-minimum", "below-minimum", "invalid-step", "off-step",
                         "fractional", "zero", "understated-quota"):
            with self.subTest(scenario=scenario), tempfile.TemporaryDirectory() as directory:
                instance = self.foundry_executor(directory)
                candidate, sources, providers, cloud, page = self.foundry_cloud(instance)
                capacity = sources["model"][0]["model"]["skus"][0]["capacity"]
                if scenario == "conflicting-model":
                    different = copy.deepcopy(sources["model"][0])
                    different["model"]["skus"][0]["capacity"]["maximum"] = 20
                    sources["model"].append(different)
                elif scenario in ("nondefault", "missing-default", "default-over-maximum", "missing-minimum"):
                    capacity.update(minimum=None, step=None, default=3)
                    if scenario == "missing-default":
                        capacity["default"] = None
                    elif scenario == "default-over-maximum":
                        capacity.update(default=2, maximum=1)
                    elif scenario == "missing-minimum":
                        capacity.update(step=1, default=3)
                elif scenario == "below-minimum":
                    capacity["minimum"] = 3
                elif scenario == "invalid-step":
                    capacity["step"] = 0
                elif scenario == "off-step":
                    capacity["step"] = 2
                elif scenario in ("fractional", "zero"):
                    candidate["models"][0]["capacity"] = 1.5 if scenario == "fractional" else 0
                else:
                    candidate["models"].append(copy.deepcopy(candidate["models"][0]))
                projection = json.loads(deployment.zlib.decompress(deployment.base64.b64decode(instance.execute.return_value)))
                projection["models"] = candidate["models"]
                instance.execute.return_value = deployment.base64.b64encode(
                    deployment.zlib.compress(json.dumps(projection).encode())).decode()
                candidate.update(startedAt=deployment.utc_now(), runId="current-run")
                with patch.object(deployment, "az", side_effect=cloud):
                    with self.assertRaises((ValueError, TypeError)):
                        instance.foundry_discovery(candidate)
                self.assertFalse((instance.directory / "foundry-discovery.json").exists())
                self.assertEqual(1, instance.execute.call_count)
                self.assertNotIn(" apply ", instance.execute.call_args.args[0])

    @patch.object(deployment, "run", return_value="a" * 40)
    def test_foundry_discovery_sums_shared_quota_for_multiple_model_deployments(self, run):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.foundry_executor(directory)
            candidate, sources, providers, cloud, page = self.foundry_cloud(instance)
            candidate["models"].append(copy.deepcopy(candidate["models"][0]))
            candidate["quotaRequests"]["model"]["requested"] = 4
            sources["usage"][0]["limit"] = 6
            projection = json.loads(deployment.zlib.decompress(deployment.base64.b64decode(instance.execute.return_value)))
            projection["models"] = candidate["models"]
            instance.execute.return_value = deployment.base64.b64encode(
                deployment.zlib.compress(json.dumps(projection).encode())).decode()
            candidate.update(startedAt=deployment.utc_now(), runId="current-run")
            with patch.object(deployment, "az", side_effect=cloud):
                result = instance.foundry_discovery(candidate)
            self.assertEqual([{"name": "model", "unit": "Count", "requested": 4, "available": 4.0}],
                             result["quota"])

    def test_foundry_discovery_verifies_exact_retained_nondefault_capacity_and_refuses_drift(self):
        for scenario in ("valid", "missing-default", "id", "state", "format", "name", "version", "sku", "capacity",
                         "same-profile-create", "foreign-scope", "duplicate-identity"):
            with self.subTest(scenario=scenario), tempfile.TemporaryDirectory() as directory:
                instance = self.foundry_executor(directory)
                candidate, sources, _, cloud, _ = self.foundry_cloud(instance)
                sources["model"][0]["model"]["skus"][0]["capacity"] = {
                    "minimum": None, "maximum": 10, "step": None, "default": 1}
                if scenario == "missing-default":
                    sources["model"][0]["model"]["skus"][0]["capacity"]["default"] = None
                deployment_id = ("/subscriptions/" + instance.args.subscription_id +
                                 "/resourceGroups/rg-demo1/providers/Microsoft.CognitiveServices/accounts/retained/deployments/model")
                retained = {"deploymentId": deployment_id, "model": copy.deepcopy(candidate["models"][0])}
                projection = json.loads(deployment.zlib.decompress(deployment.base64.b64decode(instance.execute.return_value)))
                projection["retainedModels"] = [retained]
                live = {"id": deployment_id, "properties": {"provisioningState": "Succeeded",
                        "model": {"format": "OpenAI", "name": "synthetic-model", "version": "2026-01-01"}},
                        "sku": {"name": "Standard", "capacity": 2}}
                if scenario == "id":
                    live["id"] += "-other"
                elif scenario == "state":
                    live["properties"]["provisioningState"] = "Failed"
                elif scenario in ("format", "name", "version"):
                    live["properties"]["model"][scenario] = "different"
                elif scenario == "sku":
                    live["sku"]["name"] = "different"
                elif scenario == "capacity":
                    live["sku"]["capacity"] = 3
                elif scenario in ("same-profile-create", "duplicate-identity"):
                    candidate["models"].append(copy.deepcopy(candidate["models"][0]))
                    projection["models"] = candidate["models"]
                    candidate["quotaRequests"]["model"]["requested"] = 4
                    sources["usage"][0]["limit"] = 10
                    if scenario == "duplicate-identity":
                        projection["retainedModels"].append(copy.deepcopy(retained))
                elif scenario == "foreign-scope":
                    retained["deploymentId"] = deployment_id.replace("/rg-demo1/", "/other/")
                instance.execute.return_value = deployment.base64.b64encode(
                    deployment.zlib.compress(json.dumps(projection).encode())).decode()
                candidate.update(startedAt=deployment.utc_now(), runId="current-run")

                def current_cloud(*args, **kwargs):
                    return copy.deepcopy(live) if args[:2] == ("resource", "show") else cloud(*args, **kwargs)

                with patch.object(deployment, "az", side_effect=current_cloud) as az:
                    if scenario in ("valid", "missing-default"):
                        result = instance.foundry_discovery(candidate)
                        self.assertEqual([{"name": "model", "unit": "Count", "requested": 2, "available": 3.0}],
                                         result["quota"])
                        raw = json.loads((instance.directory / "foundry-discovery.json").read_text())
                        self.assertEqual([live], raw["retainedModels"])
                        self.assertEqual(deployment.canonical_sha256(
                            {name: raw[name] for name in ("providers", "skus", "models", "retainedModels",
                                                         "search", "searchUsage")}),
                            result["provider"]["sourceSha256"])
                    else:
                        with self.assertRaises(ValueError):
                            instance.foundry_discovery(candidate)
                        self.assertFalse((instance.directory / "foundry-discovery.json").exists())
                reads = [call for call in az.call_args_list if call.args[:2] == ("resource", "show")]
                self.assertEqual(0 if scenario in ("same-profile-create", "foreign-scope", "duplicate-identity") else 1,
                                 len(reads))
                if reads:
                    self.assertEqual(deployment_id, reads[0].args[reads[0].args.index("--ids") + 1])
                self.assertEqual(1, instance.execute.call_count)
                self.assertNotIn(" apply ", instance.execute.call_args.args[0])

    @patch.object(deployment, "run", return_value="a" * 40)
    def test_foundry_discovery_refuses_unknown_sources_before_prices_assessment_or_apply(self, run):
        for field in ("account", "assignments", "inventory", "provider", "delegation-provider", "sku", "model", "quota", "unit", "plan"):
            with self.subTest(field=field), tempfile.TemporaryDirectory() as directory:
                instance = self.foundry_executor(directory)
                candidate, sources, providers, cloud, page = self.foundry_cloud(instance)
                if field == "account":
                    sources["account"]["tenantId"] = "other"
                elif field == "assignments":
                    sources["policy"] = [{"id": "changed-effective-assignment"}]
                elif field == "inventory":
                    sources["resource"] = []
                elif field == "provider":
                    providers["Microsoft.Search"]["registrationState"] = "NotRegistered"
                elif field == "delegation-provider":
                    providers["Microsoft.App"]["registrationState"] = "NotRegistered"
                elif field == "sku":
                    sources["list-skus"] = []
                elif field == "model":
                    sources["model"] = []
                elif field == "quota":
                    sources["usage"][0]["currentValue"] = 4
                elif field == "unit":
                    sources["usage"][0]["unit"] = "different"
                else:
                    changed = json.loads(deployment.zlib.decompress(deployment.base64.b64decode(instance.execute.return_value)))
                    changed["planSha256"] = "e" * 64
                    instance.execute.return_value = deployment.base64.b64encode(
                        deployment.zlib.compress(json.dumps(changed).encode())).decode()
                with patch.object(deployment, "az", side_effect=cloud), \
                        patch.object(deployment, "urlopen") as prices, patch.object(deployment, "confirm") as confirm:
                    with self.assertRaisesRegex(RuntimeError, "^foundry_preflight_refused$"):
                        instance.foundry_apply(candidate)
                prices.assert_not_called()
                confirm.assert_not_called()
                self.assertFalse((instance.directory / "foundry-preflight.json").exists())
                self.assertTrue(all("terraform -chdir=infra/terraform apply" not in call.args[0]
                                    for call in instance.execute.call_args_list))

    @patch.object(deployment, "run", return_value="a" * 40)
    def test_foundry_real_acquisition_refreshes_and_refuses_native_drift_or_missing_assessments(self, run):
        for scenario in ("valid", "native-drift", "missing-assessment"):
            with self.subTest(scenario=scenario), tempfile.TemporaryDirectory() as directory:
                instance = self.foundry_executor(directory)
                candidate, sources, providers, cloud, page = self.foundry_cloud(instance)
                deployment.write_private(instance.directory / "foundry-candidate.json", json.dumps(candidate))
                assessment_file = instance.directory / "foundry-assessments.json"
                assessment_file.write_text("stale assessment must not be reused")
                approved = []

                def assess(message):
                    if message.startswith("Apply Foundry plan"):
                        approved.append(True)
                    elif scenario != "missing-assessment":
                        request = json.loads((instance.directory / "foundry-preflight-request.json").read_text())
                        evidence = self.foundry_evidence(request)
                        evidence["capacity"]["quotaRequestsSha256"] = deployment.canonical_sha256(request["quotaRequests"])
                        deployment.write_private(assessment_file, json.dumps({
                            key: evidence[key] for key in ("capacity", "policy")}))

                def refreshed_cloud(*args, **kwargs):
                    result = cloud(*args, **kwargs)
                    if approved and scenario == "native-drift" and args[:3] == ("deployment", "group", "what-if"):
                        result["changes"][0]["changeType"] = "Modify"
                    return result

                with patch.object(deployment, "az", side_effect=refreshed_cloud), \
                        patch.object(deployment, "urlopen", side_effect=lambda *a, **k: io.StringIO(json.dumps(page))), \
                        patch.object(deployment, "confirm", side_effect=assess):
                    if scenario == "valid":
                        instance.foundry_apply()
                    else:
                        reason = "deployment_prerequisite_failed" if approved or scenario == "native-drift" else "foundry_preflight_refused"
                        with self.assertRaisesRegex(RuntimeError, "^" + reason + "$"):
                            instance.foundry_apply()
                applies = [call for call in instance.execute.call_args_list
                           if 'terraform -chdir=infra/terraform apply -input=false "$plan"' in call.args[0]]
                self.assertEqual(1 if scenario == "valid" else 0, len(applies))
                if scenario != "valid":
                    self.assertFalse((instance.directory / "foundry-preflight.json").exists())

    def test_foundry_discovery_selects_unique_sku_bound_to_requested_quota(self):
        for ambiguous in (False, True):
            with self.subTest(ambiguous=ambiguous), tempfile.TemporaryDirectory() as directory:
                instance = self.foundry_executor(directory)
                candidate, sources, _, cloud, _ = self.foundry_cloud(instance)
                candidate.update(startedAt=deployment.utc_now(), runId="current-run")
                extra = copy.deepcopy(sources["model"][0]["model"]["skus"][0])
                extra["usageName"] = "model-finetune"
                sources["model"][0]["model"]["skus"].append(extra)
                if ambiguous:
                    candidate["quotaRequests"]["model-finetune"] = {"unit": "Count", "requested": 2}
                with patch.object(deployment, "az", side_effect=cloud):
                    if ambiguous:
                        with self.assertRaises(ValueError):
                            instance.foundry_discovery(candidate)
                    else:
                        result = instance.foundry_discovery(candidate)
                        self.assertEqual([{"name": "model", "unit": "Count", "requested": 2,
                                           "available": 3.0}], result["quota"])

    def test_foundry_prices_accept_exact_regional_nonprimary_meter(self):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.foundry_executor(directory)
            candidate, sources, providers, cloud, page = self.foundry_cloud(instance)
            candidate.update(startedAt=deployment.utc_now(), runId="current-run")
            page["Items"][0]["isPrimaryMeterRegion"] = False
            with patch.object(deployment, "urlopen", return_value=io.StringIO(json.dumps(page))):
                result = instance.foundry_quote(candidate)
            self.assertEqual(0.3, result["estimatedCost"])
            self.assertEqual([{"meterId": candidate["meterIds"][0], "sku": "Standard",
                              "unit": "1 Hour", "unitPrice": 0.1, "quantity": 3,
                              "usageAssumption": "three additional synthetic instance hours",
                              "estimatedCost": 0.3}], result["items"])

    def test_foundry_prices_selects_requested_sku_for_shared_meter(self):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.foundry_executor(directory)
            candidate, _, _, _, page = self.foundry_cloud(instance)
            candidate.update(startedAt=deployment.utc_now(), runId="current-run")
            other_sku = copy.deepcopy(page["Items"][0])
            other_sku.update(skuName="Different", retailPrice=2)
            page["Items"].append(other_sku)
            with patch.object(deployment, "urlopen", return_value=io.StringIO(json.dumps(page))):
                result = instance.foundry_quote(candidate)
            self.assertEqual(0.3, result["estimatedCost"])
            self.assertEqual("Standard", result["items"][0]["sku"])

    def test_foundry_prices_reject_unverifiable_meter_rows_and_untrusted_pagination(self):
        for change in ("missing", "unit", "currency", "tier", "expired", "duplicate", "pagination"):
            with self.subTest(change=change), tempfile.TemporaryDirectory() as directory:
                instance = self.foundry_executor(directory)
                candidate, sources, providers, cloud, page = self.foundry_cloud(instance)
                candidate["startedAt"] = deployment.utc_now()
                if change == "missing":
                    page["Items"] = []
                elif change == "unit":
                    page["Items"][0]["unitOfMeasure"] = "1K Tokens"
                elif change == "currency":
                    page["Items"][0]["currencyCode"] = "EUR"
                elif change == "tier":
                    page["Items"][0]["tierMinimumUnits"] = 100
                elif change == "expired":
                    page["Items"][0]["effectiveEndDate"] = "2026-01-01T00:00:00Z"
                elif change == "duplicate":
                    page["Items"].append(copy.deepcopy(page["Items"][0]))
                else:
                    page["NextPageLink"] = "https://untrusted.invalid/api/retail/prices"
                with patch.object(deployment, "urlopen", side_effect=lambda *a, **k: io.StringIO(json.dumps(page))) as prices:
                    with self.assertRaises(ValueError):
                        instance.foundry_quote(candidate)
                self.assertEqual(1, prices.call_count)
                instance.execute.assert_not_called()

    def test_foundry_prices_global_meter_requires_explicit_scope_and_base_tier_usage(self):
        for region, quantity, expected in ((None, 3, False), ("Global", 3, True), ("", 3, True),
                                           ("Global", 100, False), ("Global", 101, False),
                                           ("different-region", 3, False)):
            with self.subTest(region=region, quantity=quantity), tempfile.TemporaryDirectory() as directory:
                instance = self.foundry_executor(directory)
                candidate, _, _, _, page = self.foundry_cloud(instance)
                candidate.update(startedAt=deployment.utc_now(), runId="current-run")
                meter = candidate["meterIds"][0]
                candidate["usage"][meter]["quantity"] = quantity
                if region is not None:
                    candidate["usage"][meter]["region"] = region
                page["Items"][0]["armRegionName"] = "" if region == "" else "Global"
                higher = copy.deepcopy(page["Items"][0])
                higher.update(tierMinimumUnits=100, retailPrice=0.05)
                page["Items"].append(higher)
                with patch.object(deployment, "urlopen", side_effect=lambda *a, **k: io.StringIO(json.dumps(page))) as prices:
                    if expected:
                        result = instance.foundry_quote(candidate)
                        self.assertEqual(0.3, result["estimatedCost"])
                        query = parse_qs(deployment.urlparse(prices.call_args.args[0]).query)
                        self.assertEqual(f"armRegionName eq '{region}' and meterId eq '{meter}'",
                                         query["$filter"][0])
                    else:
                        with self.assertRaises(ValueError):
                            instance.foundry_quote(candidate)
                instance.execute.assert_not_called()

    @patch.object(deployment, "run", return_value="a" * 40)
    def test_foundry_plan_derives_candidate_from_saved_projection_then_runs_real_preflight(self, run):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.foundry_executor(directory)
            candidate, sources, providers, cloud, page = self.foundry_cloud(instance)
            with self.assertRaisesRegex(RuntimeError, "^foundry_preflight_refused$"):
                instance.foundry_plan()
            instance.execute.assert_not_called()
            projection = json.loads(deployment.zlib.decompress(deployment.base64.b64decode(instance.execute.return_value)))
            projection.update(template=candidate["template"], parameters=candidate["parameters"])
            instance.execute.return_value = deployment.base64.b64encode(
                deployment.zlib.compress(json.dumps(projection).encode())).decode()
            deployment.write_private(instance.directory / "foundry-estimate-inputs.json", json.dumps({
                key: candidate[key] for key in ("quotaRequests", "usage", "meterSkus")}))

            def assess(message):
                request = json.loads((instance.directory / "foundry-preflight-request.json").read_text())
                evidence = self.foundry_evidence(request)
                evidence["capacity"]["quotaRequestsSha256"] = deployment.canonical_sha256(request["quotaRequests"])
                deployment.write_private(instance.directory / "foundry-assessments.json",
                                         json.dumps({key: evidence[key] for key in ("capacity", "policy")}))

            with patch.object(deployment, "az", side_effect=cloud), \
                    patch.object(deployment, "urlopen", side_effect=lambda *a, **k: io.StringIO(json.dumps(page))), \
                    patch.object(deployment, "confirm", side_effect=assess):
                result = instance.foundry_plan()
            self.assertEqual(3, instance.execute.call_count)
            self.assertIn("export ARM_RESOURCE_PROVIDER_REGISTRATIONS=none", instance.execute.call_args_list[0].args[0])
            self.assertIn("-out=/var/lib/airlinedemo/foundry.tfplan", instance.execute.call_args_list[0].args[0])
            self.assertIn("scope='/subscriptions/", instance.execute.call_args_list[1].args[0])
            generated = json.loads((instance.directory / "foundry-candidate.json").read_text())
            self.assertEqual(candidate, generated)
            self.assertEqual(candidate["bindings"], result["bindings"])
            self.assertEqual(0o600, (instance.directory / "foundry-candidate.json").stat().st_mode & 0o777)
            self.assertFalse(any(" apply " in call.args[0] for call in instance.execute.call_args_list))

    def test_foundry_plan_uses_complete_fresh_settings_pair_or_stops(self):
        for scenario in ("saved", "fresh", "missing-backend", "missing-variables"):
            with self.subTest(scenario=scenario), tempfile.TemporaryDirectory() as directory:
                root = Path(directory).resolve()
                instance = self.foundry_executor(directory)
                candidate, _, _, _, _ = self.foundry_cloud(instance)
                deployment.write_private(instance.directory / "foundry-estimate-inputs.json", json.dumps({
                    key: candidate[key] for key in ("quotaRequests", "usage", "meterSkus")}))
                log = root / "terraform-calls"
                binary = root / "terraform"
                binary.write_text('#!/bin/sh\nprintf "%s|%s\\n" "$TFVARS_FILE" "$BACKEND_CONFIG_FILE" >> "$CALL_LOG"\n'
                                  'if [ "$2" = show ]; then printf \'{"values":{"root_module":{"resources":[]}}}\'; fi\n')
                binary.chmod(0o700)
                azure = root / "az"
                azure.write_text("#!/bin/sh\nexit 0\n")
                azure.chmod(0o700)
                (root / "scripts").symlink_to(ROOT / "scripts", target_is_directory=True)
                if scenario != "saved":
                    fresh = root / "fresh-settings"
                    fresh.mkdir()
                    if scenario != "missing-variables":
                        (fresh / "deploy.tfvars.json").write_text("{}")
                    if scenario != "missing-backend":
                        (fresh / "backend.hcl").write_text("")

                def remote(script):
                    subprocess.run(["bash", "-euo", "pipefail", "-c",
                                    script.replace("/var/lib/airlinedemo/", str(root) + "/")],
                                   cwd=root, check=True, capture_output=True,
                                   env={**os.environ, "PATH": str(root) + os.pathsep + os.environ["PATH"],
                                        "CALL_LOG": str(log)})
                    raise RuntimeError("stop after exercising production plan commands")

                instance.source = str(root)
                instance.sync = Mock()
                instance.remote = Mock(side_effect=remote)
                instance.execute = deployment.Deployment.execute.__get__(instance)
                with self.assertRaisesRegex(RuntimeError, "^foundry_preflight_refused$"):
                    instance.foundry_plan()
                if scenario.startswith("missing"):
                    self.assertFalse(log.exists())
                else:
                    expected = (f"{root}/operator-settings/deploy.tfvars.json|{root}/operator-settings/backend.hcl" if scenario == "saved" else
                                f"{root}/fresh-settings/deploy.tfvars.json|{root}/fresh-settings/backend.hcl")
                    self.assertEqual([expected, expected, expected], log.read_text().splitlines())

    def test_foundry_plan_executes_repeat_safe_exact_network_state_import(self):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.foundry_executor(directory)
            candidate, _, _, _, _ = self.foundry_cloud(instance)
            deployment.write_private(instance.directory / "foundry-estimate-inputs.json", json.dumps({
                key: candidate[key] for key in ("quotaRequests", "usage", "meterSkus")}))
            instance.execute.side_effect = RuntimeError("stop before cloud execution")
            with self.assertRaisesRegex(RuntimeError, "^foundry_preflight_refused$"):
                instance.foundry_plan()
            script = instance.execute.call_args.args[0].split("python3 - <<'PY'\n", 1)[1].split("\nPY\n", 1)[0]
            fixture = FoundryRecoveryTests()
            state = fixture.state()
            for scenario in ("existing", "managed", "foreign"):
                with self.subTest(scenario=scenario):
                    live = {"id": fixture.subnet_id, "networkSecurityGroup": {
                        "id": fixture.scope + "/providers/Microsoft.Network/networkSecurityGroups/tenant-nsg"}}
                    current = copy.deepcopy(state)
                    if scenario == "managed":
                        current["values"]["root_module"]["resources"].append({"address": fixture.association})
                    if scenario == "foreign":
                        live["networkSecurityGroup"]["id"] = live["networkSecurityGroup"]["id"].replace(
                            "/rg-demo/", "/rg-foreign/")
                    native = Mock(return_value=live)
                    importer = Mock()
                    with patch.object(runpy, "run_path", return_value={
                            "foundry_network_import": deployment.foundry_network_import, "az": native,
                            "import_foundry_network": importer}), \
                            patch.object(subprocess, "check_output", return_value=json.dumps(current).encode()), \
                            patch.object(subprocess, "run") as imported, \
                            patch.dict(os.environ, {"AZURE_SUBSCRIPTION_ID": fixture.scope.split("/")[2],
                                                    "RESOURCE_GROUP_NAME": "rg-demo", "TFVARS_FILE": "vars.json",
                                                    "AZURE_TENANT_ID": "tenant", "BACKEND_CONFIG_FILE": "backend.hcl"}):
                        if scenario == "foreign":
                            with self.assertRaises(ValueError):
                                exec(script, {})
                        else:
                            exec(script, {})
                        if scenario == "existing":
                            native.assert_called_once_with("network", "vnet", "subnet", "show",
                                                           "--ids", fixture.subnet_id, private=True)
                            importer.assert_called_once_with((fixture.association, fixture.subnet_id),
                                                             "backend.hcl", fixture.scope.split("/")[2], "tenant")
                            imported.assert_not_called()
                        else:
                            importer.assert_not_called()
                            imported.assert_not_called()
                            if scenario == "managed":
                                native.assert_not_called()

    def foundry_candidate(self):
        return {
            "bindings": {"sourceCommit": "a" * 40, **{
                name: "b" * 64 for name in ("configurationSha256", "planSha256", "assignmentSha256",
                                          "retainedResourceSha256", "templateSha256", "parametersSha256")}},
            "quotaRequests": {"model": {"unit": "Count", "requested": 2}},
            "meterIds": ["meter-1"], "meterSkus": {"meter-1": "Standard"},
        }

    def foundry_evidence(self, request):
        assessment = {"runId": request["runId"], "capturedAt": deployment.utc_now(),
                      "configurationSha256": request["bindings"]["configurationSha256"],
                      "sourceReference": "authorized-private-assessment", "sourceSha256": "c" * 64}
        return {
            "schemaVersion": 1, "runId": request["runId"], "capturedAt": deployment.utc_now(),
            "identity": copy.deepcopy(request["identity"]), "bindings": copy.deepcopy(request["bindings"]),
            "provider": {"status": "available", "sourceSha256": "d" * 64},
            "quota": [{"name": "model", "unit": "Count", "requested": 2, "available": 3}],
            "capacity": {**assessment, "status": "verified"},
            "policy": {**assessment, "status": "approved",
                       "assignmentSha256": request["bindings"]["assignmentSha256"]},
            "arm": {"validate": "Succeeded", "whatIf": "Succeeded",
                    "templateSha256": "b" * 64, "parametersSha256": "b" * 64, "changeSha256": "d" * 64},
            "quote": {"source": "https://prices.azure.com/api/retail/prices", "currency": "USD",
                      "items": [{"meterId": "meter-1", "sku": "Standard", "unit": "1 Hour",
                                 "unitPrice": 0.1, "quantity": 3, "estimatedCost": 0.3,
                                 "usageAssumption": "three additional instance hours"}], "estimatedCost": 0.3},
        }

    def foundry_executor(self, directory):
        instance = self.executor()
        instance.args.tenant_id = "11111111-1111-1111-1111-111111111111"
        instance.args.subscription_id = "00000000-0000-0000-0000-000000000000"
        instance.directory = Path(directory)
        instance.execute = Mock()
        return instance

    def foundry_agent_stage(self, directory):
        instance = self.executor()
        instance.args = type("Args", (), {
            "tenant_id": "11111111-1111-1111-1111-111111111111",
            "subscription_id": "00000000-0000-0000-0000-000000000000",
            "location": "swedencentral",
        })()
        instance.app_rg = "rg-demo1"
        instance.directory = Path(directory)
        instance.source = Path("/srv/airlinedemo")
        publisher_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo1/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-demo-foundry-publisher"
        setup = {
            "project_name": "airline-demo",
            "project_endpoint": "https://demo.services.ai.azure.com/api/projects/airline-demo",
            "publisher_identity_resource_id": publisher_id,
            "runtime_deployment": "runtime",
            "runtime_model_name": "gpt-demo",
            "runtime_model_version": "2026-01",
            "filter_policy": "airline-demo",
        }
        agents = [
            {"workload": workload, "name": name, "definitionSha256": digest * 64,
             "configurationId": configuration * 64, "modelDeployment": "runtime",
             "modelName": "gpt-demo", "modelVersion": "2026-01", "filterPolicy": "airline-demo"}
            for workload, name, digest, configuration in (
                ("copilot", "airline-copilot", "a", "1"),
                ("investigator", "airline-investigator", "b", "2"),
            )
        ]
        preview = {"projectName": "airline-demo", "projectEndpoint": setup["project_endpoint"], "agents": agents}
        manifest_agents = [
            {**agent, "version": str(index + 1)}
            for index, agent in enumerate(agents)
        ]
        manifest = {
            "schemaVersion": 1,
            "projectName": "airline-demo",
            "projectEndpoint": setup["project_endpoint"],
            "agents": manifest_agents,
        }
        instance.execute = Mock(side_effect=[
            json.dumps({"foundry_agent_setup": {"value": setup}}),
            "MANAGED_AGENT_PREVIEW:" + json.dumps(preview),
        ])
        instance.remote = Mock()
        return instance, setup, preview, manifest, publisher_id

    @patch.object(deployment, "run", return_value="a" * 40)
    def test_foundry_preflight_gathers_same_invocation_evidence(self, run):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.foundry_executor(directory)
            requests = []

            def acquire(request):
                requests.append(request)
                self.assertEqual(request, json.loads((instance.directory / "foundry-preflight-request.json").read_text()))
                return self.foundry_evidence(request)

            first = instance.foundry_preflight(self.foundry_candidate(), acquire)
            second = instance.foundry_preflight(self.foundry_candidate(), acquire)
            self.assertNotEqual(first["runId"], second["runId"])
            self.assertEqual(2, len(requests))
            receipt = instance.directory / "foundry-preflight.json"
            self.assertEqual(second, json.loads(receipt.read_text()))
            self.assertEqual(0o600, receipt.stat().st_mode & 0o777)
            self.assertEqual(0o600, (instance.directory / "foundry-preflight-request.json").stat().st_mode & 0o777)
            instance.execute.assert_not_called()
            self.assertEqual(2, run.call_count)

    @patch.object(deployment, "confirm")
    @patch.object(deployment, "run", return_value="a" * 40)
    def test_foundry_preflight_rejects_stale_or_mismatched_evidence(self, run, confirm):
        changes = [
            (("schemaVersion",), 2), (("runId",), "previous-run"),
            (("capturedAt",), "2000-01-01T00:00:00+00:00"),
            (("capturedAt",), "2999-01-01T00:00:00+00:00"),
            (("capturedAt",), "2026-01-01T00:00:00"),
            (("identity", "tenantId"), "another-tenant"),
            (("identity", "subscriptionId"), "another-subscription"),
            (("identity", "region"), "eastus"),
            (("bindings", "planSha256"), "e" * 64),
            (("bindings", "configurationSha256"), "e" * 64),
            (("bindings", "retainedResourceSha256"), "e" * 64),
            (("bindings", "assignmentSha256"), "e" * 64),
            (("provider", "status"), "unknown"), (("capacity", "status"), "unknown"),
            (("policy", "status"), "unavailable"), (("policy", "runId"), "old-assessment"),
            (("policy", "sourceReference"), ""), (("policy", "assignmentSha256"), "e" * 64),
            (("quota", 0, "available"), 1), (("quota", 0, "requested"), True),
            (("quote", "items", 0, "unitPrice"), float("nan")),
            (("quote", "items", 0, "quantity"), -1),
            (("quote", "items", 0, "estimatedCost"), 0.2),
            (("quote", "items", 0, "meterId"), "other"),
            (("quote", "items", 0, "sku"), "Premium"),
            (("quote", "source"), "unapproved-source"),
            (("quote", "currency"), "EUR"), (("quote", "items"), []),
            (("arm", "validate"), "Failed"), (("arm", "whatIf"), "Failed"),
        ]
        for path, value in changes:
            with self.subTest(path=path, value=value), tempfile.TemporaryDirectory() as directory:
                instance = self.foundry_executor(directory)
                receipt = instance.directory / "foundry-preflight.json"
                receipt.write_text("stale")

                def acquire(request):
                    evidence = self.foundry_evidence(request)
                    target = evidence
                    for part in path[:-1]:
                        target = target[part]
                    target[path[-1]] = value
                    return evidence

                with self.assertRaisesRegex(RuntimeError, "^foundry_preflight_refused$"):
                    instance.foundry_apply(self.foundry_candidate(), acquire)
                self.assertFalse(receipt.exists())
                instance.execute.assert_not_called()
        confirm.assert_not_called()

    @patch.object(deployment, "confirm")
    @patch.object(deployment, "run", return_value="a" * 40)
    def test_foundry_apply_refreshes_after_confirmation_and_rejects_price_drift(self, run, confirm):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.foundry_executor(directory)
            seen = []

            def acquire(request):
                seen.append(request["runId"])
                evidence = self.foundry_evidence(request)
                if len(seen) == 2:
                    confirm.assert_called_once()
                    evidence["quote"]["items"][0].update(unitPrice=0.2, estimatedCost=0.6)
                    evidence["quote"]["estimatedCost"] = 0.6
                return evidence

            with self.assertRaisesRegex(RuntimeError, "^deployment_prerequisite_failed$"):
                instance.foundry_apply(self.foundry_candidate(), acquire)
            self.assertEqual(2, len(set(seen)))
            instance.execute.assert_not_called()
            self.assertFalse((instance.directory / "foundry-preflight.json").exists())

    @patch.object(deployment, "confirm")
    @patch.object(deployment, "run", return_value="a" * 40)
    def test_foundry_apply_passes_only_the_rechecked_saved_plan(self, run, confirm):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.foundry_executor(directory)
            acquire = Mock(side_effect=self.foundry_evidence)
            instance.foundry_apply(self.foundry_candidate(), acquire)
            self.assertEqual(2, acquire.call_count)
            confirm.assert_called_once_with("Apply Foundry plan " + "b" * 64 +
                                           " with estimated additional cost USD 0.3 for the recorded usage assumptions?")
            instance.execute.assert_called_once()
            script = instance.execute.call_args.args[0]
            self.assertIn("cp /var/lib/airlinedemo/foundry.tfplan \"$plan\"", script)
            self.assertIn("'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' \"$plan\"", script)
            self.assertIn('terraform -chdir=infra/terraform apply -input=false "$plan"', script)

    @patch.object(deployment, "confirm")
    @patch.object(deployment, "run")
    @patch.object(deployment, "az")
    def test_foundry_agents_previews_confirms_publishes_and_removes_identity(self, az, run, confirm):
        with tempfile.TemporaryDirectory() as directory:
            instance, setup, preview, manifest, publisher_id = self.foundry_agent_stage(directory)
            events = []
            def execute(script):
                if "output -json" in script:
                    events.append("outputs")
                    return json.dumps({"foundry_agent_setup": {"value": setup}})
                events.append("preview")
                return "MANAGED_AGENT_PREVIEW:" + json.dumps(preview)

            instance.execute.side_effect = execute
            instance.remote.side_effect = lambda script: (
                events.append("publish") or "MANAGED_AGENT_MANIFEST:" + json.dumps(manifest)
            )
            az.side_effect = [
                {"clientId": "22222222-2222-2222-2222-222222222222"},
                {"identity": {"userAssignedIdentities": {}}},
            ]
            run.side_effect = lambda *args, **kwargs: events.append(
                "assign" if "assign" in args else "remove")
            confirm.side_effect = lambda message: events.append("confirm")

            instance.foundry_agents()

            self.assertEqual(["outputs", "preview", "confirm", "assign", "publish", "remove"], events)
            confirm.assert_called_once_with(
                "Publish the two immutable Foundry agents to airline-demo using definition preview " +
                deployment.canonical_sha256(preview) + "?")
            self.assertIn("--identities", run.call_args_list[0].args)
            self.assertIn(publisher_id, run.call_args_list[0].args)
            self.assertIn(publisher_id, run.call_args_list[1].args)
            publish_script = instance.remote.call_args.args[0]
            self.assertIn("AZURE_CONFIG_DIR=/var/lib/airlinedemo/azure", publish_script)
            self.assertIn("managed-agents publish /var/lib/airlinedemo/managed-agent-setup.json", publish_script)
            release = Path(directory) / "managed-agent-release.json"
            self.assertEqual(manifest, json.loads(release.read_text()))
            self.assertEqual(0o600, release.stat().st_mode & 0o777)

    @patch.object(deployment, "confirm")
    @patch.object(deployment, "run")
    @patch.object(deployment, "az")
    def test_foundry_agents_declined_preview_does_not_attach_publisher(self, az, run, confirm):
        with tempfile.TemporaryDirectory() as directory:
            instance, setup, preview, _, _ = self.foundry_agent_stage(directory)
            instance.execute.side_effect = [
                json.dumps({"foundry_agent_setup": {"value": setup}}),
                "MANAGED_AGENT_PREVIEW:" + json.dumps(preview),
            ]
            az.side_effect = [
                {"clientId": "22222222-2222-2222-2222-222222222222"},
                {"identity": {"userAssignedIdentities": {}}},
            ]
            confirm.side_effect = RuntimeError("Declined")

            with self.assertRaisesRegex(RuntimeError, "^Declined$"):
                instance.foundry_agents()

            run.assert_not_called()
            instance.remote.assert_not_called()
            self.assertFalse((Path(directory) / "managed-agent-release.json").exists())

    @patch.object(deployment, "confirm")
    @patch.object(deployment, "run")
    @patch.object(deployment, "az")
    def test_foundry_agents_removes_publisher_after_publish_failure(self, az, run, confirm):
        with tempfile.TemporaryDirectory() as directory:
            instance, setup, preview, _, _ = self.foundry_agent_stage(directory)
            instance.remote.side_effect = RuntimeError("publish outcome unavailable")
            az.side_effect = [
                {"clientId": "22222222-2222-2222-2222-222222222222"},
                {"identity": {"userAssignedIdentities": {}}},
            ]

            with self.assertRaisesRegex(RuntimeError, "^publish outcome unavailable$"):
                instance.foundry_agents()

            self.assertEqual(2, run.call_count)
            self.assertIn("assign", run.call_args_list[0].args)
            self.assertIn("remove", run.call_args_list[1].args)
            self.assertFalse((Path(directory) / "managed-agent-release.json").exists())

    def executor(self):
        instance = object.__new__(deployment.Deployment)
        instance.rg = "rg-demo1-tfstate"
        instance.vm = "vm-demo1-deploy"
        instance.args = type("Args", (), {"location": "swedencentral"})()
        return instance

    def source_repository(self, directory):
        root = Path(directory) / "repository"
        root.mkdir()
        for name in ("scripts/deploy-private.py", "infra/terraform/main.tf",
                     "backend/AirlineDemo.Deployment/Program.cs", "frontend/src/main.ts"):
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("committed source\n")
        for args in (("init", "--quiet"), ("add", "."),
                     ("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                      "-c", "core.hooksPath=/dev/null", "commit", "--quiet", "--no-gpg-sign", "-m", "Fixture")):
            subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True)
        return root

    def test_sync_refuses_dirty_tracked_deployment_input(self):
        for name in ("scripts/deploy-private.py", "infra/terraform/main.tf",
                     "backend/AirlineDemo.Deployment/Program.cs"):
            for staged in (False, True):
                with self.subTest(name=name, staged=staged), tempfile.TemporaryDirectory() as directory:
                    root = self.source_repository(directory)
                    (root / name).write_text("dirty source\n")
                    if staged:
                        subprocess.run(["git", "-C", str(root), "add", name], check=True)
                    instance = self.executor()
                    instance.remote = Mock()
                    with patch.object(deployment, "ROOT", root):
                        with self.assertRaisesRegex(RuntimeError, "^deployment_source_dirty$"):
                            instance.sync()
                    instance.remote.assert_not_called()

    def test_sync_refuses_unpublished_revision(self):
        for response in ("error", "mismatch"):
            with self.subTest(response=response), tempfile.TemporaryDirectory() as directory:
                root = self.source_repository(directory)
                instance = self.executor()
                instance.remote = Mock()
                original = deployment.run

                def commands(*args, **kwargs):
                    if args[:2] == ("gh", "repo"):
                        return json.dumps({"nameWithOwner": "wesback/airlinedemo"})
                    if args[:2] == ("gh", "api"):
                        if response == "error":
                            raise subprocess.CalledProcessError(1, args)
                        return json.dumps({"sha": "0" * 40})
                    return original(*args, **kwargs)

                with patch.object(deployment, "ROOT", root), patch.object(deployment, "run", side_effect=commands):
                    with self.assertRaisesRegex(RuntimeError, "^deployment_revision_unpublished$"):
                        instance.sync()
                instance.remote.assert_not_called()

    def test_sync_packages_exact_committed_revision(self):
        with tempfile.TemporaryDirectory() as directory:
            root = self.source_repository(directory)
            (root / "scripts/untracked.py").write_text("never deployed\n")
            instance = self.executor()
            instance.directory = Path(directory) / "settings"
            instance.directory.mkdir()
            (instance.directory / "deploy.tfvars.json").write_text('{"synthetic":true}')
            instance.args.prefix = "demo1"
            instance.remote = Mock()
            revision = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
            snapshot = subprocess.check_output(["git", "-C", str(root), "archive", "--format=tar", revision])
            digest = deployment.hashlib.sha256(snapshot).hexdigest()
            original = deployment.run

            def commands(*args, **kwargs):
                if args[:2] == ("gh", "repo"):
                    return json.dumps({"nameWithOwner": "wesback/airlinedemo"})
                if args[:2] == ("gh", "api"):
                    self.assertEqual(f"repos/wesback/airlinedemo/commits/{revision}", args[2])
                    return json.dumps({"sha": revision})
                return original(*args, **kwargs)

            with patch.object(deployment, "ROOT", root), patch.object(deployment, "run", side_effect=commands):
                self.assertEqual(revision, instance.sync())
            script = instance.remote.call_args.args[0]
            self.assertEqual(f"/var/lib/airlinedemo/source/{revision}-{digest[:12]}", instance.source)
            self.assertIn(f"checkout --quiet --detach {revision}", script)
            self.assertIn(f"= '{digest}'", script)
            self.assertIn("diff --quiet HEAD --", script)
            encoded = script.split("printf '%s' '")[1].split("'")[0]
            with tarfile.open(fileobj=io.BytesIO(deployment.base64.b64decode(encoded)), mode="r:gz") as package:
                self.assertEqual(["operator-settings/deploy.tfvars.json"], package.getnames())
                self.assertEqual(b'{"synthetic":true}', package.extractfile(package.getmembers()[0]).read())
            self.assertEqual("never deployed\n", (root / "scripts/untracked.py").read_text())

    @patch.object(deployment, "run")
    @patch.object(deployment, "az")
    def test_guest_failure_is_not_reported_as_arm_success(self, az, run):
        az.side_effect = [{}, {"instanceView": {
            "exitCode": 7, "executionState": "Failed", "output": "", "error": "failed command",
        }}]
        with self.assertRaisesRegex(RuntimeError, "state=Failed, exit=7"):
            self.executor().remote("exit 7")
        self.assertIn("delete", run.call_args.args)
        create = az.call_args_list[0].args
        self.assertIn("--protected-parameters", create)
        self.assertNotIn("--parameters", create)

    @patch.object(deployment, "run")
    @patch.object(deployment, "az")
    def test_missing_guest_status_fails_closed(self, az, run):
        az.side_effect = [{}, {}]
        with self.assertRaisesRegex(RuntimeError, "exit=None"):
            self.executor().remote("true")
        self.assertEqual(1, run.call_count)

    @patch.object(deployment, "run")
    @patch.object(deployment, "az")
    def test_success_requires_guest_exit_zero_and_succeeded(self, az, run):
        az.side_effect = [{}, {"instanceView": {
            "exitCode": 0, "executionState": "Succeeded", "output": "ready", "error": "",
        }}]
        self.assertEqual("ready", self.executor().remote("printf ready"))
        create_name = az.call_args_list[0].args[az.call_args_list[0].args.index("--name") + 1]
        self.assertIn(create_name, run.call_args.args)

    @patch.object(deployment, "run")
    @patch.object(deployment, "az")
    def test_transport_failure_cleans_run_command(self, az, run):
        az.side_effect = subprocess.CalledProcessError(1, ["az"])
        with self.assertRaises(subprocess.CalledProcessError):
            self.executor().remote("true")
        self.assertIn("delete", run.call_args.args)

    def test_settings_are_private_even_when_replacing_existing_file(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "settings.json"
            path.write_text("{}")
            path.chmod(0o644)
            deployment.write_private(path, json.dumps({"prefix": "demo1"}))
            self.assertEqual(0o600, path.stat().st_mode & 0o777)
            self.assertEqual({"prefix": "demo1"}, json.loads(path.read_text()))

    def test_foundry_cleanup_timer_install_is_idempotent(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result, unit_dir, log = self.run_cleanup_installer(root, active_timer=True)
            self.assertEqual(0, result.returncode, result.stderr)
            service = unit_dir / "airlinedemo-managed-cleanup.service"
            timer = unit_dir / "airlinedemo-managed-cleanup.timer"
            original = (service.read_text(), timer.read_text())

            repeated, _, _ = self.run_cleanup_installer(root, active_timer=True)

            self.assertEqual(0, repeated.returncode, repeated.stderr)
            self.assertEqual(original, (service.read_text(), timer.read_text()))
            calls = log.read_text().splitlines()
            self.assertEqual(2, sum(call == "enable --now airlinedemo-managed-cleanup.timer" for call in calls))
            self.assertEqual(2, sum(call == "daemon-reload" for call in calls))

    def test_foundry_cleanup_timer_catchup_uses_one_bounded_run(self):
        with tempfile.TemporaryDirectory() as directory:
            result, unit_dir, _ = self.run_cleanup_installer(Path(directory))
            self.assertEqual(0, result.returncode, result.stderr)
            service = (unit_dir / "airlinedemo-managed-cleanup.service").read_text()
            timer = (unit_dir / "airlinedemo-managed-cleanup.timer").read_text()

            self.assertEqual(
                "[Unit]\n"
                "Description=Hourly Airline Demo managed-conversation cleanup\n"
                "\n"
                "[Timer]\n"
                "OnCalendar=hourly\n"
                "Persistent=true\n"
                "Unit=airlinedemo-managed-cleanup.service\n"
                "\n"
                "[Install]\n"
                "WantedBy=timers.target\n",
                timer)
            self.assertEqual(
                "[Unit]\n"
                "Description=Airline Demo bounded managed-conversation cleanup\n"
                "After=network-online.target\n"
                "Wants=network-online.target\n"
                "\n"
                "[Service]\n"
                "Type=oneshot\n"
                "User=azureuser\n"
                "Group=azureuser\n"
                "Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1\n"
                f"ExecStart={Path(directory) / 'fake-bin/dotnet'} "
                f"{Path(directory) / 'AirlineDemo.Deployment.dll'} managed-cleanup "
                f"{Path(directory) / 'managed-cleanup.json'}\n"
                "TimeoutStartSec=11min\n"
                "NoNewPrivileges=true\n"
                "PrivateTmp=true\n"
                "ProtectSystem=strict\n"
                "ProtectHome=true\n"
                "ProtectKernelTunables=true\n"
                "ProtectKernelModules=true\n"
                "ProtectControlGroups=true\n",
                service)

    def test_foundry_cleanup_timer_refuses_missing_systemd_and_active_command(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            empty_path = root / "empty-path"
            empty_path.mkdir()
            assembly = root / "AirlineDemo.Deployment.dll"
            configuration = root / "managed-cleanup.json"
            assembly.touch()
            configuration.write_text("{}")
            missing = subprocess.run(
                ["/bin/bash", str(ROOT / "scripts/install-managed-cleanup.sh"),
                 str(assembly), str(configuration)],
                text=True, capture_output=True, env={"PATH": str(empty_path)})
            self.assertNotEqual(0, missing.returncode)
            self.assertIn("systemctl is required", missing.stderr)

            result, unit_dir, log = self.run_cleanup_installer(root, active_service=True)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("while a cleanup command is running", result.stderr)
            self.assertFalse((unit_dir / "airlinedemo-managed-cleanup.service").exists())
            self.assertNotIn("stop airlinedemo-managed-cleanup.timer", log.read_text())
            self.assertNotIn("enable --now airlinedemo-managed-cleanup.timer", log.read_text())

            raced, _, raced_log = self.run_cleanup_installer(
                root, active_timer=True, active_after_stop=True)
            self.assertNotEqual(0, raced.returncode)
            self.assertIn("while a cleanup command is running", raced.stderr)
            self.assertIn("stop airlinedemo-managed-cleanup.timer", raced_log.read_text())
            self.assertIn("start airlinedemo-managed-cleanup.timer", raced_log.read_text())
            self.assertNotIn("enable --now airlinedemo-managed-cleanup.timer", raced_log.read_text())

    def run_cleanup_installer(self, root, active_service=False, active_timer=False, active_after_stop=False):
        fake_bin = root / "fake-bin"
        fake_bin.mkdir(exist_ok=True)
        install_root = root / "system"
        log = root / "systemctl.log"
        assembly = root / "AirlineDemo.Deployment.dll"
        configuration = root / "managed-cleanup.json"
        assembly.touch(exist_ok=True)
        configuration.write_text("{}")
        commands = {
            "systemctl": """#!/bin/bash
set -euo pipefail
printf '%s\\n' "$*" >> "$CALL_LOG"
if [[ $1 == is-active ]]; then
  unit=
  for argument in "$@"; do unit=$argument; done
  if [[ $unit == airlinedemo-managed-cleanup.service &&
        ( $ACTIVE_SERVICE == true || ( $ACTIVE_AFTER_STOP == true && -f "$CALL_LOG.timer-stopped" ) ) ]]; then exit 0; fi
  if [[ $unit == airlinedemo-managed-cleanup.timer && $ACTIVE_TIMER == true ]]; then exit 0; fi
  exit 3
fi
if [[ $1 == stop ]]; then touch "$CALL_LOG.timer-stopped"; fi
""",
            "dotnet": "#!/bin/sh\nexit 0\n",
            "runuser": "#!/bin/sh\nexit 0\n",
            "install": """#!/bin/bash
set -euo pipefail
[[ $1 == -m && $# == 4 ]]
mode=$2
source=$3
destination="$INSTALL_ROOT$4"
mkdir -p "$(dirname "$destination")"
cp "$source" "$destination"
chmod "$mode" "$destination"
""",
        }
        for name, contents in commands.items():
            path = fake_bin / name
            path.write_text(contents)
            path.chmod(0o755)
        environment = dict(os.environ, PATH=f"{fake_bin}:{os.environ['PATH']}",
                           INSTALL_ROOT=str(install_root), CALL_LOG=str(log),
                           ACTIVE_SERVICE=str(active_service).lower(),
                           ACTIVE_TIMER=str(active_timer).lower(),
                           ACTIVE_AFTER_STOP=str(active_after_stop).lower())
        result = subprocess.run(
            ["/bin/bash", str(ROOT / "scripts/install-managed-cleanup.sh"),
             str(assembly), str(configuration)],
            text=True, capture_output=True, env=environment)
        return result, install_root / "etc/systemd/system", log

    @patch.object(deployment, "az")
    def test_wrong_subscription_is_rejected_before_resource_operations(self, az):
        az.return_value = {"id": "00000000-0000-0000-0000-000000000000",
                           "tenantId": "11111111-1111-1111-1111-111111111111"}
        args = type("Args", (), {
            "subscription_id": "22222222-2222-2222-2222-222222222222",
            "tenant_id": "11111111-1111-1111-1111-111111111111",
        })()
        with self.assertRaisesRegex(ValueError, "Azure id mismatch"):
            deployment.Deployment(args)
        self.assertEqual(1, az.call_count)

    def test_incomplete_sign_in_stops_all_before_platform_changes(self):
        instance = self.executor()
        with tempfile.TemporaryDirectory() as directory:
            instance.directory = Path(directory)
            instance.preflight = Mock()
            instance.bootstrap = Mock()
            instance.login = Mock(return_value=False)
            instance.platform = Mock()
            with self.assertRaisesRegex(RuntimeError, "Complete the displayed private-executor sign-in"):
                instance.all()
            instance.bootstrap.assert_called_once_with()
            instance.platform.assert_not_called()

    def test_destroy_validates_plan_and_private_state_before_prompt_or_endpoint_removal(self):
        instance = self.executor()
        instance.account = "sttest"
        instance.execute = Mock(side_effect=RuntimeError("invalid destroy plan"))
        with patch.object(deployment, "confirm") as confirm, patch.object(deployment, "az") as az:
            with self.assertRaisesRegex(RuntimeError, "invalid destroy plan"):
                instance.destroy()
            self.assertIn("--check-plan", instance.execute.call_args.args[0])
            self.assertIn("--auth-mode login", instance.execute.call_args.args[0])
            confirm.assert_not_called()
            az.assert_not_called()

    @patch.object(deployment, "run")
    def test_preflight_refuses_additional_release_reviewers(self, run):
        reviewer = {"type": "User", "reviewer": {"login": "test-owner"}}
        run.side_effect = [
            json.dumps({"protection_rules": [{"type": "required_reviewers", "reviewers": [reviewer, reviewer]}]}),
            "test-owner\n",
        ]
        with self.assertRaisesRegex(RuntimeError, "must require exactly"):
            self.executor().preflight()
        self.assertEqual(2, run.call_count)

    def test_login_reports_not_ready_until_authenticated(self):
        instance = self.executor()
        instance.args.tenant_id = "11111111-1111-1111-1111-111111111111"
        instance.args.subscription_id = "00000000-0000-0000-0000-000000000000"
        instance.remote = Mock(return_value="Complete interactive authentication.")
        self.assertFalse(instance.login())
        instance.remote.return_value = "\nAIRLINEDEMO_SIGN_IN_READY\n"
        self.assertTrue(instance.login())

    @patch.object(deployment, "confirm")
    @patch.object(deployment.subprocess, "run")
    @patch.object(deployment, "run")
    @patch.object(deployment, "az")
    def test_platform_resumes_after_vault_recovery_without_recovering_or_opening_it_again(self, az, run, process, confirm):
        instance = self.executor()
        instance.args.prefix = "demo1"
        instance.args.subscription_id = "00000000-0000-0000-0000-000000000000"
        instance.app_rg = "rg-demo1"
        instance.vault = "kv-demo1-test"
        instance.prepare = Mock()
        instance.execute = Mock()
        scope = f"/subscriptions/{instance.args.subscription_id}/resourceGroups/{instance.app_rg}/providers/Microsoft.KeyVault/vaults/{instance.vault}"
        assignments = [{"id": scope + "/providers/Microsoft.Authorization/roleAssignments/" + str(index),
                        "principalId": "operator", "scope": scope, "roleDefinitionName": role}
                       for index, role in enumerate(("Key Vault Certificates Officer", "Key Vault Secrets Officer"))]
        az.side_effect = lambda *args: [] if args[0] == "keyvault" else assignments
        process.return_value = subprocess.CompletedProcess([], 0, stdout=json.dumps({
            "properties": {"secretUriWithVersion": "https://demo.invalid/secrets/retained/" + "1" * 32}}))
        with tempfile.TemporaryDirectory() as directory:
            instance.directory = Path(directory)
            values = instance.directory / "fresh/demo1/deploy.tfvars.json"
            values.parent.mkdir(parents=True)
            values.write_text(json.dumps({"image_digest": "sha256:" + "0" * 64,
                                          "terraform_principal_object_id": "operator"}))
            (instance.directory / "vault-recovery.json").write_text("{}")
            instance.platform()
            adopted = json.loads(values.read_text())["adopt_key_vault"]
            self.assertEqual(assignments[0]["id"], adopted["certificates_role_assignment_id"])
            self.assertEqual("1" * 32, adopted["build_runner_token_version"])
        run.assert_called_once_with("az", "keyvault", "update", "-n", instance.vault,
                                    "--public-network-access", "Disabled", "-o", "none")

    @patch.object(deployment, "run")
    @patch.object(deployment, "az")
    def test_existing_bridge_is_verified_and_recorded_without_recreating_links(self, az, run):
        instance = self.executor()
        instance.args.prefix = "demo1"
        instance.args.subscription_id = "00000000-0000-0000-0000-000000000000"
        instance.app_rg, instance.account, instance.vault = "rg-demo1", "sttest", "kv-test"
        instance.execute = Mock()
        scope = "/subscriptions/" + instance.args.subscription_id + "/resourceGroups/"
        executor = scope + instance.rg + "/providers/Microsoft.Network/virtualNetworks/vnet-demo1-deploy"
        application = scope + instance.app_rg + "/providers/Microsoft.Network/virtualNetworks/vnet-demo"
        zone = {"name": "privatelink.vaultcore.azure.net",
                "id": scope + instance.app_rg + "/providers/Microsoft.Network/privateDnsZones/privatelink.vaultcore.azure.net"}

        def azure(*args):
            if args[:4] == ("network", "vnet", "peering", "list"):
                return [{"name": "demo1-application" if args[5] == instance.rg else "deployment-executor",
                         "remoteVirtualNetwork": {"id": application if args[5] == instance.rg else executor},
                         "allowVirtualNetworkAccess": True}]
            if args[:4] == ("network", "private-dns", "zone", "list"):
                return [zone]
            if args[:5] == ("network", "private-dns", "link", "vnet", "list"):
                return [{"name": "deployment-executor", "virtualNetwork": {"id": executor}}]
            if args[:3] == ("network", "private-endpoint", "show"):
                return {"networkInterfaces": [{"id": "synthetic-nic"}]}
            if args[:3] == ("network", "nic", "show"):
                return {"ipConfigurations": [{"privateIPAddress": "10.20.2.4"}]}
            if args[:6] == ("network", "private-dns", "record-set", "a", "list", "-g"):
                return [{"name": "stdemo1sttest", "aRecords": records}]
            raise AssertionError(args)

        az.side_effect = azure
        for records, creates in (
            ([], 1),
            ([{"ipv4Address": "10.20.2.4"}], 0),
            ([{"ipv4Address": "10.20.2.99"}], None),
            ([{"ipv4Address": "10.20.2.4"}, {"ipv4Address": "10.20.2.99"}], None),
        ):
            with self.subTest(records=records), tempfile.TemporaryDirectory() as directory:
                run.reset_mock()
                instance.execute.reset_mock()
                instance.directory = Path(directory)
                if creates is None:
                    with self.assertRaisesRegex(RuntimeError, "Existing Blob DNS record has unexpected addresses"):
                        instance.connect()
                    run.assert_not_called()
                    instance.execute.assert_not_called()
                    self.assertFalse((instance.directory / "bridge.json").exists())
                    continue
                instance.connect()
                bridge = json.loads((instance.directory / "bridge.json").read_text())
                self.assertEqual(3, len(bridge["resources"]))
                self.assertEqual(zone["id"] + "/virtualNetworkLinks/deployment-executor",
                                 bridge["resources"]['azurerm_private_dns_zone_virtual_network_link.executor["kv"]'])
                self.assertEqual(creates, run.call_count)
                if creates:
                    self.assertEqual(("az", "network", "private-dns", "record-set", "a", "add-record"), run.call_args.args[:6])

    @patch.object(deployment, "run")
    @patch.object(deployment, "az")
    def test_bridge_does_not_overwrite_peering_to_another_network(self, az, run):
        instance = self.executor()
        instance.args.prefix, instance.args.subscription_id = "demo1", "synthetic-subscription"
        instance.app_rg = "rg-demo1"
        az.return_value = [{"name": "demo1-application", "remoteVirtualNetwork": {"id": "/unrelated"},
                            "allowVirtualNetworkAccess": True}]
        with self.assertRaisesRegex(RuntimeError, "Existing peering demo1-application has unexpected connectivity"):
            instance.connect()
        run.assert_not_called()

    @patch.object(deployment, "az")
    def test_partial_destroy_adopts_remaining_gateway_link_without_recreating_network(self, az):
        instance = self.executor()
        instance.app_rg = "rg-demo1"
        instance.connect = Mock()
        instance.execute = Mock()
        executor = "/synthetic/executor"
        link = {"name": "deployment-executor", "id": "/synthetic/gateway/link",
                "virtualNetwork": {"id": executor}}
        for zone, network, error in (
            ("azure-api.net", executor, None),
            ("azure-api.net", "/unrelated", "unexpected VNet"),
            ("unrecorded.zone", executor, "no recorded Terraform ownership"),
        ):
            with self.subTest(zone=zone, network=network), tempfile.TemporaryDirectory() as directory:
                instance.directory = Path(directory)
                bridge_path = instance.directory / "bridge.json"
                bridge_path.write_text(json.dumps({
                    "executor": {"vnet_id": executor, "blob_dns_zone_id": "/synthetic/blob"},
                    "resources": {"deleted-link": "/deleted"},
                }))
                link["virtualNetwork"]["id"] = network
                az.side_effect = [[], [{"id": link["id"]}], [{"name": zone}], [link]]
                instance.execute.reset_mock()
                if error:
                    with self.assertRaisesRegex(RuntimeError, error):
                        instance.plan_destroy()
                    instance.execute.assert_not_called()
                else:
                    instance.plan_destroy()
                    self.assertEqual({
                        "azurerm_private_dns_zone_virtual_network_link.gateway_executor[0]": link["id"],
                    }, json.loads(bridge_path.read_text())["resources"])
                    instance.execute.assert_called_once()
                instance.connect.assert_not_called()


if __name__ == "__main__":
    unittest.main()
