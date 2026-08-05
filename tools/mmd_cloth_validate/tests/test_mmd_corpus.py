# -*- coding: utf-8 -*-
import json
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace


HARNESS_DIR = Path(__file__).resolve().parents[1]
if str(HARNESS_DIR) not in sys.path:
    sys.path.insert(0, str(HARNESS_DIR))

import mmd_corpus


def bone(parent):
    return SimpleNamespace(parent=parent, name_jp="", name_en="")


def body(index, bone_index, mode, shape=2, group=0, mask=0xFFFF):
    return SimpleNamespace(
        index=index,
        bone=bone_index,
        mode=mode,
        shape=shape,
        group=group,
        mask=mask,
        name_jp="rb%d" % index,
        name_en="",
    )


def joint(a, b, *, pos_limit=False, pos_spring=False, rot_spring=False, kind=0):
    return SimpleNamespace(
        rb_a=a,
        rb_b=b,
        kind=kind,
        pos_lo=(-0.1, 0.0, 0.0) if pos_limit else (0.0, 0.0, 0.0),
        pos_hi=(0.1, 0.0, 0.0) if pos_limit else (0.0, 0.0, 0.0),
        pos_spring=(1.0, 0.0, 0.0) if pos_spring else (0.0, 0.0, 0.0),
        rot_spring=(0.0, 2.0, 0.0) if rot_spring else (0.0, 0.0, 0.0),
    )


class CorpusManifestTests(unittest.TestCase):
    def test_resolves_repo_token_and_explicit_nested_pmx(self):
        with tempfile.TemporaryDirectory() as td:
            repo = Path(td)
            pmx = repo / "assets" / "MODEL" / "fixture" / "nested" / "model.Pmx"
            pmx.parent.mkdir(parents=True)
            pmx.write_bytes(b"PMX fixture")

            resolved = mmd_corpus.resolve_entry(
                {
                    "id": "fixture",
                    "root": "${REPO_ROOT}/assets/MODEL/fixture",
                    "pmx": "nested/model.Pmx",
                },
                repo,
            )

            self.assertEqual(pmx.resolve(), resolved.path)
            self.assertEqual("fixture", resolved.model_id)

    def test_missing_explicit_pmx_is_an_error_instead_of_silently_picking_another(self):
        with tempfile.TemporaryDirectory() as td:
            repo = Path(td)
            root = repo / "model"
            root.mkdir()
            (root / "other.pmx").write_bytes(b"PMX other")

            with self.assertRaisesRegex(mmd_corpus.CorpusError, "selected PMX does not exist"):
                mmd_corpus.resolve_entry(
                    {"id": "fixture", "root": str(root), "pmx": "wanted.pmx"},
                    repo,
                )

    def test_manifest_rejects_duplicate_model_ids(self):
        with tempfile.TemporaryDirectory() as td:
            manifest = Path(td) / "models.json"
            manifest.write_text(
                json.dumps(
                    {
                        "schema": 1,
                        "models": [
                            {"id": "same", "root": ".", "pmx": "a.pmx"},
                            {"id": "same", "root": ".", "pmx": "b.pmx"},
                        ],
                    }
                ),
                encoding="utf-8",
            )

            with self.assertRaisesRegex(mmd_corpus.CorpusError, "duplicate model id"):
                mmd_corpus.load_manifest(manifest, Path(td), require_files=False)


class ConversionCoverageTests(unittest.TestCase):
    def test_reports_exactly_what_the_current_magica_converter_can_and_cannot_map(self):
        pmx = SimpleNamespace(
            version=2.0,
            name_jp="fixture",
            name_en="Fixture",
            vert_min_y=0.0,
            vert_max_y=16.0,
            bones=[bone(-1), bone(0), bone(1)],
            rigid_bodies=[
                body(0, 0, 0, shape=0),       # kinematic collider
                body(1, 1, 1, shape=2),       # ordinary dynamic body
                body(2, 2, 2, shape=1),       # mode 2: currently approximated as mode 1
                body(3, 2, 1, shape=2),       # duplicate dynamic body on the same bone
                body(4, 99, 1, shape=0),      # no usable bone
            ],
            joints=[
                joint(0, 1),
                joint(1, 2, pos_limit=True, pos_spring=True, rot_spring=True),
                joint(2, 99),                 # invalid rigid-body reference
            ],
        )

        got = mmd_corpus.summarize_pmx(pmx)

        self.assertEqual({"0": 1, "1": 3, "2": 1}, got["rigid_bodies"]["by_mode"])
        self.assertEqual(4, got["conversion"]["dynamic_bodies"])
        self.assertEqual(2, got["conversion"]["mapped_dynamic_bones"])
        self.assertEqual(1, got["conversion"]["duplicate_dynamic_bodies"])
        self.assertEqual(1, got["conversion"]["dynamic_bodies_without_bone"])
        self.assertEqual(50.0, got["conversion"]["body_mapping_percent"])
        self.assertEqual(1, got["conversion"]["chain_roots"])
        self.assertEqual(1, got["joints"]["invalid_body_references"])
        self.assertEqual(1, got["joints"]["with_linear_limits"])
        self.assertEqual(1, got["joints"]["with_linear_springs"])
        self.assertEqual(1, got["joints"]["with_rotation_springs"])
        self.assertIn("mode2_is_approximated", got["risks"])
        self.assertIn("linear_joint_motion_is_not_preserved", got["risks"])
        self.assertIn("duplicate_dynamic_body_on_bone", got["risks"])
        self.assertIn("dynamic_body_without_bone", got["risks"])


if __name__ == "__main__":
    unittest.main()
