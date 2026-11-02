import contextlib
import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch
from urllib.parse import urlparse

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("verify_deployment", ROOT / "scripts/verify-deployment.py")
verification = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verification)


class Response(io.BytesIO):
    def __init__(self, value, status=200):
        super().__init__(value if isinstance(value, bytes) else json.dumps(value).encode())
        self.status = status
        self.headers = {"Content-Type": "text/html" if isinstance(value, bytes) else "application/json"}


class HttpEdge:
    def __init__(self, model_status=200):
        self.user = None
        self.model_status = model_status
        self.model_calls = 0

    def open(self, request, timeout):
        path = urlparse(request.full_url).path
        if path == "/api/health":
            return Response({"status": "ok"})
        if path == "/":
            return Response(b"<html>synthetic client</html>")
        if path == "/api/auth/sign-in":
            self.user = json.loads(request.data)["userId"]
            role = {"presenter": "presenter", "reviewer": "technical-reviewer", "fin-1": "finance-reviewer",
                    "legal-1": "legal-reviewer", "exec-1": "executive"}[self.user]
            return Response({"id": self.user, "role": role, "csrfToken": "synthetic-csrf"})
        if not self.user:
            return Response({"error": {"code": "unauthenticated"}} if path == "/api/v1/cases"
                            else {"error": "Not signed in."}, 401)
        if path == "/api/v1/cases":
            return Response({"cases": [{"caseId": "cp-case-" + name} for name in "abc"]})
        if path.startswith("/api/cases/"):
            return Response({} if path.endswith("case-001") else {"caseId": path.rsplit("/", 1)[-1], "revision": 1},
                            404 if path.endswith("case-001") else 200)
        if path == "/api/v1/copilot/questions":
            if not request.get_header("X-csrf-token"):
                return Response({"error": {"code": "anti_forgery_failed"}}, 403)
            self.model_calls += 1
            return Response({"answer": "Synthetic return summary."}, self.model_status)
        return Response({})


class LiveVerificationTests(unittest.TestCase):
    def test_verifier_exercises_all_personas_and_one_authenticated_model_request(self):
        edges = []

        def opener(*handlers):
            edge = HttpEdge()
            edges.append(edge)
            return edge

        with patch.object(verification.urllib.request, "build_opener", side_effect=opener), contextlib.redirect_stdout(io.StringIO()):
            snapshots = verification.verify("https://demo.invalid", "synthetic-value", model=True)
        self.assertEqual(["executive", "finance-reviewer", "legal-reviewer", "presenter", "technical-reviewer"], sorted(snapshots))
        self.assertEqual(1, sum(edge.model_calls for edge in edges))

    def test_model_dependency_failure_cannot_produce_a_successful_verification(self):
        with patch.object(verification.urllib.request, "build_opener", side_effect=lambda *args: HttpEdge(503)):
            with self.assertRaisesRegex(RuntimeError, "/api/v1/copilot/questions: expected HTTP 200, received 503"):
                verification.verify("https://demo.invalid", "synthetic-value", model=True)


if __name__ == "__main__":
    unittest.main()
