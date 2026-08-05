# -*- coding: utf-8 -*-
"""Validate one built-player Magica probe recording before it is trusted."""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
from pathlib import Path
from typing import Any, Dict, Mapping, Optional, Sequence


SCENARIOS = ("rest", "turn", "walk", "spin", "dance")
RIGID_EPSILON_M = 0.001


class ProbeValidationError(RuntimeError):
    pass


def _load_object(path: Path) -> Dict[str, Any]:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise ProbeValidationError("cannot read %s: %s" % (path.name, exc)) from exc
    if not isinstance(payload, dict):
        raise ProbeValidationError("%s: JSON root must be an object" % path.name)
    return payload


def _finite_number(value: Any, where: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ProbeValidationError("%s: expected a number" % where)
    number = float(value)
    if not math.isfinite(number):
        raise ProbeValidationError("%s: NaN/Infinity is not valid probe data" % where)
    return number


def _finite_vector(value: Any, size: int, where: str) -> Sequence[float]:
    if not isinstance(value, list) or len(value) != size:
        raise ProbeValidationError("%s: expected a %d-value array" % (where, size))
    return [_finite_number(item, "%s[%d]" % (where, index)) for index, item in enumerate(value)]


def _distance(a: Sequence[float], b: Sequence[float]) -> float:
    return math.sqrt(sum((a[index] - b[index]) ** 2 for index in range(3)))


def _relative(frame: Sequence[Sequence[float]]) -> Sequence[float]:
    return [frame[-1][axis] - frame[0][axis] for axis in range(3)]


def _validate_recording(payload: Mapping[str, Any], scenario: str) -> Dict[str, Any]:
    if payload.get("scenario") != scenario:
        raise ProbeValidationError("%s: scenario field is %r" % (scenario, payload.get("scenario")))
    fps = _finite_number(payload.get("fps"), "%s.fps" % scenario)
    if fps <= 0:
        raise ProbeValidationError("%s: fps must be positive" % scenario)
    units = _finite_number(payload.get("unitsPerMeter"), "%s.unitsPerMeter" % scenario)
    if units <= 0:
        raise ProbeValidationError("%s: unitsPerMeter must be positive" % scenario)

    dt = payload.get("dt")
    anchor = payload.get("anchor")
    if not isinstance(dt, list) or not dt:
        raise ProbeValidationError("%s: dt is empty" % scenario)
    if not isinstance(anchor, list) or len(anchor) != len(dt):
        raise ProbeValidationError("%s: anchor/dt frame counts differ" % scenario)
    for frame_index, value in enumerate(dt):
        if _finite_number(value, "%s.dt[%d]" % (scenario, frame_index)) <= 0:
            raise ProbeValidationError("%s: dt[%d] must be positive" % (scenario, frame_index))
    for frame_index, value in enumerate(anchor):
        _finite_vector(value, 7, "%s.anchor[%d]" % (scenario, frame_index))

    chains = payload.get("chains")
    if not isinstance(chains, dict) or not chains:
        raise ProbeValidationError("%s: no chains were recorded" % scenario)
    normalized = {}
    for chain_id, chain in chains.items():
        if not isinstance(chain_id, str) or not chain_id:
            raise ProbeValidationError("%s: chain id must be non-empty text" % scenario)
        if not isinstance(chain, dict):
            raise ProbeValidationError("%s.%s: chain must be an object" % (scenario, chain_id))
        bones = chain.get("bones")
        frames = chain.get("frames")
        if not isinstance(bones, list) or not bones:
            raise ProbeValidationError("%s.%s: bones are empty" % (scenario, chain_id))
        if not isinstance(frames, list) or len(frames) != len(dt):
            raise ProbeValidationError("%s.%s: chain/dt frame counts differ" % (scenario, chain_id))
        parsed_frames = []
        for frame_index, frame in enumerate(frames):
            if not isinstance(frame, list) or len(frame) != len(bones):
                raise ProbeValidationError(
                    "%s.%s.frames[%d]: expected %d bone positions"
                    % (scenario, chain_id, frame_index, len(bones))
                )
            parsed_frames.append(
                [
                    _finite_vector(position, 3, "%s.%s.frames[%d][%d]" % (scenario, chain_id, frame_index, bone_index))
                    for bone_index, position in enumerate(frame)
                ]
            )
        normalized[chain_id] = {"bones": bones, "frames": parsed_frames}
    return {"fps": fps, "unitsPerMeter": units, "frames": len(dt), "chains": normalized}


def validate_run(directory: Path, expected_sha256: Optional[str] = None) -> Dict[str, Any]:
    directory = Path(directory).resolve()
    if not directory.is_dir():
        raise ProbeValidationError("probe output directory does not exist: %s" % directory)

    model_path = directory / "model.json"
    if not model_path.is_file():
        raise ProbeValidationError("missing model.json")
    model = _load_object(model_path)
    actual_sha = str(model.get("sha256", "")).upper()
    if not re.fullmatch(r"[0-9A-F]{64}", actual_sha):
        raise ProbeValidationError("model.json has no valid SHA-256")
    if expected_sha256 and actual_sha != str(expected_sha256).upper():
        raise ProbeValidationError(
            "model SHA-256 mismatch: expected %s, recorded %s" % (str(expected_sha256).upper(), actual_sha)
        )

    recordings = {}
    for scenario in SCENARIOS:
        path = directory / ("magica_%s.json" % scenario)
        if not path.is_file():
            raise ProbeValidationError("missing scenario file: %s" % scenario)
        recordings[scenario] = _validate_recording(_load_object(path), scenario)

    expected_chains = set(recordings[SCENARIOS[0]]["chains"])
    for scenario in SCENARIOS[1:]:
        actual = set(recordings[scenario]["chains"])
        if actual != expected_chains:
            raise ProbeValidationError(
                "%s: chain ids differ from rest (missing=%s extra=%s)"
                % (scenario, sorted(expected_chains - actual), sorted(actual - expected_chains))
            )
    metadata_chains = model.get("chainIds", model.get("chains"))
    if metadata_chains is not None and set(metadata_chains) != expected_chains:
        raise ProbeValidationError("model.json chain ids do not match scenario recordings")

    deformation = {chain_id: 0.0 for chain_id in expected_chains}
    for scenario, recording in recordings.items():
        units = recording["unitsPerMeter"]
        for chain_id, chain in recording["chains"].items():
            relative = [_relative(frame) for frame in chain["frames"]]
            distances = [_distance(value, (0.0, 0.0, 0.0)) / units for value in relative]
            score = max(distances) - min(distances)
            if scenario in ("rest", "walk"):
                origin = relative[0]
                score = max(score, max(_distance(value, origin) / units for value in relative))
            deformation[chain_id] = max(deformation[chain_id], score)

    max_deformation = max(deformation.values(), default=0.0)
    all_rigid = bool(deformation) and all(value < RIGID_EPSILON_M for value in deformation.values())
    if all_rigid:
        raise ProbeValidationError(
            "every recorded chain is rigid (< %.1f mm relative deformation); model cloth did not simulate"
            % (RIGID_EPSILON_M * 1000.0)
        )

    return {
        "schema": 1,
        "directory": str(directory),
        "model_sha256": actual_sha,
        "pmx_path": model.get("pmxPath"),
        "scenarios": list(SCENARIOS),
        "chains": sorted(expected_chains),
        "frames": {scenario: recordings[scenario]["frames"] for scenario in SCENARIOS},
        "deformation_m": deformation,
        "max_deformation_m": max_deformation,
        "all_chains_rigid": all_rigid,
    }


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--expected-sha", dest="expected_sha256")
    args = parser.parse_args(argv)
    try:
        report = validate_run(args.directory, args.expected_sha256)
        out = args.directory.resolve() / "probe-validation.json"
        out.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(
            "probe valid: %d scenarios, %d chains, max deformation %.4f m; wrote %s"
            % (len(report["scenarios"]), len(report["chains"]), report["max_deformation_m"], out)
        )
        return 0
    except (ProbeValidationError, OSError, ValueError) as exc:
        print("probe validation failed: %s" % exc, file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
