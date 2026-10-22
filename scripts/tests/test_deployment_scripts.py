import os
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BOOTSTRAP = ROOT / "scripts/bootstrap-deployment.sh"
DESTROY = ROOT / "scripts/destroy-deployment.sh"


class LifecycleScriptTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.bin = Path(self.temp.name)
        self.log = self.bin / "calls.log"
        for name in ("terraform", "az"):
            path = self.bin / name
            path.write_text("#!/bin/sh\necho \"$0|$*\" >> \"$CALL_LOG\"\n"
                             + (self._az_body() if name == "az" else self._terraform_body()))
            path.chmod(0o755)
        self.env = os.environ.copy()
        self.env.update({
            "PATH": f"{self.bin}:{self.env['PATH']}", "CALL_LOG": str(self.log),
            "AZURE_TENANT_ID": "11111111-1111-1111-1111-111111111111",
            "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000000",
            "RESOURCE_GROUP_NAME": "rg-airline-demo", "TFVARS_FILE": "/tmp/readiness.tfvars",
            "BACKEND_CONFIG_FILE": "/tmp/backend.hcl", "AZ_FAIL": "0", "TF_FAIL": "0",
        })

    def tearDown(self):
        self.temp.cleanup()

    def _az_body(self):
        return ('if [ "${AZ_FAIL:-0}" = "1" ]; then exit 1; fi\n'
                'if echo "$*" | grep -q "tenantId"; then printf "11111111-1111-1111-1111-111111111111\\n"; else printf "00000000-0000-0000-0000-000000000000\\n"; fi\n')

    def _terraform_body(self):
        return ('if [ "${TF_FAIL:-0}" = "1" ] && echo "$*" | grep -q " destroy "; then exit 1; fi\n')

    def calls(self):
        if not self.log.exists():
            return []
        return self.log.read_text().splitlines()

    def run_script(self, script, args=(), env=None, input_text=None):
        run_env = dict(self.env if env is None else env)
        return subprocess.run([str(script), *args], cwd=ROOT, env=run_env,
                              input=input_text, text=True, capture_output=True)

    def test_bootstrap_preflight_and_apply_sequence(self):
        result = self.run_script(BOOTSTRAP, input_text="yes\n")
        self.assertEqual(0, result.returncode, result.stderr)
        calls = self.calls()
        init = next(c for c in calls if "init -backend-config=/tmp/backend.hcl" in c)
        plan = next(c for c in calls if " plan -var-file=/tmp/readiness.tfvars -out=" in c)
        show = next(c for c in calls if " show /tmp/" in c and c.endswith(".tfplan"))
        apply = next(c for c in calls if " apply /tmp/" in c)
        self.assertLess(calls.index(init), calls.index(plan))
        self.assertLess(calls.index(plan), calls.index(show))
        self.assertLess(calls.index(show), calls.index(apply))
        plan_path = plan.split("-out=", 1)[1]
        self.assertTrue(show.endswith(f" show {plan_path}"))
        self.assertTrue(apply.endswith(f" apply {plan_path}"))
        self.assertFalse(any("docker" in c.lower() or "containerapp update" in c.lower() for c in calls))

        for name in ("AZURE_SUBSCRIPTION_ID", "AZURE_TENANT_ID", "RESOURCE_GROUP_NAME",
                     "TFVARS_FILE", "BACKEND_CONFIG_FILE"):
            self.log.unlink(missing_ok=True)
            env = self.env.copy()
            env.pop(name)
            missing = self.run_script(BOOTSTRAP, env=env, input_text="yes\n")
            self.assertEqual(2, missing.returncode)
            self.assertEqual(f"Missing required deployment configuration: {name}\n", missing.stderr)
            self.assertEqual([], self.calls())

        for mismatch, message in (("AZURE_TENANT_ID", "tenant mismatch"),
                                  ("AZURE_SUBSCRIPTION_ID", "subscription mismatch")):
            self.log.unlink(missing_ok=True)
            env = self.env.copy(); env[mismatch] = "different"
            mismatch_result = self.run_script(BOOTSTRAP, env=env, input_text="yes\n")
            self.assertNotEqual(0, mismatch_result.returncode)
            self.assertIn(message, mismatch_result.stderr)
            self.assertFalse(any(" apply " in c for c in self.calls()))

        self.log.unlink(missing_ok=True)
        declined = self.run_script(BOOTSTRAP, input_text="no\n")
        self.assertNotEqual(0, declined.returncode)
        self.assertFalse(any(" apply " in c for c in self.calls()))

    def test_destroy_confirmation_authentication_and_success_output(self):
        success = self.run_script(DESTROY, args=("--yes",))
        self.assertEqual(0, success.returncode, success.stderr)
        destroy_calls = [c for c in self.calls() if " destroy " in c]
        self.assertEqual(1, len(destroy_calls))
        self.assertTrue(destroy_calls[0].endswith(" destroy -var-file=/tmp/readiness.tfvars -var=resource_group_name=rg-airline-demo -auto-approve"))
        self.assertIn("Destroyed deployment resources for rg-airline-demo.", success.stdout)

        self.log.unlink(missing_ok=True)
        absent_yes = self.run_script(DESTROY)
        self.assertNotEqual(0, absent_yes.returncode)
        self.assertFalse(any("Destroyed deployment resources" in absent_yes.stdout for _ in (0,)))
        self.assertEqual([], [c for c in self.calls() if " destroy " in c])

        env = self.env.copy(); env.pop("RESOURCE_GROUP_NAME")
        missing_rg = self.run_script(DESTROY, args=("--yes",), env=env)
        self.assertEqual(2, missing_rg.returncode)
        self.assertEqual([], [c for c in self.calls() if " destroy " in c])

        env = self.env.copy(); env["AZ_FAIL"] = "1"
        auth_fail = self.run_script(DESTROY, args=("--yes",), env=env)
        self.assertNotEqual(0, auth_fail.returncode)
        self.assertFalse(any(" destroy " in c for c in self.calls()))

        env = self.env.copy(); env["TF_FAIL"] = "1"
        failed_destroy = self.run_script(DESTROY, args=("--yes",), env=env)
        self.assertNotEqual(0, failed_destroy.returncode)
        self.assertNotIn("Destroyed deployment resources", failed_destroy.stdout)


if __name__ == "__main__":
    unittest.main()
