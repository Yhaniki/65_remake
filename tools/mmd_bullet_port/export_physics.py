# -*- coding: utf-8 -*-
"""Dump a PMX's physics section (rigid bodies + joints + the recorded chains) to one JSON,
so the C# port can be developed and checked against ref_*.json WITHOUT Unity in the loop.

The C# solver reads exactly this file; the Unity side will feed it the same numbers straight
from PmxLoader. Keeping the two inputs identical is the point — the port is verified here,
then wired into the game unchanged.

  python export_physics.py [out.json]
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "mmd_cloth_validate"))
import pmx_parse  # noqa: E402

# the model lives in the MAIN worktree (assets/ is not checked out in every worktree)
PMX = os.environ.get("SDO_PMX") or r"H:/65_remake/assets/MODEL/IkaHatunemiku2025/Ika-HatsuneMiku 2025-JP.Pmx"

# same four chains the reference sim records (root -> tip), by rigid-body name
CHAINS = {
    "RightTwicHairA": ["RightTwicHairA_%d_1" % r for r in range(30)],
    "BangHairA": ["BangHairA_%02d" % r for r in range(3)],
    "Tie": ["Tie_%d_1" % r for r in range(20)],
    "Dress_5": ["Dress_%d_5" % r for r in range(11)],
}


def main():
    out_path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "ika_physics.json")
    pmx = pmx_parse.load(PMX)

    bodies = []
    for rb in pmx.rigid_bodies:
        bone = pmx.bones[rb.bone] if 0 <= rb.bone < len(pmx.bones) else None
        bodies.append({
            "name": rb.name_jp,
            "bone": rb.bone,
            "bonePos": list(bone.position) if bone else [0.0, 0.0, 0.0],
            "group": rb.group, "mask": rb.mask,
            "shape": rb.shape, "size": list(rb.size),
            "pos": list(rb.position), "rot": list(rb.rotation),
            "mass": rb.mass, "linDamp": rb.linear_damp, "angDamp": rb.angular_damp,
            "restitution": rb.restitution, "friction": rb.friction, "mode": rb.mode,
        })

    joints = []
    for j in pmx.joints:
        joints.append({
            "a": j.rb_a, "b": j.rb_b,
            "pos": list(j.position), "rot": list(j.rotation),
            "posLo": list(j.pos_lo), "posHi": list(j.pos_hi),
            "rotLo": list(j.rot_lo), "rotHi": list(j.rot_hi),
            "posSpring": list(j.pos_spring), "rotSpring": list(j.rot_spring),
        })

    by_name = {rb.name_jp: i for i, rb in enumerate(pmx.rigid_bodies)}
    chains = {}
    for cname, names in CHAINS.items():
        idx = [by_name[n] for n in names if n in by_name]
        if len(idx) != len(names):
            print("  ! chain %s: %d/%d bodies found" % (cname, len(idx), len(names)))
        chains[cname] = idx

    ys = [b.position[1] for b in pmx.bones]
    doc = {
        "model": os.path.basename(PMX),
        "unitsPerMeter": (pmx.vert_max_y - pmx.vert_min_y) / 1.6,
        "boneMinY": min(ys), "boneMaxY": max(ys),
        "bodies": bodies, "joints": joints, "chains": chains,
    }
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, separators=(",", ":"))
    dyn = sum(1 for b in bodies if b["mode"] != 0)
    print("wrote %s — %d bodies (%d dynamic / %d kinematic), %d joints, %d chains"
          % (out_path, len(bodies), dyn, len(bodies) - dyn, len(joints), len(chains)))


if __name__ == "__main__":
    main()
