# -*- coding: utf-8 -*-
"""Model-independent PMX physics corpus inspection.

This module deliberately stops short of claiming physics equivalence.  It reports
which authored PMX features the current Magica Cloth conversion can map directly,
which ones are approximated, and which rigid bodies are dropped before an expensive
Unity player probe is attempted.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
from collections import Counter, defaultdict, deque
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, Iterable, List, Mapping, Optional, Sequence

import pmx_parse


HERE = Path(__file__).resolve().parent
DEFAULT_MANIFEST = HERE / "model_corpus.json"
REPO_ROOT = HERE.parents[1]
EPSILON = 1.0e-9
SHAPE_NAMES = {0: "sphere", 1: "box", 2: "capsule"}


class CorpusError(RuntimeError):
    """A fixture definition is unsafe, ambiguous, or unavailable."""


@dataclass(frozen=True)
class ResolvedEntry:
    model_id: str
    label: str
    path: Path
    expected_sha256: Optional[str]
    source: Mapping[str, Any]


def _portable_path(value: str, repo_root: Path) -> Path:
    value = value.replace("${REPO_ROOT}", str(repo_root))
    value = os.path.expandvars(value)
    path = Path(value).expanduser()
    if not path.is_absolute():
        path = repo_root / path
    return path.resolve()


def _validate_id(model_id: str) -> str:
    if not model_id or not re.fullmatch(r"[a-z0-9][a-z0-9_-]*", model_id):
        raise CorpusError("model id must match [a-z0-9][a-z0-9_-]*: %r" % model_id)
    return model_id


def resolve_entry(
    entry: Mapping[str, Any],
    repo_root: Path,
    root_overrides: Optional[Mapping[str, str]] = None,
    require_file: bool = True,
) -> ResolvedEntry:
    """Resolve one explicit fixture without scanning or guessing among PMX files."""

    model_id = _validate_id(str(entry.get("id", "")))
    root_value = None
    if root_overrides and model_id in root_overrides:
        root_value = root_overrides[model_id]
    if root_value is None and entry.get("root_env"):
        root_value = os.environ.get(str(entry["root_env"]))
    if root_value is None:
        root_value = entry.get("root")
    if not root_value:
        raise CorpusError("%s: fixture root is not configured" % model_id)

    root = _portable_path(str(root_value), repo_root)
    selected = entry.get("pmx")
    if not selected:
        raise CorpusError("%s: an explicit pmx path is required" % model_id)
    pmx = Path(str(selected))
    if not pmx.is_absolute():
        pmx = root / pmx
    pmx = pmx.resolve()

    # An explicit path is intentional here.  Falling back to another PMX can turn a
    # material variant, accessory, or (in the Sakuya pack) an entirely different
    # character into a false-positive test pass.
    if require_file and not pmx.is_file():
        raise CorpusError("%s: selected PMX does not exist: %s" % (model_id, pmx))
    if pmx.suffix.lower() != ".pmx":
        raise CorpusError("%s: selected fixture is not a .pmx file: %s" % (model_id, pmx))

    expected = entry.get("sha256")
    if expected:
        expected = str(expected).upper()
        if not re.fullmatch(r"[0-9A-F]{64}", expected):
            raise CorpusError("%s: sha256 must be 64 hexadecimal characters" % model_id)

    return ResolvedEntry(
        model_id=model_id,
        label=str(entry.get("label") or model_id),
        path=pmx,
        expected_sha256=expected,
        source=entry,
    )


def load_manifest(
    manifest_path: Path,
    repo_root: Path = REPO_ROOT,
    root_overrides: Optional[Mapping[str, str]] = None,
    require_files: bool = True,
) -> List[ResolvedEntry]:
    try:
        payload = json.loads(Path(manifest_path).read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise CorpusError("cannot read corpus manifest %s: %s" % (manifest_path, exc)) from exc
    if payload.get("schema") != 1:
        raise CorpusError("unsupported corpus manifest schema: %r" % payload.get("schema"))
    models = payload.get("models")
    if not isinstance(models, list) or not models:
        raise CorpusError("corpus manifest must contain a non-empty models array")

    seen = set()
    resolved = []
    for entry in models:
        if not isinstance(entry, dict):
            raise CorpusError("every models entry must be an object")
        model_id = _validate_id(str(entry.get("id", "")))
        if model_id in seen:
            raise CorpusError("duplicate model id: %s" % model_id)
        seen.add(model_id)
        resolved.append(resolve_entry(entry, repo_root, root_overrides, require_files))
    return resolved


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def _has_nonzero(values: Iterable[float]) -> bool:
    return any(abs(float(value)) > EPSILON for value in values)


def _joint_graph(body_count: int, joints: Sequence[Any]) -> Dict[str, int]:
    adjacency = [set() for _ in range(body_count)]
    valid_edges = 0
    for joint in joints:
        a, b = int(joint.rb_a), int(joint.rb_b)
        if 0 <= a < body_count and 0 <= b < body_count:
            adjacency[a].add(b)
            adjacency[b].add(a)
            valid_edges += 1

    isolated = sum(not neighbours for neighbours in adjacency)
    high_degree = sum(len(neighbours) > 2 for neighbours in adjacency)
    visited = set()
    component_sizes = []
    cyclic_components = 0
    for start in range(body_count):
        if start in visited or not adjacency[start]:
            continue
        queue = deque([start])
        visited.add(start)
        vertices = 0
        degree_sum = 0
        while queue:
            node = queue.popleft()
            vertices += 1
            degree_sum += len(adjacency[node])
            for neighbour in adjacency[node]:
                if neighbour not in visited:
                    visited.add(neighbour)
                    queue.append(neighbour)
        component_sizes.append(vertices)
        if degree_sum // 2 >= vertices:
            cyclic_components += 1
    return {
        "valid_edges": valid_edges,
        "components": len(component_sizes),
        "largest_component": max(component_sizes, default=0),
        "cyclic_components": cyclic_components,
        "isolated_bodies": isolated,
        "bodies_with_degree_over_2": high_degree,
    }


def summarize_pmx(pmx: Any) -> Dict[str, Any]:
    """Describe PMX physics and current MmdMagicaCloth structural coverage."""

    bones = list(pmx.bones)
    bodies = list(pmx.rigid_bodies)
    joints = list(pmx.joints)
    bone_count = len(bones)
    body_count = len(bodies)

    mode_counts = Counter(int(body.mode) for body in bodies)
    shape_counts = Counter(SHAPE_NAMES.get(int(body.shape), "unknown_%s" % body.shape) for body in bodies)
    dynamic = [body for body in bodies if int(body.mode) != 0]
    kinematic = [body for body in bodies if int(body.mode) == 0]
    dynamic_valid = [body for body in dynamic if 0 <= int(body.bone) < bone_count]
    dynamic_invalid = len(dynamic) - len(dynamic_valid)

    # MmdClothChains keeps the first dynamic rigid body for each bone.  Extra
    # bodies on the same bone and bodies without a bone do not become particles.
    body_by_bone = {}
    for body in dynamic_valid:
        body_by_bone.setdefault(int(body.bone), body)
    mapped_bones = set(body_by_bone)
    duplicate_dynamic = len(dynamic_valid) - len(mapped_bones)
    roots = [
        bone_index
        for bone_index in mapped_bones
        if not (0 <= int(bones[bone_index].parent) < bone_count and int(bones[bone_index].parent) in mapped_bones)
    ]
    mapping_percent = round(100.0 * len(mapped_bones) / len(dynamic), 2) if dynamic else 100.0

    invalid_joint_refs = 0
    with_linear_limits = 0
    with_linear_springs = 0
    with_rotation_springs = 0
    kind_counts = Counter()
    joint_edges_off_hierarchy = 0
    for joint in joints:
        kind_counts[int(joint.kind)] += 1
        a, b = int(joint.rb_a), int(joint.rb_b)
        if not (0 <= a < body_count and 0 <= b < body_count):
            invalid_joint_refs += 1
        else:
            ba, bb = int(bodies[a].bone), int(bodies[b].bone)
            if 0 <= ba < bone_count and 0 <= bb < bone_count:
                if int(bones[ba].parent) != bb and int(bones[bb].parent) != ba:
                    joint_edges_off_hierarchy += 1
        if _has_nonzero(tuple(joint.pos_lo) + tuple(joint.pos_hi)):
            with_linear_limits += 1
        if _has_nonzero(joint.pos_spring):
            with_linear_springs += 1
        if _has_nonzero(joint.rot_spring):
            with_rotation_springs += 1

    graph = _joint_graph(body_count, joints)
    risks = []
    if not dynamic:
        risks.append("no_dynamic_bodies")
    if dynamic_invalid:
        risks.append("dynamic_body_without_bone")
    if duplicate_dynamic:
        risks.append("duplicate_dynamic_body_on_bone")
    if mode_counts.get(2, 0):
        risks.append("mode2_is_approximated")
    if with_linear_limits or with_linear_springs:
        risks.append("linear_joint_motion_is_not_preserved")
    if any(kind != 0 for kind in kind_counts):
        risks.append("non_spring6dof_joint_is_not_preserved")
    if invalid_joint_refs:
        risks.append("invalid_joint_body_reference")
    if joint_edges_off_hierarchy:
        risks.append("joint_graph_differs_from_bone_hierarchy")
    if graph["cyclic_components"] or graph["bodies_with_degree_over_2"]:
        risks.append("branched_or_cyclic_rigidbody_graph_is_reduced_to_bone_cloth")
    if len(dynamic) > 1:
        risks.append("dynamic_rigidbody_collisions_are_not_preserved")
    if any(int(body.shape) == 1 for body in kinematic):
        risks.append("kinematic_box_is_approximated_as_sphere")
    if float(pmx.version) > 2.0:
        risks.append("pmx21_soft_body_tail_is_not_inspected")

    ready = bool(dynamic) and bool(mapped_bones) and not invalid_joint_refs
    if not ready:
        grade = "incompatible"
    elif dynamic_invalid or duplicate_dynamic:
        grade = "partial"
    else:
        grade = "approximate"

    return {
        "pmx": {
            "version": float(pmx.version),
            "name_jp": str(getattr(pmx, "name_jp", "")),
            "name_en": str(getattr(pmx, "name_en", "")),
            "bones": bone_count,
            "height_units": round(float(pmx.vert_max_y) - float(pmx.vert_min_y), 6),
        },
        "rigid_bodies": {
            "total": body_count,
            "by_mode": {str(key): mode_counts[key] for key in sorted(mode_counts)},
            "by_shape": {key: shape_counts[key] for key in sorted(shape_counts)},
        },
        "joints": {
            "total": len(joints),
            "by_kind": {str(key): kind_counts[key] for key in sorted(kind_counts)},
            "invalid_body_references": invalid_joint_refs,
            "with_linear_limits": with_linear_limits,
            "with_linear_springs": with_linear_springs,
            "with_rotation_springs": with_rotation_springs,
            "edges_not_matching_bone_parent": joint_edges_off_hierarchy,
            **graph,
        },
        "conversion": {
            "ready_for_magica_probe": ready,
            "fidelity_grade": grade,
            "dynamic_bodies": len(dynamic),
            "mapped_dynamic_bones": len(mapped_bones),
            "duplicate_dynamic_bodies": duplicate_dynamic,
            "dynamic_bodies_without_bone": dynamic_invalid,
            "body_mapping_percent": mapping_percent,
            "chain_roots": len(roots),
            "kinematic_colliders_with_bones": sum(0 <= int(body.bone) < bone_count for body in kinematic),
            "mode2_bodies": mode_counts.get(2, 0),
        },
        "risks": risks,
    }


def inspect_entry(entry: ResolvedEntry, strict_hash: bool = True) -> Dict[str, Any]:
    actual_hash = sha256_file(entry.path)
    hash_matches = entry.expected_sha256 is None or actual_hash == entry.expected_sha256
    if strict_hash and not hash_matches:
        raise CorpusError(
            "%s: fixture SHA-256 changed (expected %s, got %s)"
            % (entry.model_id, entry.expected_sha256, actual_hash)
        )
    pmx = pmx_parse.load(str(entry.path))
    result = {
        "id": entry.model_id,
        "label": entry.label,
        "path": str(entry.path),
        "bytes": entry.path.stat().st_size,
        "sha256": actual_hash,
        "expected_sha256": entry.expected_sha256,
        "hash_matches": hash_matches,
    }
    result.update(summarize_pmx(pmx))
    return result


def _markdown(results: Sequence[Mapping[str, Any]], missing: Sequence[Mapping[str, str]]) -> str:
    lines = [
        "# MMD physics corpus",
        "",
        "This is a structural conversion report, not proof that Magica Cloth matches Bullet motion.",
        "",
        "| fixture | PMX | bones | rigid 0/1/2 | joints | mapped dynamic | chains | grade |",
        "|---|---|---:|---:|---:|---:|---:|---|",
    ]
    for result in results:
        modes = result["rigid_bodies"]["by_mode"]
        conversion = result["conversion"]
        lines.append(
            "| {label} | {version:.1f} | {bones} | {m0}/{m1}/{m2} | {joints} | "
            "{mapped}/{dynamic} ({percent:.2f}%) | {roots} | {grade} |".format(
                label=result["label"],
                version=result["pmx"]["version"],
                bones=result["pmx"]["bones"],
                m0=modes.get("0", 0),
                m1=modes.get("1", 0),
                m2=modes.get("2", 0),
                joints=result["joints"]["total"],
                mapped=conversion["mapped_dynamic_bones"],
                dynamic=conversion["dynamic_bodies"],
                percent=conversion["body_mapping_percent"],
                roots=conversion["chain_roots"],
                grade=conversion["fidelity_grade"],
            )
        )
    lines.extend(["", "## Fidelity risks", ""])
    for result in results:
        lines.append("### %s" % result["label"])
        lines.append("")
        if result["risks"]:
            lines.extend("- `%s`" % risk for risk in result["risks"])
        else:
            lines.append("- None detected by the structural inspector.")
        lines.append("")
    if missing:
        lines.extend(["## Missing fixtures", ""])
        lines.extend("- `%s`: %s" % (item["id"], item["error"]) for item in missing)
        lines.append("")
    return "\n".join(lines)


def _parse_overrides(values: Sequence[str]) -> Dict[str, str]:
    result = {}
    for value in values:
        if "=" not in value:
            raise CorpusError("--root must use id=path: %s" % value)
        model_id, path = value.split("=", 1)
        result[_validate_id(model_id)] = path
    return result


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--repo-root", type=Path, default=REPO_ROOT)
    parser.add_argument("--output-dir", type=Path, default=REPO_ROOT / "test-output" / "mmd-physics-corpus")
    parser.add_argument("--model", action="append", default=[], help="only inspect this fixture id (repeatable)")
    parser.add_argument("--root", action="append", default=[], help="override one fixture root: id=path")
    parser.add_argument("--allow-missing", action="store_true", help="report unavailable local fixtures instead of failing")
    parser.add_argument("--ignore-hash", action="store_true", help="report but do not fail a changed fixture hash")
    args = parser.parse_args(argv)

    try:
        overrides = _parse_overrides(args.root)
        entries = load_manifest(args.manifest, args.repo_root.resolve(), overrides, require_files=False)
        wanted = set(args.model)
        if wanted:
            unknown = wanted.difference(entry.model_id for entry in entries)
            if unknown:
                raise CorpusError("unknown fixture id(s): %s" % ", ".join(sorted(unknown)))
            entries = [entry for entry in entries if entry.model_id in wanted]

        results: List[Dict[str, Any]] = []
        missing: List[Dict[str, str]] = []
        for entry in entries:
            if not entry.path.is_file():
                error = "selected PMX does not exist: %s" % entry.path
                if not args.allow_missing:
                    raise CorpusError("%s: %s" % (entry.model_id, error))
                missing.append({"id": entry.model_id, "error": error})
                continue
            try:
                results.append(inspect_entry(entry, strict_hash=not args.ignore_hash))
            except Exception as exc:
                if not args.allow_missing:
                    raise
                missing.append({"id": entry.model_id, "error": str(exc)})

        out_dir = args.output_dir.resolve()
        out_dir.mkdir(parents=True, exist_ok=True)
        payload = {
            "schema": 1,
            "manifest": str(args.manifest.resolve()),
            "models": results,
            "missing": missing,
        }
        json_path = out_dir / "structural-report.json"
        md_path = out_dir / "structural-report.md"
        json_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        md_path.write_text(_markdown(results, missing), encoding="utf-8")
        for result in results:
            conversion = result["conversion"]
            print(
                "{id}: {mapped}/{dynamic} dynamic bodies mapped; {chains} chains; {grade}; risks={risks}".format(
                    id=result["id"],
                    mapped=conversion["mapped_dynamic_bones"],
                    dynamic=conversion["dynamic_bodies"],
                    chains=conversion["chain_roots"],
                    grade=conversion["fidelity_grade"],
                    risks=len(result["risks"]),
                )
            )
        print("wrote %s" % json_path)
        return 0 if results and (args.allow_missing or not missing) else 1
    except (CorpusError, OSError, ValueError) as exc:
        print("corpus validation failed: %s" % exc, file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
