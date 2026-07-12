#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""compare.py -- comparator for the MMD->Magica cloth conversion validation harness.

Loads every ref_<scenario>.json / magica_<scenario>.json present in this directory
(missing files are tolerated and reported as NOT-RUN), aligns the recorded chains on
their COMMON bone segment (the Unity probe omits kinematic root bones, e.g. the Tie
chain records Tie_2_1.. while the reference records Tie_0_1..), computes the shared
metric set with cloth_metrics.compute_metrics on BOTH sides, and prints a per-chain
per-metric table: reference value, magica value, relative error, PASS/FAIL at +/-35%
tolerance with absolute floors (5 deg angles, 0.15 s times, 2 cm amplitudes).

Also emits report.md with the same table plus a "suspect knobs" section mapping each
failing metric to the conversion parameter in MmdMagicaCloth.cs that most directly
controls it, and the direction to move it.

Usage:  python compare.py [dir]          (default: the script's own directory)
Rerun after the Unity probe produces magica_*.json -- nothing else to configure.
"""
import copy
import json
import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cloth_metrics as cm

SCENARIOS = ("rest", "turn", "walk", "spin")
REL_TOL = 0.35
# ref turnPeakAmpNorm below this => the chain is not meaningfully driven by the head
# yaw (tie/skirt anchor on the torso/hip) -> its turnLagMs / oscillations are noise.
NOISE_NORM = 0.05

#            metric              kind      needs-scenario
METRIC_ORDER = [
    ("chainLenM",       "amp",    "rest"),
    ("restDroopDeg",    "ang",    "rest"),
    ("settleTime",      "time",   "rest"),
    ("turnPeakAmp",     "amp",    "turn"),
    ("turnPeakAmpNorm", "norm",   "turn"),
    ("turnLagMs",       "timems", "turn"),
    ("oscillations",    "count",  "turn"),
    ("walkStreamDeg",   "ang",    "walk"),
    ("walkRecoverySec", "time",   "walk"),
    ("spinFlingAmp",    "amp",    "spin"),
]
FLOOR = {"ang": 5.0, "time": 0.15, "timems": 150.0, "amp": 0.02, "count": 2.0}
# "norm" floor is per-chain: 0.02 m expressed in chain lengths (0.02 / ref chainLenM)

# ----------------------------------------------------------------------------------
# Suspect knobs: (metric, direction-of-magica-vs-ref) -> which MmdMagicaCloth.cs
# parameter most directly controls it and which way to move it.
# ----------------------------------------------------------------------------------
KNOBS = {
    ("restDroopDeg", "high"): (
        "Magica hangs LESS vertical than Bullet at rest. Either angle restoration is wrongly ON "
        "(`useAngle = springMean > 0` around line 176 misclassifying this part -> pins it to the styled "
        "authored pose), `gravityFalloff` is too high (holds rest shape against gravity; it is set = "
        "springNorm, line 180), or gravity is effectively too weak: `sd.gravity = 9.8*unitsPerMeter "
        "(~375 u/s^2)` needs the LOCAL MC2 gravity<=20 clamp patch -- if the patch is missing the sim "
        "runs at ~5% gravity and chains keep their styled outward pose. Move: falloff/stiffness down, "
        "verify the clamp patch."),
    ("restDroopDeg", "low"): (
        "Magica hangs MORE vertical than Bullet. The reference keeps part of the authored pose because "
        "MMD 0/0-LOCKED joint LIMITS are load-bearing (e.g. twintails: authored 47.9 deg -> Bullet only "
        "relaxes to ~32 deg). MmdMagicaCloth derives stiffness from joint rotation SPRINGS only and "
        "ignores locked limits (`useAngle = springMean > 0`), so spring-less chains become free pendulums "
        "and hang straight down. Fix: derive `angleRestorationConstraint` stiffness (or a nonzero "
        "`gravityFalloff`) from joint-limit tightness (acc.LimSum/LimN is already accumulated but unused "
        "for stiffness), not just from springs."),
    ("settleTime", "high"): (
        "Magica keeps ringing longer. `sd.damping` too low: `BulletToMagicaDamping` (cap 0.2, solver "
        "power 0.6, line ~71) may underestimate -- raise the cap or map from the authored ANGULAR damping "
        "too (hair is authored angular-damp ~2.0, capped to 1.0 in Bullet = very dead)."),
    ("settleTime", "low"): (
        "Magica settles too fast = over-damped. `sd.damping` too high (per-substep damping at 150 Hz "
        "compounds hard -- check BulletToMagicaDamping and the dampTip curve, line ~331), or angle "
        "restoration/`gravityFalloff` pinning the chain so it never swings at all."),
    ("turnPeakAmp", "low"): (
        "Chain barely swings out on the head turn. Primary knob: `worldInertia` too low (line 191: "
        "Lerp(0.7, 0.25, springNorm); low = tracks the anchor rigidly). Also check the speed clamps: "
        "`particleSpeedLimit = 8 m/s * unitsPerMeter` (~300 u/s) requires the MC2 MaxParticleSpeedLimit "
        "patch -- unpatched it clamps back to 10 u/s = 0.26 m/s and the chain physically cannot whip. "
        "Secondary: damping too high."),
    ("turnPeakAmp", "high"): (
        "Chain overswings vs Bullet. `worldInertia` too high (lower the 0.7 pendulum end, line 191), "
        "damping too low, or missing limit-derived angle stiffness (Bullet's locked joint limits resist "
        "the swing; Magica has restoration OFF for spring-less chains)."),
    ("turnLagMs", "high"): (
        "Tip peaks too late = pendulum too slow. Pendulum period ~ sqrt(L/g): gravity effectively too "
        "weak -- verify the MC2 gravity<=20 clamp patch is live (`sd.gravity = 9.8*upm` ~375 u/s^2); "
        "or damping too high (peak dragged later)."),
    ("turnLagMs", "low"): (
        "Tip peaks too early = chain stiffer/faster than Bullet. Angle restoration too strong "
        "(`angleStiff`), `worldInertia` too low (tip yanked with the head instead of lagging), or "
        "`movementInertiaSmoothing` (0.3, line 346) smoothing away the anchor acceleration spike."),
    ("oscillations", "high"): (
        "Under-damped ring-down: raise `sd.damping` (BulletToMagicaDamping cap 0.2 may be too low for "
        "chains whose authored angular damp saturated at 1.0/s in Bullet)."),
    ("oscillations", "low"): (
        "Over-damped (or pinned): lower `sd.damping`, and check angle restoration / `gravityFalloff` "
        "are not killing the free swing."),
    ("walkStreamDeg", "low"): (
        "Chain does not stream backward during the walk. `worldInertia` too low (reference frame drags "
        "the cloth along = no apparent wind), or `movementSpeedLimit` re-enabled / `particleSpeedLimit` "
        "still clamped to the stock 10 u/s (MC2 patch missing) so particles cannot keep the lag."),
    ("walkStreamDeg", "high"): (
        "Chain streams too far back. `worldInertia` too high, or damping too low (no air resistance "
        "to relax the stream angle during constant-velocity motion -- Bullet reaches a shallower "
        "steady-state because damping bleeds relative velocity)."),
    ("walkRecoverySec", "high"): (
        "Slow recovery after the stop: damping too low (keeps swinging outside the 10-deg band) or "
        "gravity too weak (MC2 clamp patch). If it oscillates through the band repeatedly, raise "
        "`sd.damping`; if it creeps back slowly, check gravity."),
    ("walkRecoverySec", "low"): (
        "Snaps back instantly = over-damped or pinned (angle restoration / gravityFalloff). Usually "
        "benign if ref is also ~0."),
    ("spinFlingAmp", "low"): (
        "No centrifugal fling on the 360 spin. `worldInertia` too low (anchor rotation not converted "
        "into cloth inertia -- also verify the cloth GO is parented to the ANCHOR bone, line 317, not "
        "the static root), `rotationSpeedLimit` accidentally enabled (line 352 disables it), or the "
        "`particleSpeedLimit` MC2 clamp (unpatched = 0.26 m/s cap kills the fling)."),
    ("spinFlingAmp", "high"): (
        "Over-fling: `worldInertia` too high or `depthInertia` too low (line 194: 0.5*clamp(massGrad/5.7); "
        "raising it carries the root with the body and reins in the tip), or damping too low."),
    ("chainLenM", "high"): (
        "Geometry/units mismatch, not physics: the probe's `unitsPerMeter` derivation differs from the "
        "reference (vertex-mesh height vs bone-Y-extent gives ~2.3% skew; MmdMagicaCloth line ~152 uses "
        "bone extents) or the chains were not trimmed to the same bone segment. Fix units first -- every "
        "amplitude metric scales with it."),
    ("chainLenM", "low"): (
        "Geometry/units mismatch, not physics: see chainLenM/high -- unitsPerMeter derivation or bone-"
        "segment alignment. Fix units first; every amplitude metric scales with it."),
}


# ----------------------------------------------------------------------------------
def load_side(d, prefix):
    """{scenario: json} for every <prefix>_<scenario>.json that exists."""
    out = {}
    for s in SCENARIOS:
        p = os.path.join(d, "%s_%s.json" % (prefix, s))
        if os.path.exists(p):
            with open(p, "r", encoding="utf-8") as f:
                out[s] = json.load(f)
    return out


def align_chains(ref, mag):
    """Trim every chain (in-place) to the bone names common to ALL loaded files of
    both sides, preserving order. Returns (common_chain_names, notes)."""
    notes = []
    all_files = list(ref.values()) + list(mag.values())
    if not all_files:
        return [], notes

    def chain_sets(side):
        s = [set(d["chains"].keys()) for d in side.values()]
        return set.intersection(*s) if s else set()

    common = sorted(chain_sets(ref) & chain_sets(mag)) if (ref and mag) else \
             sorted(chain_sets(ref) or chain_sets(mag))
    only_ref = sorted((chain_sets(ref) or set()) - set(common))
    only_mag = sorted((chain_sets(mag) or set()) - set(common))
    if only_ref:
        notes.append("chains only in ref (not compared): %s" % ", ".join(only_ref))
    if only_mag:
        notes.append("chains only in magica (not compared): %s" % ", ".join(only_mag))

    for c in common:
        bone_lists = [d["chains"][c]["bones"] for d in all_files if c in d["chains"]]
        common_bones = set(bone_lists[0])
        for bl in bone_lists[1:]:
            common_bones &= set(bl)
        trimmed_from = set()
        for side_name, side in (("ref", ref), ("magica", mag)):
            for d in side.values():
                ch = d["chains"].get(c)
                if ch is None:
                    continue
                if len(common_bones) == len(ch["bones"]):
                    continue
                idx = [i for i, b in enumerate(ch["bones"]) if b in common_bones]
                dropped = [b for b in ch["bones"] if b not in common_bones]
                trimmed_from.add("%s: dropped %s" % (side_name, ", ".join(dropped)))
                ch["bones"] = [ch["bones"][i] for i in idx]
                ch["frames"] = [[fr[i] for i in idx] for fr in ch["frames"]]
        if trimmed_from:
            notes.append("chain %s aligned to common %d-bone segment (%s..%s); %s"
                         % (c, len(common_bones),
                            min(common_bones, key=lambda b: bone_lists[0].index(b) if b in bone_lists[0] else 999),
                            max(common_bones, key=lambda b: bone_lists[0].index(b) if b in bone_lists[0] else -1),
                            "; ".join(sorted(trimmed_from))))
    return common, notes


def metrics_for(scens):
    """cloth_metrics.compute_metrics with a tolerated-missing-rest fallback: every
    scenario starts with the same 1.5 s settle from the authored rest pose, so if
    rest was not recorded we use the settle window of any recorded scenario as a
    pseudo-rest (restDroopDeg/settleTime then approximate, flagged 'restApprox')."""
    if not scens:
        return None
    if "rest" in scens:
        m = cm.compute_metrics(scens)
        m["restApprox"] = False
        return m
    base = scens[sorted(scens.keys())[0]]
    k0 = int(round(cm.SETTLE_END * base["fps"]))
    pseudo = {
        "scenario": "rest", "fps": base["fps"], "unitsPerMeter": base["unitsPerMeter"],
        "anchor": base["anchor"][:k0 + 1],
        "chains": {n: {"bones": ch["bones"], "frames": ch["frames"][:k0 + 1]}
                   for n, ch in base["chains"].items()},
    }
    scens2 = dict(scens)
    scens2["rest"] = pseudo
    m = cm.compute_metrics(scens2)
    m["restApprox"] = True
    return m


# ----------------------------------------------------------------------------------
def fmt(v, kind):
    if v is None:
        return "never" if kind in ("time",) else "n/a"
    if kind == "count":
        return "%d" % int(round(v))
    if kind == "timems":
        return "%.1f" % v
    if kind in ("amp", "norm"):
        return "%.4f" % v
    return "%.2f" % v


def judge(mref, mmag, kind, ref_chain_len, scen_ok, noise):
    """-> (verdict, relerr_str, tol_str). verdict in PASS/FAIL/SKIP/NOT-RUN."""
    if not scen_ok:
        return "NOT-RUN", "-", "-"
    if noise:
        return "SKIP", "-", "- (noise: chain not driven by this motion)"
    if mref is None and mmag is None:
        return "PASS", "-", "- (both: never)"
    if mref is None or mmag is None:
        return "FAIL", "-", "- (one side never settled/recovered)"
    floor = (0.02 / max(ref_chain_len, 1e-9)) if kind == "norm" else FLOOR[kind]
    tol = max(REL_TOL * abs(mref), floor)
    diff = abs(mmag - mref)
    rel = "inf" if abs(mref) < 1e-12 else "%+.0f%%" % ((mmag - mref) / abs(mref) * 100.0)
    return ("PASS" if diff <= tol else "FAIL"), rel, fmt(tol, kind)


def liveness(side):
    """Did the cloth solver actually write any bone? For each chain, the max movement
    of the TIP relative to the chain ROOT (m) across all frames of every scenario --
    a purely kinematic (rigid) skeleton scores ~0 even while the anchor is driven.
    Returns (per_chain_max, dead) where dead = every chain < 1 mm."""
    per = {}
    for sname, scn in side.items():
        upm = float(scn["unitsPerMeter"])
        for name, ch in scn["chains"].items():
            P = np.asarray(ch["frames"], dtype=float) / upm
            rel = P[:, -1, :] - P[:, 0, :]
            # rotation-invariant deformation: root->tip straight-line distance spread
            # (valid in every scenario; a rigid chain rotating with the head would fake
            # a large tip-rel-root translation in turn/spin, so that signal is only
            # trusted for the translation-driven scenarios)
            dist = np.linalg.norm(rel, axis=1)
            score = float(dist.max() - dist.min())
            if sname in ("rest", "walk"):
                score = max(score, float(np.linalg.norm(rel - rel[0], axis=1).max()))
            per[name] = max(per.get(name, 0.0), score)
    dead = bool(per) and all(v < 1e-3 for v in per.values())
    return per, dead


def drive_checks(ref, mag):
    """Sanity: did both sims drive the anchor the same way? Returns rows
    [(scenario, quantity, ref, magica, ok)]."""
    rows = []

    def anchor(scn):
        a = np.asarray(scn["anchor"], dtype=float)
        return a[:, :3] / float(scn["unitsPerMeter"]), a[:, 3:7]

    for s in SCENARIOS:
        if s not in ref or s not in mag:
            continue
        (pr, qr), (pm, qm) = anchor(ref[s]), anchor(mag[s])
        fps = ref[s]["fps"]
        k0 = int(round(cm.SETTLE_END * fps))
        if s == "rest":
            dr = float(np.linalg.norm(pr - pr[0], axis=1).max())
            dm = float(np.linalg.norm(pm - pm[0], axis=1).max())
            rows.append((s, "max anchor drift (m)", "%.4f" % dr, "%.4f" % dm,
                         dr < 0.01 and dm < 0.01))
        elif s == "turn":
            yr = abs(cm._yaw_deg(ref[s]["anchor"])[-1])
            ym = abs(cm._yaw_deg(mag[s]["anchor"])[-1])
            rows.append((s, "final |head yaw| (deg)", "%.1f" % yr, "%.1f" % ym,
                         abs(yr - 90) < 3 and abs(ym - 90) < 3))
        elif s == "walk":
            ke = min(int(round(cm.MOVE_END["walk"] * fps)), len(pr) - 1, len(pm) - 1)
            dr = float(np.linalg.norm(pr[ke, [0, 2]] - pr[k0, [0, 2]]))
            dm = float(np.linalg.norm(pm[ke, [0, 2]] - pm[k0, [0, 2]]))
            rows.append((s, "anchor travel (m, expect 2.4)", "%.2f" % dr, "%.2f" % dm,
                         abs(dr - 2.4) < 0.15 and abs(dm - 2.4) < 0.15))
        elif s == "spin":
            def swept(q):
                yaw = np.unwrap(np.radians(cm._yaw_deg(q)))
                ke_ = min(int(round(cm.MOVE_END["spin"] * fps)), len(yaw) - 1)
                return abs(math.degrees(yaw[ke_] - yaw[int(round(cm.SETTLE_END * fps))]))
            sr, sm = swept(ref[s]["anchor"]), swept(mag[s]["anchor"])
            rows.append((s, "swept |yaw| (deg, expect 360)", "%.0f" % sr, "%.0f" % sm,
                         abs(sr - 360) < 10 and abs(sm - 360) < 10))
    return rows


# ----------------------------------------------------------------------------------
def main():
    d = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
    ref = load_side(d, "ref")
    mag = load_side(d, "magica")

    not_run = [s for s in SCENARIOS if s not in ref or s not in mag]
    missing = ["%s_%s.json" % (p, s) for p, side in (("ref", ref), ("magica", mag))
               for s in SCENARIOS if s not in side]

    common_chains, align_notes = align_chains(ref, mag)
    live_ref, dead_ref = liveness(ref) if ref else ({}, False)
    live_mag, dead_mag = liveness(mag) if mag else ({}, False)
    met_ref = metrics_for(ref)
    met_mag = metrics_for(mag)

    lines = []          # console
    md = []             # report.md

    def emit(s=""):
        lines.append(s)

    emit("=" * 100)
    emit("MMD -> Magica cloth conversion comparison   (tolerance +/-%d%%, floors: 5deg / 0.15s / 2cm)" % int(REL_TOL * 100))
    emit("dir: %s" % d)
    emit("=" * 100)
    if missing:
        emit("MISSING FILES: " + ", ".join(missing))
    if not_run:
        emit("NOT-RUN SCENARIOS (absent on at least one side): " + ", ".join(not_run))
    else:
        emit("All 4 scenarios present on both sides.")
    for n in align_notes:
        emit("ALIGN: " + n)
    if met_ref and met_ref.get("restApprox"):
        emit("NOTE: ref rest missing -> rest metrics approximated from another scenario's settle window")
    if met_mag and met_mag.get("restApprox"):
        emit("NOTE: magica rest missing -> rest metrics approximated from another scenario's settle window")
    emit("unitsPerMeter: ref=%.4f  magica=%.4f (each file self-normalizes to meters)" %
         (met_ref["unitsPerMeter"] if met_ref else float("nan"),
          met_mag["unitsPerMeter"] if met_mag else float("nan")))
    emit()

    # ---- data-validity (solver liveness) ----
    def live_str(per):
        return "  ".join("%s=%.4fm" % (k, v) for k, v in sorted(per.items()))
    if live_ref:
        emit("solver liveness (max chain deformation, m)  ref:    " + live_str(live_ref))
    if live_mag:
        emit("solver liveness (max chain deformation, m)  magica: " + live_str(live_mag))
    if dead_ref:
        emit("!! REF DATA INVALID: every ref chain is rigid (<1 mm deformation) -- the reference solver never ran.")
    if dead_mag:
        emit("!! MAGICA DATA INVALID: every magica chain is rigid (<1 mm deformation over every scenario,")
        emit("   incl. the walk stop and the rest settle). The recording captured a KINEMATIC skeleton --")
        emit("   the Magica Cloth solver never wrote a single bone. All FAILs below share this one root cause;")
        emit("   ignore per-metric knob advice until the probe records a live simulation.")
    emit()

    # ---- drive checks ----
    dch = drive_checks(ref, mag)
    if dch:
        emit("-- anchor drive checks (are both sims performing the same scripted motion?) --")
        emit("%-6s %-32s %>10s %>10s %s".replace("%>", "%") % ("scen", "quantity", "ref", "magica", "ok"))
        for s, q, r, m, ok in dch:
            emit("%-6s %-32s %10s %10s %s" % (s, q, r, m, "OK" if ok else "** MISMATCH **"))
        emit()

    # ---- per-chain metric table ----
    fails = []   # (chain, metric, direction, ref, mag)
    n_pass = n_fail = n_skip = n_notrun = 0
    table_rows_md = []

    for c in common_chains:
        mr = met_ref["chains"].get(c, {}) if met_ref else {}
        mm = met_mag["chains"].get(c, {}) if met_mag else {}
        ref_len = mr.get("chainLenM") or 1e-9
        noise_gate = (mr.get("turnPeakAmpNorm") is not None
                      and mr.get("turnPeakAmpNorm") < NOISE_NORM)
        emit("== chain %s ==" % c)
        hdr = "%-18s %12s %12s %8s %10s   %s" % ("metric", "ref", "magica", "rel.err", "tol", "verdict")
        emit(hdr)
        emit("-" * len(hdr))
        for metric, kind, scen in METRIC_ORDER:
            scen_ok = (scen in ref) and (scen in mag)
            noise = noise_gate and metric in ("turnLagMs", "oscillations")
            vr, vm = mr.get(metric), mm.get(metric)
            verdict, rel, tol = judge(vr, vm, kind, ref_len, scen_ok, noise)
            if verdict == "PASS":
                n_pass += 1
            elif verdict == "FAIL":
                n_fail += 1
                direction = "high" if (vr is None or (vm is not None and vm > vr)) else "low"
                fails.append((c, metric, direction, vr, vm))
            elif verdict == "SKIP":
                n_skip += 1
            else:
                n_notrun += 1
            emit("%-18s %12s %12s %8s %10s   %s" %
                 (metric, fmt(vr, kind), fmt(vm, kind), rel, tol, verdict))
            table_rows_md.append((c, metric, fmt(vr, kind), fmt(vm, kind), rel, tol, verdict))
        emit()

    emit("SUMMARY: %d PASS, %d FAIL, %d SKIP(noise), %d NOT-RUN  (%d metrics x %d chains)" %
         (n_pass, n_fail, n_skip, n_notrun, len(METRIC_ORDER), len(common_chains)))
    emit()
    if fails:
        emit("-- suspect knobs (per failing metric; parameter references are MmdMagicaCloth.cs) --")
        if dead_mag:
            emit("* ROOT CAUSE FIRST -- MAGICA SIM NOT RUNNING: the recording is a rigid skeleton, so no")
            emit("    conversion parameter can explain the failures. Suspect the PROBE ENVIRONMENT, not the")
            emit("    conversion: MC2 simulation does not step in this batchmode test run (unity_diag.log:")
            emit("    a VANILLA MC2 BoneCloth also stays frozen -- '[probe-diag] f=60 moved=0.00000")
            emit("    IsPlaying=True teams=1 active=1'). Check: MagicaManager PlayerLoop injection /")
            emit("    Burst compilation in -batchmode, Time.captureDeltaTime actually reaching MC2's")
            emit("    TimeManager (diag showed dt=0.003-0.005 s, not 1/60), or run the probe in the GUI")
            emit("    editor (Test Runner > PlayMode > Sdo.Tests.MmdClothProbe).")
        for c, metric, direction, vr, vm in fails:
            key = (metric, direction)
            emit("* %s.%s: magica %s vs ref %s (%s)" % (c, metric, str(vm), str(vr), direction.upper()))
            emit("    " + KNOBS.get(key, "no knob mapping for this metric/direction"))
        emit()

    out = "\n".join(lines)
    print(out)

    # ------------------------------ report.md ------------------------------
    md.append("# MMD -> Magica cloth conversion validation report")
    md.append("")
    md.append("Comparator: `compare.py` over `ref_<scenario>.json` (pybullet ground truth) vs "
              "`magica_<scenario>.json` (Unity Magica Cloth 2 probe). All values meters/seconds/degrees; "
              "each file self-normalizes by its own `unitsPerMeter`.")
    md.append("")
    md.append("**Pass rule:** `|magica - ref| <= max(0.35*|ref|, floor)` with floors "
              "5 deg (angles), 0.15 s (times, 150 ms for turnLagMs), 0.02 m (amplitudes), "
              "2 (oscillation count), 0.02/chainLen (normalized amplitude).")
    md.append("")
    if dead_mag or dead_ref:
        md.append("## DATA VALIDITY -- READ FIRST")
        md.append("")
        if dead_mag:
            md.append("**The magica recordings are INVALID: every chain is perfectly rigid** (max deformation "
                      "%.6f m across all scenarios, vs ref up to %.2f m). The tips never move relative to their "
                      "roots -- not during the rest settle, not at the walk stop. The probe recorded a kinematic "
                      "skeleton; the Magica Cloth solver never wrote a bone. `unity_diag.log` independently "
                      "reproduces this with a **vanilla** MC2 BoneCloth in the same batchmode run "
                      "(`[probe-diag] f=60 moved=0.00000 IsPlaying=True teams=1 active=1`), so the cause is the "
                      "probe/environment (MC2 not stepping under `-batchmode -runTests`), **not** the "
                      "MmdMagicaCloth.cs conversion parameters. Every FAIL below shares this single root cause."
                      % (max(live_mag.values()) if live_mag else 0.0,
                         max(live_ref.values()) if live_ref else 0.0))
            md.append("")
        if dead_ref:
            md.append("**The ref recordings are INVALID: every chain is rigid** -- the reference solver never ran.")
            md.append("")
    if not_run:
        md.append("## NOT-RUN scenarios")
        md.append("")
        for s in not_run:
            md.append("- `%s` (missing: %s)" % (s, ", ".join(f for f in missing if f.endswith("_%s.json" % s))))
        md.append("")
    else:
        md.append("All 4 scenarios (rest / turn / walk / spin) recorded on both sides.")
        md.append("")
    if align_notes:
        md.append("## Chain alignment notes")
        md.append("")
        for n in align_notes:
            md.append("- " + n)
        md.append("")
    if dch:
        md.append("## Anchor drive checks")
        md.append("")
        md.append("| scenario | quantity | ref | magica | ok |")
        md.append("|---|---|---|---|---|")
        for s, q, r, m, ok in dch:
            md.append("| %s | %s | %s | %s | %s |" % (s, q, r, m, "OK" if ok else "**MISMATCH**"))
        md.append("")
    md.append("## Per-chain metrics")
    md.append("")
    cur = None
    for c, metric, r, m, rel, tol, verdict in table_rows_md:
        if c != cur:
            if cur is not None:
                md.append("")
            md.append("### %s" % c)
            md.append("")
            md.append("| metric | ref | magica | rel.err | tol | verdict |")
            md.append("|---|---|---|---|---|---|")
            cur = c
        v = verdict if verdict != "FAIL" else "**FAIL**"
        md.append("| %s | %s | %s | %s | %s | %s |" % (metric, r, m, rel, tol, v))
    md.append("")
    md.append("**Summary: %d PASS / %d FAIL / %d SKIP(noise) / %d NOT-RUN**" %
              (n_pass, n_fail, n_skip, n_notrun))
    md.append("")
    md.append("## Suspect knobs")
    md.append("")
    if not fails:
        md.append("No failing metrics -- no knobs to suspect.")
    else:
        if dead_mag:
            md.append("**Root cause first -- magica sim not running (see Data Validity above).** The rigid-"
                      "skeleton signature explains every FAIL at once: `settleTime=0` / `oscillations=0` "
                      "(nothing ever moves), `restDroopDeg` = the authored pose angle (chains keep their "
                      "styled shape), `walkStreamDeg` = `restDroopDeg` exactly (no streaming), `turnLagMs=0` "
                      "(tip rigidly bolted to the head), `spinFlingAmp~0` (no centrifugal lag). Fix the probe "
                      "environment before touching any conversion knob. Checklist: (1) does MC2 step under "
                      "`-batchmode -runTests`? A vanilla BoneCloth froze too (unity_diag.log) -- check "
                      "MagicaManager's PlayerLoop injection and Burst in batchmode; (2) `Time.captureDeltaTime` "
                      "vs MC2 TimeManager (diag dt was 0.003-0.005 s, not 1/60; MC2 SimulationFrequency showed "
                      "90, not the 150 set by MmdMagicaCloth -- was `SetSimulationFrequency` reached?); "
                      "(3) run the probe in the GUI editor: Test Runner > PlayMode > `Sdo.Tests.MmdClothProbe`. "
                      "The per-metric mappings below are kept for the NEXT run with a live sim.")
            md.append("")
        md.append("For each failing metric: the conversion parameter in `MmdMagicaCloth.cs` "
                  "(H:/65_remake-mmd/65/My project/Assets/Scripts/Game/MmdMagicaCloth.cs) that most "
                  "directly controls it, and the direction to move it.")
        md.append("")
        for c, metric, direction, vr, vm in fails:
            md.append("- **%s.%s** (magica %s vs ref %s, %s): %s" %
                      (c, metric, str(vm), str(vr), direction,
                       KNOBS.get((metric, direction), "no knob mapping")))
    md.append("")
    md.append("## Rerun")
    md.append("")
    md.append("```")
    md.append("python H:/65_remake-mmd/tools/mmd_cloth_validate/compare.py")
    md.append("```")
    md.append("(Regenerate the Unity side first if needed: "
              "`powershell -File H:/65_remake-mmd/tools/mmd_cloth_validate/run_magica_probe.ps1` "
              "with the editor closed, or Test Runner > PlayMode > Sdo.Tests.MmdClothProbe with it open.)")
    md.append("")

    rp = os.path.join(d, "report.md")
    with open(rp, "w", encoding="utf-8") as f:
        f.write("\n".join(md))
    print("report written: %s" % rp)
    return 1 if n_fail else 0


if __name__ == "__main__":
    sys.exit(main())
