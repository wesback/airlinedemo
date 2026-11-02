#!/usr/bin/env python3
"""Live checks using only committed synthetic cases. Never print credentials or session material."""
import argparse
import hashlib
import http.cookiejar
import json
from pathlib import Path
import subprocess
import urllib.error
import urllib.request


def verify(origin, passcode, model):
    def request(client, path, body=None, headers=None, expected=200):
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(origin + path, data=data,
                                         headers={"Content-Type": "application/json", **(headers or {})})
        try:
            response = client.open(request, timeout=90)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            if response.status != expected:
                # Do not echo response bodies: they may contain session/model content.
                raise RuntimeError(f"{path}: expected HTTP {expected}, received {response.status}")
            raw = response.read()
            return json.loads(raw) if raw and "json" in response.headers.get("Content-Type", "") else raw

    anonymous = urllib.request.build_opener()
    if request(anonymous, "/api/health") != {"status": "ok"}:
        raise RuntimeError("Unexpected health contract.")
    for path in ("/api/auth/users", "/api/cases/cp-case-a"):
        if request(anonymous, path, expected=401) != {"error": "Not signed in."}:
            raise RuntimeError("Unexpected unauthenticated response.")
    if request(anonymous, "/api/v1/cases", expected=401)["error"]["code"] != "unauthenticated":
        raise RuntimeError("Unexpected unauthenticated case-list response.")
    root = request(anonymous, "/")
    if not isinstance(root, bytes) or b"<html" not in root.lower():
        raise RuntimeError("Client document is not served.")

    snapshots = {}
    users = [("presenter", "presenter"), ("reviewer", "technical-reviewer"),
             ("fin-1", "finance-reviewer"), ("legal-1", "legal-reviewer"), ("exec-1", "executive")]
    for user, role in users:
        client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        session = request(client, "/api/auth/sign-in", {"userId": user, "passcode": passcode})
        if session["id"] != user or session["role"] != role:
            raise RuntimeError("Unexpected authenticated persona.")
        visible = request(client, "/api/v1/cases")
        if sorted(case["caseId"] for case in visible["cases"]) != ["cp-case-a", "cp-case-b", "cp-case-c"]:
            raise RuntimeError("The persona cannot see the three synthetic case-pack aircraft.")
        cases = {}
        for case in ("cp-case-a", "cp-case-b", "cp-case-c"):
            state = request(client, "/api/cases/" + case)
            if state["caseId"] != case or not isinstance(state["revision"], int):
                raise RuntimeError("Case identity/revision contract failed.")
            cases[case] = state
        snapshots[role] = hashlib.sha256(json.dumps(cases, sort_keys=True).encode()).hexdigest()
        if user == "exec-1":
            request(client, "/api/cases/case-001", expected=404)
        for projection in ("summary", "open-points", "activity"):
            request(client, "/api/v1/cases/cp-case-a/copilot/" + projection)
        if user == "presenter":
            denied = request(client, "/api/v1/copilot/questions",
                             {"question": "Summarize this aircraft return.", "selectedCaseId": "cp-case-a"},
                             {"Origin": origin}, expected=403)
            if denied["error"]["code"] != "anti_forgery_failed":
                raise RuntimeError("Copilot did not reject missing anti-forgery credentials.")
            if model:
                answer = request(client, "/api/v1/copilot/questions",
                                 {"question": "Summarize the return status of this aircraft. Read only; do not propose or perform changes.",
                                  "selectedCaseId": "cp-case-a"},
                                 {"Origin": origin, "X-CSRF-Token": session["csrfToken"]})
                if not isinstance(answer.get("answer"), str) or not answer["answer"].strip():
                    raise RuntimeError("The live model returned no answer.")
        print(f"Verified {role}: sign-in, three scoped cases and copilot projections.")
    return snapshots


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--origin", required=True)
    parser.add_argument("--vault", required=True)
    parser.add_argument("--snapshot", type=Path, required=True)
    parser.add_argument("--compare", action="store_true")
    args = parser.parse_args()
    passcode = subprocess.check_output(
        ["az", "keyvault", "secret", "show", "--vault-name", args.vault,
         "--name", "demo-passcode", "--query", "value", "-o", "tsv"], text=True).strip()
    snapshots = verify(args.origin, passcode, model=not args.compare)
    if args.compare:
        if snapshots != json.loads(args.snapshot.read_text()):
            raise RuntimeError("Synthetic case state changed across the application restart.")
        print("Verified exact per-role case-state digests survive restart.")
    else:
        args.snapshot.write_text(json.dumps(snapshots, sort_keys=True))


if __name__ == "__main__":
    main()
