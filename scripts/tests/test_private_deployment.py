import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("private_deployment", ROOT / "scripts/deploy-private.py")
deployment = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deployment)


class PrivateDeploymentTests(unittest.TestCase):
    def executor(self):
        instance = object.__new__(deployment.Deployment)
        instance.rg = "rg-demo1-tfstate"
        instance.vm = "vm-demo1-deploy"
        instance.args = type("Args", (), {"location": "swedencentral"})()
        return instance

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


if __name__ == "__main__":
    unittest.main()
