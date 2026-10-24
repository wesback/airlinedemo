import hashlib
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DEPLOY = ROOT / "scripts/deploy.sh"
TENANT = "11111111-1111-1111-1111-111111111111"
SUBSCRIPTION = "00000000-0000-0000-0000-000000000000"
PRINCIPAL = "33333333-3333-3333-3333-333333333333"
GROUP = "22222222-2222-2222-2222-222222222222"

AZ = """#!/bin/sh
echo "az $*" >> "$CALL_LOG"
case "$*" in
  "account show --query tenantId"*) echo "$FAKE_TENANT" ;;
  "account show --query id"*) echo "$FAKE_SUBSCRIPTION" ;;
  "account show") exit "${AZ_ACCOUNT_FAIL:-0}" ;;
  "ad signed-in-user show --query id"*) echo "$FAKE_PRINCIPAL" ;;
  "ad signed-in-user show --query displayName"*) echo "Demo Operator" ;;
  "ad signed-in-user show --query mail"*) echo "operator@example.invalid" ;;
  "ad group show --group"*)
    [ "${FAKE_GROUP_MISSING:-0}" = "1" ] && exit 1
    case " $* " in
      *" displayName "*|*" displayName"*) echo "grp-ops" ;;
      *) echo "${FAKE_GROUP_ID:-22222222-2222-2222-2222-222222222222}" ;;
    esac ;;
  "storage account show"*) echo "/subscriptions/x/resourceGroups/rg-demo1-tfstate/providers/Microsoft.Storage/storageAccounts/acct" ;;
  "storage container create"*) exit "${AZ_CONTAINER_FAIL:-0}" ;;
  "keyvault show-deleted"*) [ -z "$FAKE_DELETED_VAULT" ] || echo "$FAKE_DELETED_VAULT" ;;
  "role assignment create"*)
    prev=; scope=; role=
    for a in "$@"; do [ "$prev" = "--scope" ] && scope=$a; [ "$prev" = "--role" ] && role=$a; prev=$a; done
    case "$scope:$role" in
      */vaults/*:*Certificates*) echo "$scope/providers/Microsoft.Authorization/roleAssignments/55555555-5555-5555-5555-555555555555" ;;
      */vaults/*:*Secrets*) echo "$scope/providers/Microsoft.Authorization/roleAssignments/66666666-6666-6666-6666-666666666666" ;;
    esac ;;
  "resource show"*"release-image-signing-certificate"*) echo "https://kv.vault.azure.net/secrets/release-image-signing-certificate/0123456789abcdef0123456789abcdef" ;;
esac
exit 0
"""
TERRAFORM = """#!/bin/sh
echo "terraform $*" >> "$CALL_LOG"
exit 0
"""


class DeployScriptTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        base = Path(self.temp.name)
        self.bin = base / "bin"
        self.bin.mkdir()
        self.state = base / "state"
        self.log = base / "calls.log"
        for name, body in (("az", AZ), ("terraform", TERRAFORM)):
            path = self.bin / name
            path.write_text(body)
            path.chmod(0o755)
        self.env = os.environ.copy()
        self.env.update({
            "PATH": f"{self.bin}:{self.env['PATH']}", "CALL_LOG": str(self.log),
            "DEPLOY_STATE_DIR": str(self.state), "DEPLOY_ROLE_WAIT_SECONDS": "0",
            "FAKE_TENANT": TENANT, "FAKE_SUBSCRIPTION": SUBSCRIPTION, "FAKE_PRINCIPAL": PRINCIPAL,
        })
        self.args = [
            "--prefix", "demo1", "--location", "swedencentral", "--model-name", "gpt-demo-model",
            "--model-version", "2025-01-01",
            "--budget-amount", "250", "--image-digest", "sha256:" + "a" * 64,
            "--passcode-secret-uri", "https://kv-demo.vault.azure.net/secrets/demo-passcode",
        ]

    def tearDown(self):
        self.temp.cleanup()

    def run_deploy(self, *extra, args=None, stdin="no\n", env=None):
        return subprocess.run([str(DEPLOY), *(self.args if args is None else args), *extra], cwd=ROOT,
                              env=env or self.env, input=stdin, text=True, capture_output=True)

    def calls(self):
        return self.log.read_text().splitlines() if self.log.exists() else []

    def test_help_lists_every_required_option(self):
        result = subprocess.run([str(DEPLOY), "--help"], cwd=ROOT, text=True, capture_output=True)
        self.assertEqual(0, result.returncode)
        for option in ("--prefix", "--location", "--model-name", "--model-version", "--ops-group-name",
                       "--ops-group-id", "--budget-amount", "--image-digest", "--passcode-secret-uri"):
            self.assertIn(option, result.stdout)

    def test_default_identity_uses_signed_in_user(self):
        result = self.run_deploy("--prepare-only")
        self.assertEqual(0, result.returncode, result.stderr)
        calls = self.calls()
        self.assertFalse([c for c in calls if "az ad group" in c])
        values = json.loads((self.state / "demo1/deploy.tfvars.json").read_text())
        self.assertEqual("User", values["telemetry_reader_principal_type"])
        self.assertEqual(PRINCIPAL, values["sql_admin_group_object_id"])
        self.assertEqual("Demo Operator", values["sql_admin_group_name"])

    def test_existing_ops_group_is_found_by_name_without_modification(self):
        result = self.run_deploy("--prepare-only", "--ops-group-name", "grp-ops")
        self.assertEqual(0, result.returncode, result.stderr)
        calls = self.calls()
        self.assertTrue(any(c.startswith("az ad group show --group grp-ops") for c in calls))
        self.assertFalse([c for c in calls if "az ad group create" in c or "az ad group member add" in c])
        values = json.loads((self.state / "demo1/deploy.tfvars.json").read_text())
        self.assertEqual("Group", values["telemetry_reader_principal_type"])
        self.assertEqual(GROUP, values["sql_admin_group_object_id"])

    def test_existing_ops_group_id_is_resolved_without_modification(self):
        result = self.run_deploy("--prepare-only", "--ops-group-id", GROUP)
        self.assertEqual(0, result.returncode, result.stderr)
        calls = self.calls()
        self.assertTrue(any(c.startswith("az ad group show --group " + GROUP) for c in calls))
        self.assertFalse([c for c in calls if "az ad group create" in c or "az ad group member add" in c])

    def test_group_name_and_id_must_match(self):
        result = self.run_deploy("--prepare-only", "--ops-group-name", "other-group", "--ops-group-id", GROUP)
        self.assertEqual(2, result.returncode)
        self.assertIn("does not match object ID", result.stderr)

    def test_missing_ops_group_name_is_rejected_without_modification(self):
        env = dict(self.env, FAKE_GROUP_MISSING="1")
        result = self.run_deploy("--prepare-only", "--ops-group-name", "grp-ops", env=env)
        self.assertEqual(2, result.returncode)
        self.assertIn("could not find existing Entra group 'grp-ops'", result.stderr)
        self.assertFalse([c for c in self.calls() if "az ad group create" in c or "az ad group member add" in c])

    def test_prepare_only_writes_discovered_values_and_changes_nothing_in_azure(self):
        result = self.run_deploy("--prepare-only", "--presenter-cidrs", "203.0.113.0/24,198.51.100.7/32")
        self.assertEqual(0, result.returncode, result.stderr)
        values = json.loads((self.state / "demo1/deploy.tfvars.json").read_text())
        self.assertEqual(
            (SUBSCRIPTION, TENANT, PRINCIPAL, "demo1", PRINCIPAL, ["operator@example.invalid"],
             ["203.0.113.0/24", "198.51.100.7/32"], 250,
             {"name": "Demo Operator", "email": "operator@example.invalid"}),
            (values["subscription_id"], values["tenant_id"], values["terraform_principal_object_id"],
             values["name_prefix"], values["telemetry_reader_group_id"], values["budget_contact_emails"],
             values["presenter_reviewer_cidrs"], values["budget_amount"], values["security_response_owner"]))
        backend = (self.state / "demo1/backend.hcl").read_text()
        self.assertIn('resource_group_name  = "rg-demo1-tfstate"', backend)
        self.assertIn("use_azuread_auth     = true", backend)
        self.assertEqual(0o600, (self.state / "demo1/deploy.tfvars.json").stat().st_mode & 0o777)
        self.assertFalse([c for c in self.calls() if c.startswith(("az group", "az storage", "az role", "terraform"))])

    def test_full_run_creates_backend_then_stops_at_the_plan_confirmation(self):
        result = self.run_deploy("--register-providers", stdin="no\n")
        self.assertEqual(4, result.returncode, result.stderr)
        calls = self.calls()
        order = [next(i for i, c in enumerate(calls) if c.startswith(prefix)) for prefix in (
            "az group create", "az storage account create --name", "az role assignment create",
            "az storage container create", "terraform -chdir=infra/terraform init", "terraform -chdir=infra/terraform plan")]
        self.assertEqual(sorted(order), order)
        account_create = next(c for c in calls if c.startswith("az storage account create"))
        self.assertIn("--allow-shared-key-access false --allow-blob-public-access false", account_create)
        self.assertFalse([c for c in calls if c.startswith("terraform -chdir=infra/terraform apply")])
        self.assertIn("Terraform apply declined", result.stderr)

    def test_existing_backend_is_not_created(self):
        result = self.run_deploy("--existing-backend", "stownerstate", "--register-providers")
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertFalse([c for c in self.calls() if c.startswith(("az group create", "az storage", "az role"))])
        self.assertIn('storage_account_name = "stownerstate"', (self.state / "demo1/backend.hcl").read_text())

    def test_missing_required_options_are_all_named_at_once(self):
        result = self.run_deploy(args=["--prefix", "demo1"])
        self.assertEqual(2, result.returncode)
        self.assertIn("missing required options: --location --model-name --model-version --budget-amount\n", result.stderr)

    def test_invalid_values_are_rejected_before_anything_is_written(self):
        cases = [
            ("--prefix", "Demo_1", "--prefix must be 3-10 lowercase letters or digits starting with a letter"),
            ("--image-digest", "latest", "--image-digest must be sha256: followed by 64 lowercase hex characters"),
            ("--budget-amount", "12.5", "--budget-amount must be a whole number"),
            ("--model-name", 'gpt"; rm', "--model-name contains characters that are not allowed"),
        ]
        for option, value, message in cases:
            with self.subTest(option=option):
                args = list(self.args)
                args[args.index(option) + 1] = value
                result = self.run_deploy(args=args)
                self.assertEqual(2, result.returncode)
                self.assertIn(message, result.stderr)
        self.assertFalse(self.state.exists())

    def test_platform_phase_writes_null_application_values(self):
        args = self.args[:self.args.index("--image-digest")]
        result = self.run_deploy("--prepare-only", args=args)
        self.assertEqual(0, result.returncode, result.stderr)
        values = json.loads((self.state / "demo1/deploy.tfvars.json").read_text())
        self.assertEqual((None, None), (values["image_digest"], values["demo_passcode_secret_uri"]))

    def test_digest_without_passcode_uri_is_rejected(self):
        args = self.args[:self.args.index("--passcode-secret-uri")]
        result = self.run_deploy("--prepare-only", args=args)
        self.assertEqual(2, result.returncode)
        self.assertIn("give --image-digest and --passcode-secret-uri together", result.stderr)
        self.assertFalse(self.state.exists())

    def test_soft_deleted_vault_is_recovered_and_adopted_before_planning(self):
        suffix = hashlib.sha1(f"{SUBSCRIPTION}/demo1".encode()).hexdigest()[:6]
        vault = f"kv-demo1-{suffix}"
        vault_id = f"/subscriptions/{SUBSCRIPTION}/resourceGroups/rg-demo1/providers/Microsoft.KeyVault/vaults/{vault}"
        result = self.run_deploy("--register-providers", env=dict(self.env, FAKE_DELETED_VAULT=vault))
        self.assertEqual(4, result.returncode, result.stderr)
        calls = self.calls()
        order = [next(i for i, c in enumerate(calls) if c.startswith(prefix)) for prefix in (
            "az group create --name rg-demo1 ", f"az keyvault recover --name {vault}",
            f"az role assignment create --assignee-object-id {PRINCIPAL} --assignee-principal-type User --role Key Vault Certificates Officer --scope {vault_id}",
            "terraform -chdir=infra/terraform plan")]
        self.assertEqual(sorted(order), order)
        adopt = json.loads((self.state / "demo1/deploy.tfvars.json").read_text())["adopt_key_vault"]
        self.assertEqual({
            "certificates_role_assignment_id": f"{vault_id}/providers/Microsoft.Authorization/roleAssignments/55555555-5555-5555-5555-555555555555",
            "secrets_role_assignment_id": f"{vault_id}/providers/Microsoft.Authorization/roleAssignments/66666666-6666-6666-6666-666666666666",
            "signing_certificate_version": "0123456789abcdef0123456789abcdef",
            "build_runner_token_version": None,
        }, adopt)

    def test_live_vault_without_soft_delete_is_not_recovered(self):
        result = self.run_deploy("--register-providers")
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertFalse([c for c in self.calls() if c.startswith(("az keyvault recover", "az group create --name rg-demo1 "))])
        self.assertIsNone(json.loads((self.state / "demo1/deploy.tfvars.json").read_text())["adopt_key_vault"])

    def test_invalid_cidr_is_rejected(self):
        result = self.run_deploy("--prepare-only", "--presenter-cidrs", "203.0.113.0/24,evil")
        self.assertEqual(2, result.returncode)
        self.assertIn("invalid list entry: evil", result.stderr)

    def test_signed_out_azure_cli_stops_the_run(self):
        env = dict(self.env, AZ_ACCOUNT_FAIL="1")
        result = self.run_deploy("--prepare-only", env=env)
        self.assertEqual(3, result.returncode)
        self.assertIn("Azure CLI is not signed in", result.stderr)

    def test_container_creation_failure_is_reported_without_planning(self):
        env = dict(self.env, AZ_CONTAINER_FAIL="1")
        result = self.run_deploy(env=env)
        self.assertEqual(3, result.returncode)
        self.assertIn("the state container could not be created", result.stderr)
        self.assertFalse([c for c in self.calls() if c.startswith("terraform")])


if __name__ == "__main__":
    unittest.main()
