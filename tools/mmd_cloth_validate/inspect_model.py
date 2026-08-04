# -*- coding: utf-8 -*-
"""One-off analysis of the physics section to design the ref sim (not part of the pipeline)."""
import sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")
import pmx_parse

p = pmx_parse.load(r"H:/65_remake/assets/IkaHatunemiku2025/Ika-HatsuneMiku 2025-JP.Pmx")

print("height units:", p.vert_max_y - p.vert_min_y, "unitsPerMeter:", (p.vert_max_y - p.vert_min_y) / 1.6)

# bones of interest
for i, b in enumerate(p.bones):
    if b.name_jp in (u"頭", u"下半身", u"全ての親", u"センター", u"上半身", u"首", u"上半身2"):
        par = p.bones[b.parent].name_jp if b.parent >= 0 else "-"
        print("bone %3d %-8s pos=%s parent=%s" % (i, b.name_jp, ["%.3f" % v for v in b.position], par))

# joint prefix census
from collections import Counter
import re
c = Counter()
for j in p.joints:
    c[re.sub(r"[0-9]+(_[0-9]+)?$", "", j.name_jp)] += 1
print("--- joint prefixes ---")
for k, v in sorted(c.items(), key=lambda kv: -kv[1]):
    print("%4d %s" % (v, k))

def bname(rb):
    return p.bones[rb.bone].name_jp if 0 <= rb.bone < len(p.bones) else "-"

# root joints: A = kinematic body
print("--- root joints (A kinematic) for chains of interest ---")
for j in p.joints:
    if 0 <= j.rb_a < len(p.rigid_bodies) and p.rigid_bodies[j.rb_a].mode == 0:
        b = p.rigid_bodies[j.rb_b]
        if any(b.name_jp.startswith(px) for px in ("RightTwicHairA", "BangHair", "Tie_", "Dress_")):
            a = p.rigid_bodies[j.rb_a]
            print("J %-24s A=%3d(%s bone=%s) -> B=%3d(%s) rotLo=%s rotHi=%s spr=%s" %
                  (j.name_jp, j.rb_a, a.name_jp, bname(a), j.rb_b, b.name_jp,
                   ["%.2f" % v for v in j.rot_lo], ["%.2f" % v for v in j.rot_hi],
                   ["%.1f" % v for v in j.rot_spring]))

# twintail col-1 chain joints
print("--- RightTwicHairA joints touching col chain ---")
for j in p.joints:
    if "RightTwicHairA" in j.name_jp:
        a = p.rigid_bodies[j.rb_a] if j.rb_a >= 0 else None
        b = p.rigid_bodies[j.rb_b] if j.rb_b >= 0 else None
        an = a.name_jp if a else "-"
        bn = b.name_jp if b else "-"
        if "_1" == an[-2:] or "_1" == bn[-2:] or True:
            pass
print("total twic joints:", sum(1 for j in p.joints if "TwicHairA" in j.name_jp))
tw = [j for j in p.joints if "RightTwicHairA" in j.name_jp]
for j in tw[:8] + tw[-4:]:
    a = p.rigid_bodies[j.rb_a]
    b = p.rigid_bodies[j.rb_b]
    print("J %-26s %-22s -> %-22s rotLo=%s rotHi=%s spr=%s posSpr=%s" %
          (j.name_jp, a.name_jp, b.name_jp,
           ["%.2f" % v for v in j.rot_lo], ["%.2f" % v for v in j.rot_hi],
           ["%.1f" % v for v in j.rot_spring], ["%.1f" % v for v in j.pos_spring]))

# any joints with nonzero linear limits or linear springs?
nlin = [j for j in p.joints if any(abs(v) > 1e-9 for v in j.pos_lo + j.pos_hi)]
nspr = [j for j in p.joints if any(abs(v) > 1e-9 for v in j.pos_spring)]
nrspr = [j for j in p.joints if any(abs(v) > 1e-9 for v in j.rot_spring)]
print("joints with nonzero linear limits:", len(nlin), " linear springs:", len(nspr), " rot springs:", len(nrspr))
for j in nlin[:6]:
    print("  linlim", j.name_jp, j.pos_lo, j.pos_hi)
for j in nrspr[:10]:
    print("  rotspr", j.name_jp, j.rot_spring, "rotLo", j.rot_lo, "rotHi", j.rot_hi)

# tie bodies params
print("--- Tie bodies ---")
for rb in p.rigid_bodies:
    if rb.name_jp.startswith("Tie_"):
        print("%3d %-8s m=%.3f ld=%.2f ad=%.2f shape=%d size=%s mode=%d grp=%d mask=%04x" %
              (rb.index, rb.name_jp, rb.mass, rb.linear_damp, rb.angular_damp, rb.shape,
               ["%.2f" % v for v in rb.size], rb.mode, rb.group, rb.mask))

# bang chains
print("--- BangHairA bodies + joints ---")
for rb in p.rigid_bodies:
    if rb.name_jp.startswith("BangHairA"):
        print("%3d %-14s m=%.3f ld=%.2f ad=%.2f mode=%d bone=%s" %
              (rb.index, rb.name_jp, rb.mass, rb.linear_damp, rb.angular_damp, rb.mode, bname(rb)))
for j in p.joints:
    if "BangHairA" in j.name_jp:
        a = p.rigid_bodies[j.rb_a]; b = p.rigid_bodies[j.rb_b]
        print("J %-16s %-16s -> %-16s rotLo=%s rotHi=%s spr=%s" %
              (j.name_jp, a.name_jp, b.name_jp, ["%.2f" % v for v in j.rot_lo],
               ["%.2f" % v for v in j.rot_hi], ["%.1f" % v for v in j.rot_spring]))

# mode census
from collections import Counter as C2
print("mode census:", C2(rb.mode for rb in p.rigid_bodies))
# which bones do kinematic anchors reference for our chains
