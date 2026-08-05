# -*- coding: utf-8 -*-
import json
import sys
import tempfile
import unittest
from pathlib import Path


HARNESS_DIR = Path(__file__).resolve().parents[1]
if str(HARNESS_DIR) not in sys.path:
    sys.path.insert(0, str(HARNESS_DIR))

import validate_probe_run


SCENARIOS = ("rest", "turn", "walk", "spin", "dance")
SHA = "A" * 64


def recording(name, live=True):
    tip_x = 1.02 if live else 1.0
    return {
        "scenario": name,
        "fps": 60,
        "unitsPerMeter": 1.0,
        "dt": [1.0 / 60.0, 1.0 / 60.0],
        "anchor": [[0, 0, 0, 0, 0, 0, 1], [0, 0, 0, 0, 0, 0, 1]],
        "chains": {
            "chain_0001_0002": {
                "bones": ["root", "tip"],
                "frames": [
                    [[0, 0, 0], [1, 0, 0]],
                    [[0, 0, 0], [tip_x, 0, 0]],
                ],
            }
        },
    }


class ProbeRunValidationTests(unittest.TestCase):
    def write_run(self, root, *, live=True):
        root = Path(root)
        (root / "model.json").write_text(
            json.dumps({"schema": 1, "sha256": SHA, "pmxPath": "fixture.pmx", "chains": ["chain_0001_0002"]}),
            encoding="utf-8",
        )
        for scenario in SCENARIOS:
            (root / ("magica_%s.json" % scenario)).write_text(
                json.dumps(recording(scenario, live=live)), encoding="utf-8"
            )

    def test_accepts_complete_live_run_with_matching_model_identity(self):
        with tempfile.TemporaryDirectory() as td:
            self.write_run(td)
            report = validate_probe_run.validate_run(Path(td), expected_sha256=SHA.lower())
            self.assertEqual(list(SCENARIOS), report["scenarios"])
            self.assertFalse(report["all_chains_rigid"])
            self.assertGreater(report["max_deformation_m"], 0.001)

    def test_missing_canonical_scenario_fails_even_when_dance_exists(self):
        with tempfile.TemporaryDirectory() as td:
            self.write_run(td)
            (Path(td) / "magica_rest.json").unlink()
            with self.assertRaisesRegex(validate_probe_run.ProbeValidationError, "missing scenario file.*rest"):
                validate_probe_run.validate_run(Path(td), expected_sha256=SHA)

    def test_empty_chains_are_never_a_success(self):
        with tempfile.TemporaryDirectory() as td:
            self.write_run(td)
            path = Path(td) / "magica_turn.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["chains"] = {}
            path.write_text(json.dumps(payload), encoding="utf-8")
            with self.assertRaisesRegex(validate_probe_run.ProbeValidationError, "turn.*no chains"):
                validate_probe_run.validate_run(Path(td), expected_sha256=SHA)

    def test_sha_mismatch_rejects_a_recording_from_another_model(self):
        with tempfile.TemporaryDirectory() as td:
            self.write_run(td)
            with self.assertRaisesRegex(validate_probe_run.ProbeValidationError, "SHA-256 mismatch"):
                validate_probe_run.validate_run(Path(td), expected_sha256="B" * 64)

    def test_perfectly_rigid_model_recording_is_invalid(self):
        with tempfile.TemporaryDirectory() as td:
            self.write_run(td, live=False)
            with self.assertRaisesRegex(validate_probe_run.ProbeValidationError, "every recorded chain is rigid"):
                validate_probe_run.validate_run(Path(td), expected_sha256=SHA)


if __name__ == "__main__":
    unittest.main()
