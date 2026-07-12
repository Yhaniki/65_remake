# -*- coding: utf-8 -*-
"""REUSABLE cloth-behavior metrics over the shared scenario-JSON contract.

Both the pybullet ground-truth sim (ref_<scenario>.json) and the Unity MagicaCloth
probe (magica_<scenario>.json) emit the same schema:

  { "scenario": "...", "fps": 60, "unitsPerMeter": <float>,
    "anchor":  [[x,y,z,qx,qy,qz,qw], ...per frame],
    "chains":  { "<name>": { "bones": [...], "frames": [[[x,y,z]..per bone]..per frame] } } }

All metrics are METERS / SECONDS / DEGREES: positions are divided by unitsPerMeter.

Canonical timeline (shared contract — 1.5 s settle before every motion):
  rest: no motion, 4.0 s
  turn: head +90deg yaw over 0.4 s (motion 1.5..1.9 s), hold 2 s
  walk: whole model +Z at 1.2 m/s for 2 s (1.5..3.5 s), stop, hold 2 s
  spin: whole model 360deg about +Y over 1 s (1.5..2.5 s), hold 2 s

Entry points:
  compute_metrics(scenarios)      scenarios = {"rest": <json dict>, "turn": ..,
                                               "walk": .., "spin": ..}
  compute_metrics_from_dir(d, prefix)   loads <d>/<prefix>_<scenario>.json
CLI: python cloth_metrics.py <dir> <prefix>       (e.g.  . ref   /  . magica)
"""
import json
import math
import os
import sys

import numpy as np

SETTLE_END = 1.5
MOVE_END = {"turn": 1.9, "walk": 3.5, "spin": 2.5}
TIP_SETTLED_MPS = 0.05      # tip counts as settled below this speed
OSC_DEADBAND_MPS = 0.02     # velocity dead-band for oscillation counting
RECOVERY_DEG = 10.0         # walk recovery: within this many degrees of rest droop
RECOVERY_HOLD_S = 0.5       # ...and staying within the band at least this long


def _chain_pos(scn, chain):
    """(frames, bones, 3) in METERS."""
    upm = float(scn["unitsPerMeter"])
    return np.asarray(scn["chains"][chain]["frames"], dtype=float) / upm


def _droop_deg(P):
    """Angle of root->tip vs straight down (-Y), per frame. P: (F,B,3)."""
    v = P[:, -1, :] - P[:, 0, :]
    horiz = np.hypot(v[:, 0], v[:, 2])
    return np.degrees(np.arctan2(horiz, -v[:, 1]))


def _tip_speed(P, fps):
    """Tip speed per frame interval (m/s), length F-1."""
    d = np.diff(P[:, -1, :], axis=0)
    return np.linalg.norm(d, axis=1) * fps


def _sustained_below(series, thresh, fps, start_frame=0):
    """First time t (s) such that series[k] < thresh for ALL k >= t*fps.
    Returns None if never sustained."""
    ok = np.asarray(series) < thresh
    n = len(ok)
    suffix = np.logical_and.accumulate(ok[::-1])[::-1]   # suffix[k] = all(ok[k:])
    cand = np.nonzero(suffix)[0]
    cand = cand[cand >= start_frame]
    if len(cand) == 0:
        return None
    return int(cand[0]) / float(fps)


def _yaw_deg(anchor):
    """Yaw (deg) about +Y from the anchor quaternion (valid for pure-Y rotations)."""
    a = np.asarray(anchor, dtype=float)
    qy, qw = a[:, 4], a[:, 6]
    return np.degrees(2.0 * np.arctan2(qy, qw))


def chain_length_m(scn, chain):
    P = _chain_pos(scn, chain)
    seg = np.diff(P[0], axis=0)
    return float(np.linalg.norm(seg, axis=1).sum())


def compute_metrics(scenarios):
    """scenarios: dict with keys rest/turn/walk/spin -> parsed contract JSON.
    Returns {"unitsPerMeter":..,"fps":..,"chains":{name:{metric:value}}}."""
    rest = scenarios["rest"]
    fps = int(rest["fps"])
    chains = list(rest["chains"].keys())
    out = {"unitsPerMeter": float(rest["unitsPerMeter"]), "fps": fps, "chains": {}}

    for c in chains:
        m = {}
        # ---------------- rest ----------------
        P = _chain_pos(rest, c)
        m["chainLenM"] = round(chain_length_m(rest, c), 4)
        droop_rest = _droop_deg(P)
        m["restDroopDeg"] = round(float(droop_rest[-1]), 2)
        st = _sustained_below(_tip_speed(P, fps), TIP_SETTLED_MPS, fps)
        m["settleTime"] = None if st is None else round(st, 3)

        # ---------------- turn ----------------
        if "turn" in scenarios:
            scn = scenarios["turn"]
            P = _chain_pos(scn, c)
            k0 = int(round(SETTLE_END * fps))
            pre = P[k0, -1, :]
            lat = np.hypot(P[k0:, -1, 0] - pre[0], P[k0:, -1, 2] - pre[2])
            m["turnPeakAmp"] = round(float(lat.max()), 4)
            clen = chain_length_m(scn, c)
            m["turnPeakAmpNorm"] = round(float(lat.max()) / clen, 4) if clen > 0 else None
            # lag uses the FIRST SWING PEAK (first local max >= 80% of global):
            # a heavily damped chain settles asymptotically toward an equilibrium
            # that can exceed the first swing peak, which would push a plain
            # argmax to the final frame
            gmax = lat.max()
            k_rel = int(lat.argmax())
            for k in range(1, len(lat) - 1):
                if lat[k] >= lat[k - 1] and lat[k] >= lat[k + 1] and lat[k] >= 0.8 * gmax:
                    k_rel = k
                    break
            k_peak = k0 + k_rel
            yaw = _yaw_deg(scn["anchor"])
            hit = np.nonzero(yaw >= 89.9)[0]
            if len(hit):
                m["turnLagMs"] = round((k_peak - int(hit[0])) / float(fps) * 1000.0, 1)
            else:
                m["turnLagMs"] = None
            # oscillations: tip-velocity sign flips on the main swing axis,
            # between turn end and settle (or end of recording)
            ke = int(round(MOVE_END["turn"] * fps))
            spd = _tip_speed(P, fps)
            st2 = _sustained_below(spd, TIP_SETTLED_MPS, fps, start_frame=ke)
            kset = len(P) - 1 if st2 is None else int(round(st2 * fps))
            seg = P[ke:kset + 1, -1, :]
            if len(seg) > 2:
                var = seg.var(axis=0)
                axis = 0 if var[0] >= var[2] else 2      # main horizontal swing axis
                v = np.diff(seg[:, axis]) * fps
                flips, last = 0, 0
                for x in v:
                    if abs(x) < OSC_DEADBAND_MPS:
                        continue
                    s = 1 if x > 0 else -1
                    if last != 0 and s != last:
                        flips += 1
                    last = s
                m["oscillations"] = flips
            else:
                m["oscillations"] = 0

        # ---------------- walk ----------------
        if "walk" in scenarios:
            scn = scenarios["walk"]
            P = _chain_pos(scn, c)
            droop = _droop_deg(P)
            k0 = int(round(SETTLE_END * fps))
            ke = int(round(MOVE_END["walk"] * fps))
            m["walkStreamDeg"] = round(float(droop[k0:ke + 1].max()), 2)
            ref = m["restDroopDeg"]
            within = np.abs(droop - ref) <= RECOVERY_DEG
            # recovered = droop stays inside the band for >= RECOVERY_HOLD_S
            # (a decaying oscillator may exit the band briefly much later; a
            # sustained-forever rule would spuriously return None)
            hold = int(round(RECOVERY_HOLD_S * fps))
            rec = None
            for k in range(ke, len(within)):
                end = min(k + hold, len(within))
                if within[k:end].all():
                    rec = k / float(fps)
                    break
            m["walkRecoverySec"] = None if rec is None else round(rec - MOVE_END["walk"], 3)

        # ---------------- spin ----------------
        if "spin" in scenarios:
            scn = scenarios["spin"]
            P = _chain_pos(scn, c)
            k0 = int(round(SETTLE_END * fps))
            ke = min(int(round(MOVE_END["spin"] * fps)), len(P) - 1)
            # spin axis: the anchor sweeps a full circle during the spin phase, so
            # the mean of its XZ track is the axis (models may sit anywhere in
            # world space — do NOT assume the origin)
            a = np.asarray(scn["anchor"], dtype=float)[:, :3] / float(scn["unitsPerMeter"])
            ax = a[k0:ke + 1, 0].mean(); az = a[k0:ke + 1, 2].mean()
            radial = np.hypot(P[:, -1, 0] - ax, P[:, -1, 2] - az)
            m["spinFlingAmp"] = round(float((radial[k0:ke + 1] - radial[k0]).max()), 4)

        out["chains"][c] = m
    return out


def compute_metrics_from_dir(d, prefix):
    scen = {}
    for s in ("rest", "turn", "walk", "spin"):
        path = os.path.join(d, "%s_%s.json" % (prefix, s))
        if os.path.exists(path):
            with open(path, "r", encoding="utf-8") as f:
                scen[s] = json.load(f)
    if "rest" not in scen:
        raise FileNotFoundError("need at least %s_rest.json in %s" % (prefix, d))
    return compute_metrics(scen)


if __name__ == "__main__":
    d = sys.argv[1] if len(sys.argv) > 1 else "."
    prefix = sys.argv[2] if len(sys.argv) > 2 else "ref"
    print(json.dumps(compute_metrics_from_dir(d, prefix), indent=2))
