# -*- coding: utf-8 -*-
"""Compute the shared-contract cloth metrics from <prefix>_<scenario>.json recordings.

Usage:  python compute_metrics_magica.py [prefix]     (default: magica)
Reads   <prefix>_rest.json / _turn.json / _walk.json / _spin.json from this directory
Writes  <prefix>_metrics.json

All metrics are in METERS (world positions / unitsPerMeter) and SECONDS, per the contract:
  restDroopDeg    angle of root->tip vs straight down (-Y) at the end of "rest"
  settleTimeSec   first time in "rest" after which tip speed stays < 0.05 m/s
  turnPeakAmpM    max lateral (XZ) tip displacement from its pre-turn position
  turnPeakAmpNorm same, normalized by chain rest length
  turnLagMs       time of tip peak minus time the head anchor reaches 90 deg yaw
  oscillations    tip-velocity sign flips along the main horizontal swing axis, turn end -> settle
  walkStreamDeg   max chain angle from vertical during the walk phase (1.5..3.5 s)
  walkRecoverySec after the stop (3.5 s), time until chain angle stays within 10 deg of its pre-walk droop
  spinFlingAmpM   max radial tip displacement (from the spin axis) vs pre-spin radius, spin start -> end of rec
"""
import json, math, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
SCENARIOS = ("rest", "turn", "walk", "spin")


def load(prefix, scen):
    path = os.path.join(HERE, "%s_%s.json" % (prefix, scen))
    if not os.path.exists(path):
        return None
    with open(path, "r", encoding="utf-8") as f:
        d = json.load(f)
    return resample_uniform(d)


def resample_uniform(d, fps=60.0):
    """Recordings made with REAL frame pacing carry a per-frame 'dt' array (variable time axis). Resample anchor +
    chain frames onto a uniform fps grid with linear interpolation so the metric code's (f+1)/fps axis is exact."""
    dts = d.get("dt")
    if not dts:
        return d
    # cumulative sample times: frame f is at t = sum(dt[0..f])
    times = []
    t = 0.0
    for dt in dts:
        t += dt
        times.append(t)
    total = times[-1]
    n_out = int(total * fps)

    def interp_series(frames, lerp):
        out = []
        j = 0
        for k in range(n_out):
            tk = (k + 1) / fps
            while j < len(times) - 1 and times[j] < tk:
                j += 1
            if j == 0:
                out.append(frames[0])
                continue
            t0, t1 = times[j - 1], times[j]
            a = 0.0 if t1 <= t0 else max(0.0, min(1.0, (tk - t0) / (t1 - t0)))
            out.append(lerp(frames[j - 1], frames[j], a))
        return out

    lerp_vec = lambda p, q, a: [p[i] + (q[i] - p[i]) * a for i in range(len(p))]
    lerp_frame = lambda p, q, a: [lerp_vec(p[b], q[b], a) for b in range(len(p))]
    d["anchor"] = interp_series(d["anchor"], lerp_vec)
    for ch in d["chains"].values():
        ch["frames"] = interp_series(ch["frames"], lerp_frame)
    d["fps"] = fps
    del d["dt"]
    return d


def v_sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def v_len(a): return math.sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2])


def droop_deg(root, tip):
    """Angle of root->tip vs straight down (0,-1,0), degrees."""
    d = v_sub(tip, root)
    n = v_len(d)
    if n < 1e-9:
        return 0.0
    c = max(-1.0, min(1.0, -d[1] / n))
    return math.degrees(math.acos(c))


def chain_len_m(frames0, upm):
    total = 0.0
    for i in range(1, len(frames0)):
        total += v_len(v_sub(frames0[i], frames0[i - 1]))
    return total / upm


def tip_speeds(tips, fps, upm):
    """Per-frame tip speed in m/s; index f = speed between frame f-1 and f (speeds[0] = 0)."""
    out = [0.0]
    for f in range(1, len(tips)):
        out.append(v_len(v_sub(tips[f], tips[f - 1])) * fps / upm)
    return out


def settle_time(speeds, fps, thresh=0.05):
    """First time after which speed stays < thresh. None if it never settles."""
    last_fast = -1
    for f, s in enumerate(speeds):
        if s >= thresh:
            last_fast = f
    if last_fast == len(speeds) - 1:
        return None
    return (last_fast + 1 + 1) / fps   # frame f samples t=(f+1)/fps


def yaw_deg(q):
    """Yaw of the anchor quaternion: heading of the rotated +Z axis (degrees, unwrapped by caller)."""
    x, y, z, w = q
    # rotate (0,0,1) by q
    vx = 2 * (x * z + w * y)
    vz = 1 - 2 * (x * x + y * y)
    return math.degrees(math.atan2(vx, vz))


def unwrap(seq):
    out = [seq[0]]
    for a in seq[1:]:
        prev = out[-1]
        while a - prev > 180.0: a -= 360.0
        while a - prev < -180.0: a += 360.0
        out.append(a)
    return out


def idx_at(t, fps, n):
    """Frame index whose sample time (f+1)/fps == t (clamped)."""
    return max(0, min(n - 1, int(round(t * fps)) - 1))


def main():
    prefix = sys.argv[1] if len(sys.argv) > 1 else "magica"
    data = {s: load(prefix, s) for s in SCENARIOS}
    have = [s for s in SCENARIOS if data[s]]
    if not have:
        print("no %s_*.json found in %s" % (prefix, HERE)); sys.exit(1)

    first = data[have[0]]
    upm = float(first["unitsPerMeter"])
    fps = float(first.get("fps", 60))
    chain_names = list(first["chains"].keys())
    out = {"sim": prefix, "unitsPerMeter": upm, "fps": fps, "scenarios": have, "chains": {}}

    for name in chain_names:
        m = {}
        rest_frames = data["rest"]["chains"][name]["frames"] if data.get("rest") else None
        base_frames = rest_frames or data[have[0]]["chains"][name]["frames"]
        clen = chain_len_m(base_frames[0], upm)
        m["chainLenM"] = round(clen, 5)

        # ---- rest ----
        if rest_frames:
            tips = [f[-1] for f in rest_frames]
            roots = [f[0] for f in rest_frames]
            m["restDroopDeg"] = round(droop_deg(roots[-1], tips[-1]), 3)
            st = settle_time(tip_speeds(tips, fps, upm), fps)
            m["settleTimeSec"] = None if st is None else round(st, 4)

        # ---- turn ----
        if data.get("turn"):
            fr = data["turn"]["chains"][name]["frames"]
            n = len(fr)
            tips = [f[-1] for f in fr]
            pre = idx_at(1.5, fps, n)
            lat = [math.hypot(tips[f][0] - tips[pre][0], tips[f][2] - tips[pre][2]) / upm
                   for f in range(n)]
            f_peak = max(range(pre, n), key=lambda f: lat[f])
            m["turnPeakAmpM"] = round(lat[f_peak], 5)
            m["turnPeakAmpNorm"] = round(lat[f_peak] / clen, 5) if clen > 1e-9 else None
            yaws = unwrap([yaw_deg(a[3:7]) for a in data["turn"]["anchor"]])
            y0 = yaws[pre]
            t90 = None
            for f in range(pre, n):
                if abs(yaws[f] - y0) >= 89.5:
                    t90 = (f + 1) / fps
                    break
            if t90 is None:
                t90 = 1.9   # nominal ramp end
            m["turnLagMs"] = round(((f_peak + 1) / fps - t90) * 1000.0, 1)
            # oscillations: turn end (1.9 s) -> settle (or end); PCA main horizontal axis; velocity sign flips
            f_end = idx_at(1.9, fps, n)
            speeds = tip_speeds(tips, fps, upm)
            st = settle_time(speeds, fps)
            f_settle = n - 1 if st is None else min(n - 1, idx_at(st, fps, n))
            f_settle = max(f_settle, f_end + 2)
            xs = [tips[f][0] / upm for f in range(f_end, f_settle + 1)]
            zs = [tips[f][2] / upm for f in range(f_end, f_settle + 1)]
            mx, mz = sum(xs) / len(xs), sum(zs) / len(zs)
            sxx = sum((x - mx) ** 2 for x in xs); szz = sum((z - mz) ** 2 for z in zs)
            sxz = sum((x - mx) * (z - mz) for x, z in zip(xs, zs))
            ang = 0.5 * math.atan2(2 * sxz, sxx - szz)   # principal axis of the horizontal swing
            ux, uz = math.cos(ang), math.sin(ang)
            flips, sign = 0, 0
            eps = 0.01   # m/s hysteresis
            for f in range(f_end + 1, f_settle + 1):
                v = ((tips[f][0] - tips[f - 1][0]) * ux + (tips[f][2] - tips[f - 1][2]) * uz) * fps / upm
                if abs(v) < eps:
                    continue
                s = 1 if v > 0 else -1
                if sign != 0 and s != sign:
                    flips += 1
                sign = s
            m["oscillations"] = flips

        # ---- walk ----
        if data.get("walk"):
            fr = data["walk"]["chains"][name]["frames"]
            n = len(fr)
            angles = [droop_deg(f[0], f[-1]) for f in fr]
            f0, f1 = idx_at(1.5, fps, n), idx_at(3.5, fps, n)
            m["walkStreamDeg"] = round(max(angles[f0:f1 + 1]), 3)
            ref = angles[f0]   # pre-walk droop (settled = rest droop)
            rec = None
            for f in range(f1, n):
                if all(abs(a - ref) <= 10.0 for a in angles[f:]):
                    rec = (f + 1) / fps - 3.5
                    break
            m["walkRecoverySec"] = None if rec is None else round(max(rec, 0.0), 4)

        # ---- spin ----
        if data.get("spin"):
            fr = data["spin"]["chains"][name]["frames"]
            n = len(fr)
            tips = [f[-1] for f in fr]
            anch = data["spin"]["anchor"]
            f0, f1 = idx_at(1.5, fps, n), idx_at(2.5, fps, n)
            # spin axis = centroid of the anchor's horizontal circle during the spin
            ax = sum(a[0] for a in anch[f0:f1 + 1]) / (f1 - f0 + 1)
            az = sum(a[2] for a in anch[f0:f1 + 1]) / (f1 - f0 + 1)
            r = [math.hypot(t[0] - ax, t[2] - az) / upm for t in tips]
            m["spinFlingAmpM"] = round(max(r[f0:]) - r[f0], 5)

        out["chains"][name] = m

    dst = os.path.join(HERE, "%s_metrics.json" % prefix)
    with open(dst, "w", encoding="utf-8") as f:
        json.dump(out, f, indent=2, ensure_ascii=False)
    print("wrote", dst)
    for name, m in out["chains"].items():
        print(" ", name, json.dumps(m, ensure_ascii=False))


if __name__ == "__main__":
    main()
