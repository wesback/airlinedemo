"""Contract tests for the frozen synthetic 40-case evaluation suite (v1).

Run: python3 -m unittest discover -s evaluation/tests -v
"""
import copy
import hashlib
import importlib.util
import json
import re
import unittest
from collections import Counter
from pathlib import Path

HERE = Path(__file__).resolve().parent
CASES_DIR = HERE.parent / "cases"
GEN_PATH = CASES_DIR / "generate.py"


def _load_generator():
    spec = importlib.util.spec_from_file_location("generate", GEN_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


GEN = _load_generator()
VERSION_DIR = CASES_DIR / GEN.VERSION
CASES_TEXT = (VERSION_DIR / "cases.jsonl").read_text(encoding="utf-8")
MANIFEST = json.loads((VERSION_DIR / "manifest.json").read_text(encoding="utf-8"))
CASES = [json.loads(line) for line in CASES_TEXT.splitlines() if line.strip()]

# Identifiers and values that would indicate real or customer data.
REAL_LOOKING = re.compile(
    r"(\b[A-Z]{2}-\d{3,}\b|@[A-Za-z0-9.-]+\.(com|org|net)|"
    r"\b\d{3}-\d{2}-\d{4}\b|password|secret|api[_-]?key|BEGIN [A-Z ]*PRIVATE KEY)",
    re.IGNORECASE,
)
# Fictional identifiers that the generator emits and that may match REAL_LOOKING.
SYNTHETIC_TOKEN = re.compile(r"SYN-(?:AC-|v\d+-)\d{3}")


def find_real_looking(text):
    return REAL_LOOKING.search(SYNTHETIC_TOKEN.sub("", text))


def validate_suite(cases, manifest, cases_text):
    """Raise ValueError if the suite is not a valid frozen v1 fixture."""
    if len(cases) != manifest["case_count"] or len(cases) != GEN.CASE_COUNT:
        raise ValueError("case count mismatch")
    if hashlib.sha256(cases_text.encode("utf-8")).hexdigest() != manifest["sha256"]:
        raise ValueError("content hash mismatch: modified file with old hash")
    if manifest["version"] != GEN.VERSION:
        raise ValueError("version mismatch")
    for case in cases:
        if case.get("fixture_version") != manifest["version"]:
            raise ValueError(f"{case.get('case_id')}: wrong fixture version")
        missing = [k for k in GEN.REQUIRED_ANNOTATIONS if k not in case.get("annotations", {})]
        if missing:
            raise ValueError(f"{case.get('case_id')}: missing annotations {missing}")
    return True


class CaseCountTests(unittest.TestCase):
    def test_exactly_40_cases(self):
        self.assertEqual(len(CASES), 40)
        self.assertEqual(MANIFEST["case_count"], 40)

    def test_case_ids_unique(self):
        ids = [c["case_id"] for c in CASES]
        self.assertEqual(len(ids), len(set(ids)))

    def test_count_break_is_detected(self):
        with self.assertRaises(ValueError):
            validate_suite(CASES[:-1], MANIFEST, CASES_TEXT)


class DeterminismTests(unittest.TestCase):
    def test_regeneration_matches_committed_files(self):
        self.assertEqual(GEN.render(GEN.generate_cases()), CASES_TEXT)
        self.assertEqual(
            GEN.render_manifest(GEN.build_manifest(GEN.render(GEN.generate_cases()))),
            (VERSION_DIR / "manifest.json").read_text(encoding="utf-8"),
        )

    def test_same_seed_same_output(self):
        self.assertEqual(GEN.render(GEN.generate_cases()), GEN.render(GEN.generate_cases()))

    def test_different_seed_changes_output(self):
        self.assertNotEqual(GEN.render(GEN.generate_cases(seed=1)), CASES_TEXT)


class CoverageTests(unittest.TestCase):
    def test_prd_mix_is_exact(self):
        counts = Counter(c["situation"] for c in CASES)
        self.assertEqual(dict(counts), GEN.MIX)
        self.assertEqual(sum(GEN.MIX.values()), 40)

    def test_every_headline_situation_present(self):
        situations = {c["situation"] for c in CASES}
        self.assertEqual(situations, set(GEN.MIX))

    def test_unprocessed_and_injection_cases_have_their_condition(self):
        for c in CASES:
            docs = c["inputs"]["documents"]
            if c["situation"] == "unprocessed_evidence":
                self.assertTrue(any(d["processing"] != "processed" for d in docs))
            if c["situation"] == "injection_attempt":
                self.assertTrue(any("Ignore prior instructions" in d["text"] for d in docs))


class SyntheticOnlyTests(unittest.TestCase):
    def test_every_case_is_synthetic(self):
        for c in CASES:
            self.assertEqual(c["source"], "synthetic", c["case_id"])
            self.assertTrue(c["case_id"].startswith("SYN-"), c["case_id"])

    def test_no_real_looking_identifiers_or_secrets(self):
        self.assertIsNone(find_real_looking(CASES_TEXT))

    def test_leak_detector_catches_real_looking_data(self):
        self.assertIsNotNone(find_real_looking("contact jane@example.com"))
        self.assertIsNotNone(find_real_looking('"tail": "AB-12345"'))
        self.assertIsNone(find_real_looking("SYN-v1-001 SYN-AC-001"))


class AnnotationTests(unittest.TestCase):
    def test_every_case_has_required_annotations(self):
        for c in CASES:
            for key in GEN.REQUIRED_ANNOTATIONS:
                self.assertIn(key, c["annotations"], f"{c['case_id']} missing {key}")

    def test_missing_annotation_is_rejected(self):
        broken = copy.deepcopy(CASES)
        del broken[0]["annotations"]["expected_outcome"]
        text = GEN.render(broken)
        manifest = GEN.build_manifest(text)
        with self.assertRaises(ValueError):
            validate_suite(broken, manifest, text)

    def test_unreviewed_draft_is_recorded(self):
        self.assertEqual(MANIFEST["status"], "draft-pending-product-owner-review")
        self.assertFalse(MANIFEST["frozen"])
        for c in CASES:
            self.assertEqual(c["annotations"]["reviewer_status"], "pending")


class HashVersionTests(unittest.TestCase):
    def test_committed_suite_validates(self):
        self.assertTrue(validate_suite(CASES, MANIFEST, CASES_TEXT))

    def test_modified_file_with_old_hash_is_rejected(self):
        tampered = CASES_TEXT.replace("ready", "gap_open", 1)
        self.assertNotEqual(tampered, CASES_TEXT)
        with self.assertRaises(ValueError):
            validate_suite(CASES, MANIFEST, tampered)

    def test_content_change_without_version_bump_is_rejected(self):
        changed = copy.deepcopy(CASES)
        changed[0]["inputs"]["aircraft_ref"] = "SYN-AC-999"
        text = GEN.render(changed)
        stale = dict(MANIFEST)
        with self.assertRaises(ValueError):
            validate_suite(changed, stale, text)

    def test_wrong_version_is_rejected(self):
        bad = copy.deepcopy(CASES)
        bad[0]["fixture_version"] = "v0"
        text = GEN.render(bad)
        manifest = dict(MANIFEST, sha256=hashlib.sha256(text.encode()).hexdigest())
        with self.assertRaises(ValueError):
            validate_suite(bad, manifest, text)


if __name__ == "__main__":
    unittest.main()
