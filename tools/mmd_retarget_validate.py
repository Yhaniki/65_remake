# -*- coding: utf-8 -*-
"""離線重跑 MmdAvatar 的 retarget,量「MMD 的腳有沒有踩在動作原本踩的地方」。

為什麼要有這支:腳會抖、會滑,在畫面上只看得出「怪」,看不出是哪一級出問題。這支把 SDO 的 .MOT
餵給 HRC 骨架跑 FK,再照 MmdAvatar.LateUpdate 的規則(aim + twist + 世界差量 + root 平移 + 腳部 IK)
把同一段動作套到 .pmx 上,然後把兩邊的腳擺在一起量。判準是數字,不是看起來順不順眼。

四個指標(都以 SDO 那份動作為基準):
  planted-foot slide  支撐腳(該幀比較低的那隻)的水平移動總量。1.00x = 跟原動作滑一樣多。
  ankle jerk          腳踝位置的二階差分 —— 高頻抖動。1.00x = 跟原動作一樣穩。
  ankle height error  腳踝高度差,單位是 HRC 的長度單位(女角身高約 52.9)。
  sole rotation error 「MMD 腳掌相對自己 rest 的旋轉」對上「SDO 腳掌相對 bind 的旋轉」差幾度。
                      兩具骨架的 rest 都是站直腳底貼地,所以這個差就是腳底歪掉的角度。

用法:
  python tools/mmd_retarget_validate.py                                   # 預設 W_005663 + 初音 Ika
  python tools/mmd_retarget_validate.py --mot ...\\W_005663.MOT --pmx ...\\miku.pmx
  python tools/mmd_retarget_validate.py --compare                         # 修改前/後逐項對照

--mode 可以單獨關掉某條規則來做 A/B(對照 MmdRetargetPlan 的規則①②③與 MmdFootIk):
  --no-root-fix    センター 吃回 SDO Bip01 的世界旋轉(改壞前的行為)
  --ankle follow   足首 跟著小腿(改壞前的行為) / aim(瞄 つま先,曾經試過的錯方向) / delta(現行)
  --no-ik          關掉腳部 IK

相依:H:/bms/tools 的 bms_sdo(讀 .MOT/.HRC)與 tools/mmd_cloth_validate/pmx_parse(讀 .pmx)。
"""
from __future__ import annotations

import argparse
import math
import os
import sys

import numpy as np

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, r"H:\bms\tools")
sys.path.insert(0, os.path.join(REPO, "tools", "mmd_cloth_validate"))

from bms_sdo.mot_player import (build_skeleton, compose_local, guess_hrc,  # noqa: E402
                                interp_keys, read_hrc, read_mot)
import pmx_parse  # noqa: E402

DEFAULT_MOT = os.path.join(REPO, r"assets\sdox_offline\Extracted\AUMOTION\W_005663.MOT")
DEFAULT_PMX = os.path.join(REPO, r"assets\MODEL\IkaHatunemiku2025\Ika-HatsuneMiku 2025-JP.Pmx")

# MmdBoneMap.ToBip01 的逐字複本(見 65/My project/Assets/Scripts/Game/MmdBoneMap.cs)。
BONE_MAP = {
    "センター": "Bip01", "下半身": "Bip01_Pelvis", "上半身": "Bip01_Spine", "上半身2": "Bip01_Spine1",
    "首": "Bip01_Neck", "頭": "Bip01_Head",
    "左肩": "Bip01_L_Clavicle", "左腕": "Bip01_L_UpperArm", "左ひじ": "Bip01_L_Forearm", "左手首": "Bip01_L_Hand",
    "右肩": "Bip01_R_Clavicle", "右腕": "Bip01_R_UpperArm", "右ひじ": "Bip01_R_Forearm", "右手首": "Bip01_R_Hand",
    "左足": "Bip01_L_Thigh", "左ひざ": "Bip01_L_Calf", "左足首": "Bip01_L_Foot",
    "右足": "Bip01_R_Thigh", "右ひざ": "Bip01_R_Calf", "右足首": "Bip01_R_Foot",
}
for _s, _S in (("左", "L"), ("右", "R")):
    for _jp, _en in (("親指", "Finger0"), ("人指", "Finger1"), ("中指", "Finger2"), ("薬指", "Finger3"), ("小指", "Finger4")):
        _a, _b, _c = ("０", "１", "２") if _jp == "親指" else ("１", "２", "３")
        BONE_MAP[f"{_s}{_jp}{_a}"] = f"Bip01_{_S}_{_en}"
        BONE_MAP[f"{_s}{_jp}{_b}"] = f"Bip01_{_S}_{_en}1"
        BONE_MAP[f"{_s}{_jp}{_c}"] = f"Bip01_{_S}_{_en}2"

# MmdBoneMap.AimChild 的逐字複本 —— aim 只沿同一條語意骨鏈前進。不能拿「HRC 的第一個 mapped child」猜:
# FEMALE.HRC 的 children 依檔案順序,Spine 先列兩條腿才列 Spine1、Neck 先列兩邊肩膀才列 Head。
AIM_CHILD = {
    "Bip01": "Bip01_Pelvis",
    "Bip01_Pelvis": "Bip01_Spine",
    "Bip01_Spine": "Bip01_Spine1",
    "Bip01_Spine1": "Bip01_Neck",
    "Bip01_Neck": "Bip01_Head",
}
for _S in ("L", "R"):
    AIM_CHILD[f"Bip01_{_S}_Clavicle"] = f"Bip01_{_S}_UpperArm"
    AIM_CHILD[f"Bip01_{_S}_UpperArm"] = f"Bip01_{_S}_Forearm"
    AIM_CHILD[f"Bip01_{_S}_Forearm"] = f"Bip01_{_S}_Hand"
    AIM_CHILD[f"Bip01_{_S}_Thigh"] = f"Bip01_{_S}_Calf"
    AIM_CHILD[f"Bip01_{_S}_Calf"] = f"Bip01_{_S}_Foot"
    for _f in ("Finger0", "Finger1", "Finger2", "Finger3", "Finger4"):
        AIM_CHILD[f"Bip01_{_S}_{_f}"] = f"Bip01_{_S}_{_f}1"
        AIM_CHILD[f"Bip01_{_S}_{_f}1"] = f"Bip01_{_S}_{_f}2"

# --------------------------------------------------------------------------- 四元數(Unity 慣例 x,y,z,w)


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return np.array([aw*bx + ax*bw + ay*bz - az*by,
                     aw*by - ax*bz + ay*bw + az*bx,
                     aw*bz + ax*by - ay*bx + az*bw,
                     aw*bw - ax*bx - ay*by - az*bz])


def qconj(q):
    return np.array([-q[0], -q[1], -q[2], q[3]])


def qrot(q, v):
    u, s = q[:3], q[3]
    return 2.0*np.dot(u, v)*u + (s*s - np.dot(u, u))*v + 2.0*s*np.cross(u, v)


def mat_to_quat(m):
    m = np.asarray(m, dtype=np.float64)[:3, :3].copy()
    for c in range(3):
        n = np.linalg.norm(m[:, c])
        if n > 1e-12:
            m[:, c] /= n
    t = m[0, 0] + m[1, 1] + m[2, 2]
    if t > 0:
        s = math.sqrt(t + 1.0) * 2
        return np.array([(m[2, 1]-m[1, 2])/s, (m[0, 2]-m[2, 0])/s, (m[1, 0]-m[0, 1])/s, 0.25*s])
    if m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        return np.array([0.25*s, (m[0, 1]+m[1, 0])/s, (m[0, 2]+m[2, 0])/s, (m[2, 1]-m[1, 2])/s])
    if m[1, 1] > m[2, 2]:
        s = math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        return np.array([(m[0, 1]+m[1, 0])/s, 0.25*s, (m[1, 2]+m[2, 1])/s, (m[0, 2]-m[2, 0])/s])
    s = math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
    return np.array([(m[0, 2]+m[2, 0])/s, (m[1, 2]+m[2, 1])/s, 0.25*s, (m[1, 0]-m[0, 1])/s])


def from_to(a, b):
    """Quaternion.FromToRotation。"""
    a = a/np.linalg.norm(a)
    b = b/np.linalg.norm(b)
    d = float(np.clip(np.dot(a, b), -1, 1))
    if d > 1 - 1e-9:
        return np.array([0., 0., 0., 1.])
    if d < -1 + 1e-9:
        axis = np.cross(a, [1., 0., 0.])
        if np.linalg.norm(axis) < 1e-6:
            axis = np.cross(a, [0., 1., 0.])
        axis /= np.linalg.norm(axis)
        return np.array([axis[0], axis[1], axis[2], 0.0])
    axis = np.cross(a, b)
    s = math.sqrt((1+d)*2)
    return np.array([axis[0]/s, axis[1]/s, axis[2]/s, s*0.5])


def angle_axis(deg, axis):
    r = math.radians(deg)*0.5
    a = axis/np.linalg.norm(axis)
    return np.array([a[0]*math.sin(r), a[1]*math.sin(r), a[2]*math.sin(r), math.cos(r)])


def signed_angle_y(a, b):
    a = a/np.linalg.norm(a)
    b = b/np.linalg.norm(b)
    ang = math.degrees(math.acos(float(np.clip(np.dot(a, b), -1, 1))))
    return ang * (1.0 if np.dot([0., 1., 0.], np.cross(a, b)) >= 0 else -1.0)


def twist_about(q, axis):
    """MmdAvatar.TwistAbout —— swing-twist 分解取繞 axis 的那一半。"""
    d = float(np.dot(q[:3], axis))
    t = np.array([axis[0]*d, axis[1]*d, axis[2]*d, q[3]])
    n = np.linalg.norm(t)
    return np.array([0., 0., 0., 1.]) if n < 1e-6 else t/n


def ik_solve(hip, target, knee_hint, a, b):
    """MmdFootIk.Solve 的逐字對應。回傳 (thighDir, kneePos) 或 None。"""
    if not (a > 1e-5 and b > 1e-5):
        return None
    to = target - hip
    d = float(np.linalg.norm(to))
    if d < 1e-5:
        return None
    dir_t = to/d
    if d >= a + b - 1e-5:
        return dir_t, hip + dir_t*a
    if d <= abs(a - b) + 1e-5:
        return None
    cos_a = float(np.clip((a*a + d*d - b*b)/(2*a*d), -1, 1))
    axis = np.cross(dir_t, knee_hint - hip)
    for fallback in ([0., 0., 1.], [1., 0., 0.]):
        if float(np.dot(axis, axis)) >= 1e-10:
            break
        axis = np.cross(dir_t, np.array(fallback))
    if float(np.dot(axis, axis)) < 1e-10:
        return None
    td = qrot(angle_axis(math.degrees(math.acos(cos_a)), axis), dir_t)
    td /= np.linalg.norm(td)
    return td, hip + td*a


# --------------------------------------------------------------------------- retarget 重演


class Rig:
    def __init__(self, mot_path, pmx_path, root_fix=True, ankle="delta", ik=True):
        self.root_fix, self.ankle_mode, self.ik = root_fix, ankle, ik

        self.nodes = read_mot(mot_path)
        self.skel = build_skeleton(read_hrc(guess_hrc(mot_path)))
        self.by_id = {n["bone_id"]: n for n in self.nodes}
        self.hname = {b["name"]: i for i, b in enumerate(self.skel)}

        tmax = 0.0
        for n in self.nodes:
            for k in ("rot", "scale", "pos"):
                if len(n[k]):
                    tmax = max(tmax, float(n[k][:, -1].max()))
        self.frames = int(round(tmax)) + 1

        self.bind = [None]*len(self.skel)
        for i, bn in enumerate(self.skel):
            p = bn["parent"]
            self.bind[i] = bn["rest_mat"] if p < 0 else self.bind[p] @ bn["rest_mat"]

        pmx = pmx_parse.load(pmx_path)
        self.mname = {}
        for i, b in enumerate(pmx.bones):
            self.mname.setdefault(b.name_jp, i)
        self.bc = len(pmx.bones)
        self.mpos = np.array([b.position for b in pmx.bones], dtype=np.float64)
        self.mpar = [b.parent if 0 <= b.parent < self.bc else -1 for b in pmx.bones]
        self.rest_local = np.array([self.mpos[i] - (self.mpos[self.mpar[i]] if self.mpar[i] >= 0 else 0)
                                    for i in range(self.bc)])

        bind_y = np.array([self.bind[i][1, 3] for i in range(len(self.skel))])
        self.feet_y = float(bind_y.min())
        self.body_h = float(bind_y.max() - bind_y.min())
        self.mmd_min_y = pmx.vert_min_y
        self.unit = self.body_h / max(pmx.vert_max_y - pmx.vert_min_y, 1e-3)
        self.offset = np.array([0.0, self.feet_y - self.mmd_min_y*self.unit, 0.0])

        hr = np.array(self.bind[self.hname["Bip01_R_UpperArm"]][:3, 3]) - np.array(self.bind[self.hname["Bip01_L_UpperArm"]][:3, 3])
        mr = self.mpos[self.mname["右腕"]] - self.mpos[self.mname["左腕"]]
        hr[1] = mr[1] = 0
        self.qroot = angle_axis(signed_angle_y(mr, hr), np.array([0., 1., 0.]))
        self.qrooti = qconj(self.qroot)

        self._wire(ankle)
        self._legs()

    # -- MmdRetargetPlan.Build ------------------------------------------------
    def _wire(self, ankle):
        bmap = dict(BONE_MAP)
        aim_child = dict(AIM_CHILD)
        if ankle == "aim":   # 曾經試過的錯方向,留著做 A/B
            bmap.update({"左つま先": "Bip01_L_Toe0", "右つま先": "Bip01_R_Toe0"})
            aim_child.update({"Bip01_L_Foot": "Bip01_L_Toe0", "Bip01_R_Foot": "Bip01_R_Toe0"})
        self.hrc_i = [-1]*self.bc
        self.rest_inv = [np.array([0., 0., 0., 1.])]*self.bc
        self.aim = [False]*self.bc
        self.aim_child = [-1]*self.bc
        self.aim_dir = [None]*self.bc
        self.delta = [False]*self.bc
        b2m = {}
        for i in range(self.bc):
            nm = None
            for k, v in bmap.items():
                if k == self._jp(i):
                    nm = v
                    break
            if nm is None:
                continue
            b2m.setdefault(nm, i)
            if nm in self.hname:
                self.hrc_i[i] = self.hname[nm]
                self.rest_inv[i] = qconj(mat_to_quat(self.bind[self.hname[nm]]))
        self.root = self.mname.get("センター", -1)

        for i in range(self.bc):
            h = self.hrc_i[i]
            if h < 0:
                continue
            child_name = aim_child.get(self.skel[h]["name"])
            hc = self.hname.get(child_name, -1) if child_name else -1
            if hc < 0 or self.skel[hc]["parent"] != h:
                continue
            mc = b2m.get(child_name, -1)
            if mc < 0:
                continue
            rd = self.mpos[mc] - self.mpos[i]
            hd = np.array(self.bind[hc][:3, 3]) - np.array(self.bind[h][:3, 3])
            if np.dot(hd, hd) < 1e-6:      # SDO 那端退化(Bip01→Pelvis 重疊)→ 沒得瞄
                continue
            if np.dot(rd, rd) < 1e-6:      # MMD 那端重合(上半身/下半身 同點)→ 規則③ 借 SDO 的 rest 方向
                rd = hd
            self.aim[i], self.aim_child[i], self.aim_dir[i] = True, hc, rd/np.linalg.norm(rd)

        head = b2m.get("Bip01_Head", -1)
        if head >= 0 and not self.aim[head]:
            self.delta[head] = True
        if ankle == "delta":
            for nm in ("Bip01_L_Foot", "Bip01_R_Foot"):
                i = b2m.get(nm, -1)
                if i >= 0 and self.hrc_i[i] >= 0:
                    self.aim[i] = False
                    self.delta[i] = True
        pelvis_driven = b2m.get("Bip01_Pelvis", -1) >= 0 and self.hrc_i[b2m["Bip01_Pelvis"]] >= 0
        if self.root >= 0 and not (pelvis_driven and self.root_fix) and not self.aim[self.root]:
            self.delta[self.root] = True

        self.order = sorted(range(self.bc), key=self._depth)
        self.hroot = self.hname.get("Bip01", -1)
        self.hroot_rest = np.array(self.bind[self.hroot][:3, 3]) if self.hroot >= 0 else np.zeros(3)

    def _jp(self, i):
        if not hasattr(self, "_jpcache"):
            self._jpcache = {v: k for k, v in self.mname.items()}
        return self._jpcache.get(i, "")

    def _depth(self, i):
        d, p = 0, self.mpar[i]
        while p >= 0:
            d, p = d + 1, self.mpar[p]
        return d

    def _legs(self):
        self.legs = []
        for s, S in (("左", "L"), ("右", "R")):
            th, ca, an = (self.mname.get(s + "足", -1), self.mname.get(s + "ひざ", -1), self.mname.get(s + "足首", -1))
            h = self.hname.get(f"Bip01_{S}_Foot", -1)
            if min(th, ca, an) < 0 or h < 0 or not (self.aim[th] and self.aim[ca]):
                continue
            a = float(np.linalg.norm(self.mpos[ca] - self.mpos[th]))
            b = float(np.linalg.norm(self.mpos[an] - self.mpos[ca]))
            if a > 1e-4 and b > 1e-4:
                self.legs.append((th, ca, an, h, a, b))

    # -- SdoAvatar 的 FK ------------------------------------------------------
    def sdo(self, t):
        W = [None]*len(self.skel)
        for i, bn in enumerate(self.skel):
            node = self.by_id.get(bn["id"])
            local = (compose_local(interp_keys(node["rot"], t, True), interp_keys(node["scale"], t),
                                   interp_keys(node["pos"], t)) if node is not None else bn["rest_mat"].copy())
            p = bn["parent"]
            W[i] = local if p < 0 else W[p] @ local
        return W

    # -- MmdAvatar.LateUpdate -------------------------------------------------
    def pose(self, t):
        W = self.sdo(t)
        rw = [np.array([0., 0., 0., 1.])]*self.bc
        lpos = self.rest_local.copy()
        if self.root >= 0 and self.hroot >= 0:
            lpos[self.root] = self.rest_local[self.root] + qrot(self.qrooti, np.array(W[self.hroot][:3, 3]) - self.hroot_rest)/self.unit
        for i in self.order:
            p = self.mpar[i]
            prw = rw[p] if p >= 0 else np.array([0., 0., 0., 1.])
            if self.hrc_i[i] < 0:
                q = prw
            elif self.aim[i]:
                h = self.hrc_i[i]
                tgt = np.array(W[self.aim_child[i]][:3, 3]) - np.array(W[h][:3, 3])
                if np.dot(tgt, tgt) > 1e-8:
                    swing = from_to(self.aim_dir[i], qrot(self.qrooti, tgt))
                    dh = qmul(mat_to_quat(W[h]), self.rest_inv[i])
                    tw = qmul(qmul(self.qrooti, twist_about(dh, tgt/np.linalg.norm(tgt))), self.qroot)
                    q = qmul(tw, swing)
                else:
                    q = prw
            elif self.delta[i]:
                q = qmul(qmul(self.qrooti, qmul(mat_to_quat(W[self.hrc_i[i]]), self.rest_inv[i])), self.qroot)
            else:
                q = prw
            rw[i] = q
        pos = np.zeros((self.bc, 3))
        for i in self.order:
            p = self.mpar[i]
            pos[i] = (pos[p] + qrot(rw[p], lpos[i])) if p >= 0 else lpos[i]
        if self.ik:
            for th, ca, an, h, a, b in self.legs:
                tgt = qrot(self.qrooti, np.array(W[h][:3, 3]) - self.offset)/self.unit
                r = ik_solve(pos[th], tgt, pos[ca], a, b)
                if r is None:
                    continue
                pos[ca] = r[1]
                cd = tgt - r[1]
                n = float(np.linalg.norm(cd))
                pos[an] = r[1] + (cd/n)*b if n > 1e-10 else r[1]
        world = np.array([qrot(self.qroot, pos[i]*self.unit) for i in range(self.bc)]) + self.offset
        return world, W, rw


# --------------------------------------------------------------------------- 指標


def measure(rig):
    lf, rf = rig.mname["左足首"], rig.mname["右足首"]
    hl, hr = rig.hname["Bip01_L_Foot"], rig.hname["Bip01_R_Foot"]
    M = np.zeros((rig.frames, 2, 3))
    S = np.zeros((rig.frames, 2, 3))
    sole = np.zeros(rig.frames)
    bind_inv = qconj(mat_to_quat(rig.bind[hl]))
    for f in range(rig.frames):
        w, W, rw = rig.pose(float(f))
        M[f] = [w[lf], w[rf]]
        S[f] = [np.array(W[hl][:3, 3]), np.array(W[hr][:3, 3])]
        ideal = qmul(qmul(rig.qrooti, qmul(mat_to_quat(W[hl]), bind_inv)), rig.qroot)
        sole[f] = math.degrees(2*math.acos(min(1.0, abs(float(np.dot(ideal, rw[lf]))))))

    def slide(arr):
        tot = 0.0
        for f in range(1, rig.frames):
            i = 0 if S[f, 0, 1] <= S[f, 1, 1] else 1     # 支撐腳由 SDO 那份動作認定
            d = arr[f, i] - arr[f-1, i]
            tot += math.hypot(d[0], d[2])
        return tot

    def jerk(arr):
        return float(np.linalg.norm(np.diff(arr, n=2, axis=0), axis=2).mean())

    dy = np.concatenate([M[:, 0, 1]-S[:, 0, 1], M[:, 1, 1]-S[:, 1, 1]])
    return {
        "slide_sdo": slide(S), "slide_mmd": slide(M),
        "jerk_sdo": jerk(S), "jerk_mmd": jerk(M),
        "dy_mean": float(dy.mean()), "dy_min": float(dy.min()), "dy_max": float(dy.max()),
        "sole_mean": float(sole.mean()), "sole_max": float(sole.max()),
        "body_h": rig.body_h, "frames": rig.frames,
    }


def report(tag, m):
    print(f"--- {tag}")
    print(f"  planted-foot slide   SDO {m['slide_sdo']:7.1f}   MMD {m['slide_mmd']:7.1f}   ({m['slide_mmd']/max(m['slide_sdo'],1e-6):.2f}x)")
    print(f"  ankle jerk           SDO {m['jerk_sdo']:7.3f}   MMD {m['jerk_mmd']:7.3f}   ({m['jerk_mmd']/max(m['jerk_sdo'],1e-6):.2f}x)")
    print(f"  ankle height error   mean {m['dy_mean']:6.2f}   range {m['dy_min']:6.2f} .. {m['dy_max']:5.2f}   (身高 {m['body_h']:.1f})")
    print(f"  sole rotation error  mean {m['sole_mean']:6.1f}°  max {m['sole_max']:5.1f}°")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--mot", default=DEFAULT_MOT)
    ap.add_argument("--pmx", default=DEFAULT_PMX)
    ap.add_argument("--no-root-fix", action="store_true", help="センター 吃回 SDO Bip01 的世界旋轉(改壞前)")
    ap.add_argument("--ankle", choices=("follow", "aim", "delta"), default="delta")
    ap.add_argument("--no-ik", action="store_true")
    ap.add_argument("--compare", action="store_true", help="印修改前 / 只修 root / 現行 三組對照")
    a = ap.parse_args()

    print(f"MOT {a.mot}\nPMX {a.pmx}")
    if a.compare:
        for tag, kw in (("修改前(root 甩動 + 腳掌跟小腿 + 無 IK)", dict(root_fix=False, ankle="follow", ik=False)),
                        ("只修 センター 旋轉", dict(root_fix=True, ankle="follow", ik=False)),
                        ("+ 腳掌世界差量", dict(root_fix=True, ankle="delta", ik=False)),
                        ("+ 腳部 IK (現行)", dict(root_fix=True, ankle="delta", ik=True))):
            report(tag, measure(Rig(a.mot, a.pmx, **kw)))
        return
    rig = Rig(a.mot, a.pmx, root_fix=not a.no_root_fix, ankle=a.ankle, ik=not a.no_ik)
    report(f"root_fix={not a.no_root_fix} ankle={a.ankle} ik={not a.no_ik} ({rig.frames} frames)", measure(rig))


if __name__ == "__main__":
    main()
