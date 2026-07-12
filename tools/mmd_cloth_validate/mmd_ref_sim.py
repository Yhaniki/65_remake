# -*- coding: utf-8 -*-
"""GROUND-TRUTH reference simulation of MMD cloth physics (pybullet reconstruction).

MMD physics IS Bullet, so this pybullet rebuild is the authentic reference the Unity
(MagicaCloth) probe is compared against. It parses the PMX 2.0 physics section of
  H:/65_remake/assets/IkaHatunemiku2025/Ika-HatsuneMiku 2025-JP.Pmx
and follows three.js MMDPhysics / saba conventions:

  * world gravity (0, -9.8*10, 0) in MMD model units
  * mode-0 bodies  -> kinematic (mass 0), teleported once per 60fps frame from the
                      animated bone (three.js updates kinematic bodies per FRAME, then
                      runs the substeps against the new pose — reproduced here)
  * mode-1/2 bodies -> dynamic with authored mass; shape sphere/box/capsule with the
                      authored size (box size = half extents, capsule height = cylinder
                      section, Y-aligned — pybullet capsules are Z-aligned so a fixed
                      -90deg X rotation is baked into the collision frame)
  * collision filtering: authored group/enable-mask via setCollisionFilterGroupMask
                      ((groupA & maskB) && (groupB & maskA), identical to Bullet)
  * rigid-body / joint eulers compose R = Ry * Rx * Rz (MMD / saba convention)
  * stepping: 1/60 frames, 2 substeps of 1/120 (numSolverIterations=10)

APPROXIMATIONS (pybullet's user-constraint API has no btGeneric6DofSpringConstraint):
  1. Joints whose linear limits are fully locked (lower==upper==0 — every chain joint
     in this model) become NATIVE pybullet JOINT_POINT2POINT constraints at the
     authored anchor (exact for the linear part).
  2. The ANGULAR part of every joint (limits + rotation springs) is emulated each
     1/120 substep by a velocity-impulse solver written in numpy (Jacobi iterations
     with under-relaxation, ERP = 0.2 like Bullet's default solverInfo.m_erp):
     for each axis outside [lo, hi] the relative angular velocity is driven to the
     Bullet bias  -erp*err/dt  ("high-gain servo"; lower==upper => always active =
     locked axis, soft exactly the way Bullet's iterative solver is soft — this is
     what makes 0/0-limit MMD hair still swing).  Rotation springs (bang hair, k=5)
     add the impulse  -k*theta*dt  inside the allowed range.
     Axis frame & euler decomposition follow btGeneric6DofConstraint
     (calculateAngleInfo / matrixToEulerXYZ, R = Rx*Ry*Rz extraction).
  3. Joints WITH linear play (the 150 "Dress横" shear joints, +/-0.103 units) get the
     same emulation on their linear axes (anchor-velocity impulses incl. lever arms)
     instead of a p2p pin, so the authored skirt slack is preserved.
  4. btRigidBody damping semantics (v *= (1-damp)^dt each substep, damp clamped to
     [0,1] — the authored angular damp 2.0 clamps to 1.0 exactly like Bullet's
     setDamping) are applied manually; pybullet's own multibody damping (a force,
     different semantics) is disabled.
  5. mode-2 ("physics + bone align") bodies are simulated as mode 1. This model has
     none (650x mode 1, 73x mode 0).
  6. Joint linear SPRINGS on linearly-locked joints are no-ops (the lock dominates in
     Bullet too) and are skipped.  No ground plane (no chain can reach y=0).

Scenario contract (shared with the Unity probe):
  rest: 4.0 s no motion.
  turn: 1.5 s settle, HEAD yaws +90 deg about +Y (linear ramp) over 0.4 s about the
        head-bone origin, hold 2.0 s.                         (total 3.9 s)
  walk: 1.5 s settle, whole model translates +Z at 1.2 m/s (linear) for 2 s, stop,
        hold 2.0 s.                                           (total 5.5 s)
  spin: 1.5 s settle, whole model rotates 360 deg about world +Y through the origin
        (linear ramp) over 1.0 s, hold 2.0 s.                 (total 4.5 s)
  The recorded "anchor" is ALWAYS the head bone world transform [x,y,z,qx,qy,qz,qw].
  Chains recorded (rigid-body name prefix -> ordered bone list, root->tip):
    RightTwicHairA : RightTwicHairA_{0..29}_1   (twintail strand A, col 1)
    BangHairA      : BangHairA_{00..02}         (bang chain A)
    Tie            : Tie_{0..19}_1              (first two links are kinematic)
    Dress_5        : Dress_{0..10}_5            (skirt panel 5, rows 0..10)
  JSON positions are MODEL UNITS; divide by unitsPerMeter (= height/1.6) for meters.
"""
import json
import math
import os
import sys
import time

import numpy as np
import pybullet as p

import pmx_parse

HERE = os.path.dirname(os.path.abspath(__file__))
PMX_PATH = r"H:/65_remake/assets/IkaHatunemiku2025/Ika-HatsuneMiku 2025-JP.Pmx"

FPS = 60
SUBSTEPS = 2
DT = 1.0 / (FPS * SUBSTEPS)          # 1/120
GRAVITY = -9.8 * 10.0                # three.js MMDPhysics world gravity (model units)
ERP = 0.2                            # Bullet solverInfo.m_erp default
SOLVER_ITERS = 10                    # Gauss-Seidel sweeps (Bullet numSolverIterations)
RELAX = 1.0                          # GS relaxation (Bullet sequential impulse = 1)
MAX_W = 200.0                        # safety clamps (never hit in a healthy run)
MAX_V = 1000.0

HEAD_BONE = u"頭"                # 頭
LOWER_BONE = u"下半身"   # 下半身


# ---------------------------------------------------------------- math helpers
def euler_mmd_to_mat(r):
    """MMD rigid-body/joint euler (radians) -> rotation matrix, R = Ry*Rx*Rz."""
    rx, ry, rz = r
    cx, sx = math.cos(rx), math.sin(rx)
    cy, sy = math.cos(ry), math.sin(ry)
    cz, sz = math.cos(rz), math.sin(rz)
    Rx = np.array([[1, 0, 0], [0, cx, -sx], [0, sx, cx]])
    Ry = np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]])
    Rz = np.array([[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]])
    return Ry @ Rx @ Rz


def mat_to_quat(m):
    """3x3 -> pybullet quaternion [x,y,z,w]."""
    t = m[0, 0] + m[1, 1] + m[2, 2]
    if t > 0:
        s = math.sqrt(t + 1.0) * 2
        w = 0.25 * s
        x = (m[2, 1] - m[1, 2]) / s
        y = (m[0, 2] - m[2, 0]) / s
        z = (m[1, 0] - m[0, 1]) / s
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        w = (m[2, 1] - m[1, 2]) / s
        x = 0.25 * s
        y = (m[0, 1] + m[1, 0]) / s
        z = (m[0, 2] + m[2, 0]) / s
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        w = (m[0, 2] - m[2, 0]) / s
        x = (m[0, 1] + m[1, 0]) / s
        y = 0.25 * s
        z = (m[1, 2] + m[2, 1]) / s
    else:
        s = math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        w = (m[1, 0] - m[0, 1]) / s
        x = (m[0, 2] + m[2, 0]) / s
        y = (m[1, 2] + m[2, 1]) / s
        z = 0.25 * s
    return [x, y, z, w]


def quats_to_mats(q):
    """(N,4) xyzw -> (N,3,3), vectorized."""
    x, y, z, w = q[:, 0], q[:, 1], q[:, 2], q[:, 3]
    m = np.empty((q.shape[0], 3, 3))
    m[:, 0, 0] = 1 - 2 * (y * y + z * z)
    m[:, 0, 1] = 2 * (x * y - z * w)
    m[:, 0, 2] = 2 * (x * z + y * w)
    m[:, 1, 0] = 2 * (x * y + z * w)
    m[:, 1, 1] = 1 - 2 * (x * x + z * z)
    m[:, 1, 2] = 2 * (y * z - x * w)
    m[:, 2, 0] = 2 * (x * z - y * w)
    m[:, 2, 1] = 2 * (y * z + x * w)
    m[:, 2, 2] = 1 - 2 * (x * x + y * y)
    return m


def mats_to_euler_xyz(m):
    """Bullet matrixToEulerXYZ, vectorized: extract (tx,ty,tz) with R = Rx*Ry*Rz."""
    r02 = np.clip(m[:, 0, 2], -1.0, 1.0)
    ty = np.arcsin(r02)
    tx = np.arctan2(-m[:, 1, 2], m[:, 2, 2])
    tz = np.arctan2(-m[:, 0, 1], m[:, 0, 0])
    # gimbal fallback (|r02| == 1)
    lim = np.abs(r02) > 0.999999
    if lim.any():
        tx = np.where(lim, np.arctan2(m[:, 1, 0], m[:, 1, 1]) * np.sign(r02), tx)
        tz = np.where(lim, 0.0, tz)
    return np.stack([tx, ty, tz], axis=1)


def normalize_rows(v, eps=1e-9):
    n = np.linalg.norm(v, axis=1, keepdims=True)
    return v / np.maximum(n, eps)


# ---------------------------------------------------------------- model / world
class RefWorld(object):
    def __init__(self, pmx):
        self.pmx = pmx
        self.height = pmx.vert_max_y - pmx.vert_min_y
        self.upm = self.height / 1.6                      # unitsPerMeter
        self.nb = len(pmx.rigid_bodies)
        self.bone_by_name = {b.name_jp: i for i, b in enumerate(pmx.bones)}
        self.head_bone = self.bone_by_name[HEAD_BONE]
        self.head_pos = np.array(pmx.bones[self.head_bone].position)
        self.head_desc = self._descendants(self.head_bone)

        # rest world transform per rigid body
        self.rb_rot0 = np.array([euler_mmd_to_mat(rb.rotation) for rb in pmx.rigid_bodies])
        self.rb_pos0 = np.array([rb.position for rb in pmx.rigid_bodies])
        self.dynamic = np.array([rb.mode != 0 for rb in pmx.rigid_bodies])
        self.kin_ids = [i for i in range(self.nb) if not self.dynamic[i]]
        self.kin_head = np.array([pmx.rigid_bodies[i].bone in self.head_desc
                                  for i in self.kin_ids])

        self._build_bodies()
        self._build_joints()
        self._build_chains()
        self._bid2idx = {bid: i for i, bid in enumerate(self.bids)}

    def _descendants(self, root):
        kids = {}
        for i, b in enumerate(self.pmx.bones):
            kids.setdefault(b.parent, []).append(i)
        out, stack = set(), [root]
        while stack:
            b = stack.pop()
            out.add(b)
            stack.extend(kids.get(b, []))
        return out

    # -- bodies ---------------------------------------------------------------
    def _build_bodies(self):
        cap_fix = [-math.sin(math.pi / 4), 0, 0, math.cos(math.pi / 4)]  # Z-cap -> Y-cap
        self.bids = []
        for rb in self.pmx.rigid_bodies:
            if rb.shape == 0:
                cs = p.createCollisionShape(p.GEOM_SPHERE, radius=max(rb.size[0], 1e-4))
            elif rb.shape == 1:
                he = [max(v, 1e-4) for v in rb.size]
                cs = p.createCollisionShape(p.GEOM_BOX, halfExtents=he)
            else:
                cs = p.createCollisionShape(p.GEOM_CAPSULE, radius=max(rb.size[0], 1e-4),
                                            height=max(rb.size[1], 1e-4),
                                            collisionFrameOrientation=cap_fix)
            mass = rb.mass if rb.mode != 0 else 0.0
            q = mat_to_quat(self.rb_rot0[rb.index])
            bid = p.createMultiBody(baseMass=mass, baseCollisionShapeIndex=cs,
                                    basePosition=list(self.rb_pos0[rb.index]),
                                    baseOrientation=q)
            p.setCollisionFilterGroupMask(bid, -1, 1 << rb.group, int(rb.mask))
            p.changeDynamics(bid, -1, lateralFriction=rb.friction,
                             restitution=rb.restitution,
                             linearDamping=0.0, angularDamping=0.0,
                             activationState=p.ACTIVATION_STATE_DISABLE_SLEEPING)
            self.bids.append(bid)

        # cached dynamics data
        self.mass = np.zeros(self.nb)
        self.inertia_diag = np.full((self.nb, 3), np.inf)
        for i, rb in enumerate(self.pmx.rigid_bodies):
            if rb.mode != 0:
                info = p.getDynamicsInfo(self.bids[i], -1)
                self.mass[i] = info[0]
                self.inertia_diag[i] = np.maximum(info[2], 1e-9)
        self.inv_mass = np.where(self.dynamic, 1.0 / np.maximum(self.mass, 1e-9), 0.0)
        # btRigidBody-style exponential damping factors per substep (kinematic
        # bodies are never damped — their injected anchor velocity must survive)
        ld = np.array([min(max(rb.linear_damp, 0.0), 1.0) for rb in self.pmx.rigid_bodies])
        ad = np.array([min(max(rb.angular_damp, 0.0), 1.0) for rb in self.pmx.rigid_bodies])
        self.lin_damp_f = np.where(self.dynamic, np.power(1.0 - ld, DT), 1.0)
        self.ang_damp_f = np.where(self.dynamic, np.power(1.0 - ad, DT), 1.0)

    # -- joints ---------------------------------------------------------------
    def _build_joints(self):
        pmx = self.pmx
        ja, jb, fA_rot, fB_rot, aA, aB = [], [], [], [], [], []
        rlo, rhi, rspr = [], [], []
        lin_mask, plo, phi = [], [], []
        self.p2p_ids = []
        for j in pmx.joints:
            A, B = j.rb_a, j.rb_b
            if not (self.dynamic[A] or self.dynamic[B]):
                continue                                  # both kinematic: no-op
            Tj_rot = euler_mmd_to_mat(j.rotation)
            Tj_pos = np.array(j.position)
            RA0, RB0 = self.rb_rot0[A], self.rb_rot0[B]
            pA0, pB0 = self.rb_pos0[A], self.rb_pos0[B]
            frameA = RA0.T @ Tj_rot                       # joint frame in A-local
            frameB = RB0.T @ Tj_rot
            anchorA = RA0.T @ (Tj_pos - pA0)              # anchor in A-local
            anchorB = RB0.T @ (Tj_pos - pB0)

            lin_locked = all(abs(lo) < 1e-9 and abs(hi) < 1e-9
                             for lo, hi in zip(j.pos_lo, j.pos_hi))
            if lin_locked:
                cid = p.createConstraint(self.bids[A], -1, self.bids[B], -1,
                                         p.JOINT_POINT2POINT, [0, 0, 0],
                                         list(anchorA), list(anchorB))
                p.changeConstraint(cid, maxForce=1e8, erp=ERP)
                self.p2p_ids.append(cid)

            ja.append(A); jb.append(B)
            fA_rot.append(frameA); fB_rot.append(frameB)
            aA.append(anchorA); aB.append(anchorB)
            rlo.append(j.rot_lo); rhi.append(j.rot_hi); rspr.append(j.rot_spring)
            lin_mask.append(not lin_locked)
            plo.append(j.pos_lo); phi.append(j.pos_hi)

        self.ja = np.array(ja); self.jb = np.array(jb)
        self.fA = np.array(fA_rot); self.fB = np.array(fB_rot)
        self.aA = np.array(aA); self.aB = np.array(aB)
        self.rlo = np.array(rlo); self.rhi = np.array(rhi)
        self.rspr = np.abs(np.array(rspr))
        self.lin_em = np.array(lin_mask)                  # joints with emulated linear axes
        self.plo = np.array(plo); self.phi = np.array(phi)
        self.nj = len(ja)

        # Greedy edge coloring: joints = edges over bodies. No two joints in one
        # color share a body, so a per-color vectorized update IS a sequential
        # (Gauss-Seidel) impulse sweep — same convergence class as Bullet's solver,
        # which is what keeps the closed-loop skirt lattice from ringing.
        used = {}                                        # body -> set of colors
        color_of = np.zeros(self.nj, dtype=int)
        for k in range(self.nj):
            a, b = ja[k], jb[k]
            ca = used.setdefault(a, set()); cb = used.setdefault(b, set())
            c = 0
            while c in ca or c in cb:
                c += 1
            color_of[k] = c
            ca.add(c); cb.add(c)
        self.colors = [np.nonzero(color_of == c)[0]
                       for c in range(color_of.max() + 1)]
        # warm-start accumulators (Bullet SOLVER_USE_WARMSTARTING is default-on)
        self.lam_ws = np.zeros((self.nj, 3))     # angular impulse per joint axis
        self.lamL_ws = np.zeros((self.nj, 3))    # linear impulse per joint axis

    # -- chains ---------------------------------------------------------------
    def _build_chains(self):
        names = {rb.name_jp: i for i, rb in enumerate(self.pmx.rigid_bodies)}
        chains = {
            "RightTwicHairA": ["RightTwicHairA_%d_1" % r for r in range(30)],
            "BangHairA": ["BangHairA_%02d" % r for r in range(3)],
            "Tie": ["Tie_%d_1" % r for r in range(20)],
            "Dress_5": ["Dress_%d_5" % r for r in range(11)],
        }
        self.chains = {}
        for cname, bodylist in chains.items():
            idx = [names[n] for n in bodylist]
            bones = []
            offs = []
            for i in idx:
                rb = self.pmx.rigid_bodies[i]
                bone = self.pmx.bones[rb.bone]
                bones.append(bone.name_jp)
                # bone position in body-local coords (bone rest rotation = identity)
                offs.append(self.rb_rot0[i].T @ (np.array(bone.position) - self.rb_pos0[i]))
            self.chains[cname] = {"bodies": idx, "bones": bones, "off": np.array(offs)}

    # -- state I/O ------------------------------------------------------------
    def read_state(self):
        pos = np.empty((self.nb, 3)); quat = np.empty((self.nb, 4))
        vel = np.zeros((self.nb, 3)); avl = np.zeros((self.nb, 3))
        for i in range(self.nb):
            pp, qq = p.getBasePositionAndOrientation(self.bids[i])
            pos[i] = pp; quat[i] = qq
            if self.dynamic[i]:
                v, w = p.getBaseVelocity(self.bids[i])
                vel[i] = v; avl[i] = w
        # kinematic bodies: Bullet derives their velocity from the motion state
        # (btTransformUtil::calculateVelocity) and the contact/joint solver sees
        # it; pybullet mass-0 bodies report 0, so inject the finite-difference
        # velocities of the animated anchors here for OUR solve.
        vel += self.kin_vel
        avl += self.kin_avl
        return pos, quat, vel, avl

    # -- constraint emulation (one substep) ------------------------------------
    def solve_substep(self, pos, quat, vel, avl):
        """Damping + angular 6DOF emulation + Dress横 linear emulation; writes
        corrected velocities back to pybullet. Returns nothing."""
        # 1) btRigidBody damping
        vel = vel * self.lin_damp_f[:, None]
        avl = avl * self.ang_damp_f[:, None]

        R = quats_to_mats(quat)
        RA = R[self.ja] @ self.fA                          # joint frame A, world
        RB = R[self.jb] @ self.fB
        Rrel = np.einsum("nji,njk->nik", RA, RB)           # RA^T @ RB
        theta = mats_to_euler_xyz(Rrel)

        # Bullet calculateAngleInfo axes
        axis0B = RB[:, :, 0]
        axis2A = RA[:, :, 2]
        ax1 = normalize_rows(np.cross(axis2A, axis0B))
        ax0 = normalize_rows(np.cross(ax1, axis2A))
        ax2 = normalize_rows(np.cross(axis0B, ax1))
        axes = np.stack([ax0, ax1, ax2], axis=1)           # (nj,3axes,3)

        # world-space inverse inertia tensor per body: R * diag^-1 * R^T
        inv_d = np.where(np.isinf(self.inertia_diag), 0.0,
                         1.0 / self.inertia_diag)              # (nb,3)
        invIW = np.einsum("nij,nj,nkj->nik", R, inv_d, R)      # (nb,3,3)

        # per-axis angular effective mass: 1/Ieff = ax^T IA^-1 ax + ax^T IB^-1 ax
        invIA = np.einsum("nki,nij,nkj->nk", axes, invIW[self.ja], axes)
        invIB = np.einsum("nki,nij,nkj->nk", axes, invIW[self.jb], axes)
        inv_sum = invIA + invIB
        Ieff = np.where(inv_sum > 1e-12, 1.0 / np.maximum(inv_sum, 1e-12), 0.0)

        err = theta - np.clip(theta, self.rlo, self.rhi)
        active = np.abs(err) > 1e-12
        bias = np.where(active, -ERP * err / DT, 0.0)
        # rotation spring inside the allowed range (torque impulse -k*theta*dt)
        spr_imp = np.where(~active & (self.rspr > 0), -self.rspr * theta * DT, 0.0)

        # --- linear emulation data (joints with authored linear play) ---
        rAw = np.einsum("nij,nj->ni", R[self.ja], self.aA)       # anchor offset world
        rBw = np.einsum("nij,nj->ni", R[self.jb], self.aB)
        d = np.einsum("nji,nj->ni", RA, (pos[self.jb] + rBw) - (pos[self.ja] + rAw))
        lerr = d - np.clip(d, self.plo, self.phi)
        lact = self.lin_em[:, None] & (np.abs(lerr) > 1e-12)
        lbias = np.where(lact, -ERP * lerr / DT, 0.0)
        mA = self.inv_mass[self.ja]; mB = self.inv_mass[self.jb]
        # linear effective mass per joint axis n (world cols of RA), INCLUDING the
        # rotational lever-arm terms: 1/meff = 1/mA + 1/mB
        #                                     + (rA x n)^T IA^-1 (rA x n) + (B term)
        colsW = np.transpose(RA, (0, 2, 1))                # (nj,3axes,3) world axes
        cA = np.cross(rAw[:, None, :], colsW)              # rA x n  (nj,3axes,3)
        cB = np.cross(rBw[:, None, :], colsW)
        kA = np.einsum("nki,nij,nkj->nk", cA, invIW[self.ja], cA)
        kB = np.einsum("nki,nij,nkj->nk", cB, invIW[self.jb], cB)
        inv_meff = mA[:, None] + mB[:, None] + kA + kB
        meff = 1.0 / np.maximum(inv_meff, 1e-12)

        # --- contact rows (joints and contacts must be solved TOGETHER, like
        # Bullet's single sequential-impulse LCP, or the joint bias velocity and
        # the contact push-out pump energy into each other forever).  Unilateral
        # normal rows, velocity target 0 (restitution 0; authored cloth friction
        # is 0 so friction rows are omitted); penetration recovery itself is left
        # to pybullet's split impulse, which is position-only = no added energy.
        p.performCollisionDetection()
        rows = []
        for cp in p.getContactPoints():
            ca = self._bid2idx.get(cp[1]); cb = self._bid2idx.get(cp[2])
            if ca is None or cb is None:
                continue
            if not (self.dynamic[ca] or self.dynamic[cb]):
                continue
            if cp[8] > 0.0:                      # separated
                continue
            rows.append((ca, cb, cp[5], cp[6], cp[7]))
        nc = len(rows)
        if nc:
            cia = np.array([r[0] for r in rows])
            cib = np.array([r[1] for r in rows])
            cpA = np.array([r[2] for r in rows])
            cpB = np.array([r[3] for r in rows])
            cn = np.array([r[4] for r in rows])   # normalOnB: points B -> A
            crA = cpA - pos[cia]
            crB = cpB - pos[cib]
            cxA = np.cross(crA, cn); cxB = np.cross(crB, cn)
            ckA = np.einsum("ni,nij,nj->n", cxA, invIW[cia], cxA)
            ckB = np.einsum("ni,nij,nj->n", cxB, invIW[cib], cxB)
            cmeff = 1.0 / np.maximum(
                self.inv_mass[cia] + self.inv_mass[cib] + ckA + ckB, 1e-12)
            cacc = np.zeros(nc)

        dv = np.zeros_like(vel)
        dw = np.zeros_like(avl)

        # springs: single application (force-based, not a velocity target)
        if (self.rspr > 0).any():
            tauS = np.einsum("nk,nki->ni", spr_imp, axes)
            np.add.at(dw, self.jb, np.einsum("nij,nj->ni", invIW[self.jb], tauS))
            np.subtract.at(dw, self.ja, np.einsum("nij,nj->ni", invIW[self.ja], tauS))

        # warm start (Bullet applies 0.85 * last step's impulse before iterating)
        lam_acc = np.where(active, 0.85 * self.lam_ws, 0.0)
        lamL_acc = np.where(lact, 0.85 * self.lamL_ws, 0.0)
        tau0 = np.einsum("nk,nki->ni", lam_acc, axes)
        np.add.at(dw, self.jb, np.einsum("nij,nj->ni", invIW[self.jb], tau0))
        np.subtract.at(dw, self.ja, np.einsum("nij,nj->ni", invIW[self.ja], tau0))
        J0 = np.einsum("nk,nki->ni", lamL_acc, colsW)
        np.add.at(dv, self.jb, J0 * self.inv_mass[self.jb, None])
        np.subtract.at(dv, self.ja, J0 * self.inv_mass[self.ja, None])
        np.add.at(dw, self.jb, np.einsum("nij,nj->ni", invIW[self.jb],
                                         np.cross(rBw, J0)))
        np.subtract.at(dw, self.ja, np.einsum("nij,nj->ni", invIW[self.ja],
                                              np.cross(rAw, J0)))

        # Gauss-Seidel sweeps over edge-color groups: within a color no two joints
        # share a body, so vectorized "+=" is a true sequential-impulse update.
        for _ in range(SOLVER_ITERS):
            for sel in self.colors:
                ia = self.ja[sel]; ib = self.jb[sel]
                axs = axes[sel]
                wrel = np.einsum("nki,ni->nk", axs, (avl + dw)[ib] - (avl + dw)[ia])
                lam = np.where(active[sel],
                               RELAX * Ieff[sel] * (bias[sel] - wrel), 0.0)
                lam_acc[sel] += lam
                tau = np.einsum("nk,nki->ni", lam, axs)
                dw[ib] += np.einsum("nij,nj->ni", invIW[ib], tau)
                dw[ia] -= np.einsum("nij,nj->ni", invIW[ia], tau)

                if self.lin_em[sel].any():
                    l = sel[self.lin_em[sel]]
                    il_a = self.ja[l]; il_b = self.jb[l]
                    vA = (vel + dv)[il_a] + np.cross((avl + dw)[il_a], rAw[l])
                    vB = (vel + dv)[il_b] + np.cross((avl + dw)[il_b], rBw[l])
                    vrel = np.einsum("nki,ni->nk", colsW[l], vB - vA)
                    lamL = np.where(lact[l],
                                    RELAX * meff[l] * (lbias[l] - vrel), 0.0)
                    lamL_acc[l] += lamL
                    J = np.einsum("nk,nki->ni", lamL, colsW[l])
                    dv[il_b] += J * self.inv_mass[il_b, None]
                    dv[il_a] -= J * self.inv_mass[il_a, None]
                    dw[il_b] += np.einsum("nij,nj->ni", invIW[il_b],
                                          np.cross(rBw[l], J))
                    dw[il_a] -= np.einsum("nij,nj->ni", invIW[il_a],
                                          np.cross(rAw[l], J))

            if nc:
                # contact pass (Jacobi, under-relaxed, accumulated clamp >= 0):
                # stop approach along the normal, never pull
                vpA = (vel + dv)[cia] + np.cross((avl + dw)[cia], crA)
                vpB = (vel + dv)[cib] + np.cross((avl + dw)[cib], crB)
                vn = np.einsum("ni,ni->n", cn, vpA - vpB)   # <0 = closing
                delta = -0.5 * cmeff * vn
                new_acc = np.maximum(cacc + delta, 0.0)
                dj = new_acc - cacc
                cacc = new_acc
                Jc = cn * dj[:, None]
                np.add.at(dv, cia, Jc * self.inv_mass[cia, None])
                np.subtract.at(dv, cib, Jc * self.inv_mass[cib, None])
                np.add.at(dw, cia, np.einsum("nij,nj->ni", invIW[cia],
                                             np.cross(crA, Jc)))
                np.subtract.at(dw, cib, np.einsum("nij,nj->ni", invIW[cib],
                                                  np.cross(crB, Jc)))

        self.lam_ws = lam_acc
        self.lamL_ws = lamL_acc

        vel = np.clip(vel + dv, -MAX_V, MAX_V)
        avl = np.clip(avl + dw, -MAX_W, MAX_W)
        for i in np.nonzero(self.dynamic)[0]:
            p.resetBaseVelocity(self.bids[i], list(vel[i]), list(avl[i]))

    # -- kinematic driving ------------------------------------------------------
    def set_kinematic(self, Mrot, Mpos, Hrot, frame_dt=None):
        """Teleport mode-0 bodies. World pose = M * (head-desc? Th*H*Th^-1) * rest.
        When frame_dt is given, finite-difference velocities vs the previous pose
        are stored (self.kin_vel / kin_avl) so the constraint solve sees moving
        anchors the way Bullet's kinematic motion-state velocity does."""
        if not hasattr(self, "kin_vel"):
            self.kin_vel = np.zeros((self.nb, 3))
            self.kin_avl = np.zeros((self.nb, 3))
            self._kin_prev = {}
        for k, i in enumerate(self.kin_ids):
            R0, p0 = self.rb_rot0[i], self.rb_pos0[i]
            if self.kin_head[k]:
                Rw = Mrot @ Hrot @ R0
                pw = Mrot @ (self.head_pos + Hrot @ (p0 - self.head_pos)) + Mpos
            else:
                Rw = Mrot @ R0
                pw = Mrot @ p0 + Mpos
            if frame_dt and i in self._kin_prev:
                pp, Rp = self._kin_prev[i]
                self.kin_vel[i] = (pw - pp) / frame_dt
                dR = Rw @ Rp.T
                ang = math.acos(min(max((np.trace(dR) - 1.0) / 2.0, -1.0), 1.0))
                if ang > 1e-9:
                    axis = np.array([dR[2, 1] - dR[1, 2],
                                     dR[0, 2] - dR[2, 0],
                                     dR[1, 0] - dR[0, 1]])
                    n = np.linalg.norm(axis)
                    if n > 1e-12:
                        self.kin_avl[i] = axis / n * (ang / frame_dt)
                else:
                    self.kin_avl[i] = 0.0
            else:
                self.kin_vel[i] = 0.0
                self.kin_avl[i] = 0.0
            self._kin_prev[i] = (pw, Rw)
            p.resetBasePositionAndOrientation(self.bids[i], list(pw), mat_to_quat(Rw))


# ---------------------------------------------------------------- scenarios
def scenario_motion(name, t, upm):
    """Returns (Mrot 3x3, Mpos 3, Hrot 3x3, headYawRad) at time t."""
    I = np.eye(3)
    Mrot, Mpos, Hrot, yaw = I, np.zeros(3), I, 0.0
    if name == "turn":
        f = min(max((t - 1.5) / 0.4, 0.0), 1.0)
        yaw = math.radians(90.0) * f
        Hrot = euler_mmd_to_mat((0.0, yaw, 0.0))
    elif name == "walk":
        dist = 1.2 * upm * min(max(t - 1.5, 0.0), 2.0)
        Mpos = np.array([0.0, 0.0, dist])
    elif name == "spin":
        f = min(max((t - 1.5) / 1.0, 0.0), 1.0)
        ang = 2.0 * math.pi * f
        Mrot = euler_mmd_to_mat((0.0, ang, 0.0))
    return Mrot, Mpos, Hrot, yaw


DURATIONS = {"rest": 4.0, "turn": 3.9, "walk": 5.5, "spin": 4.5}


def run_scenario(pmx, name):
    p.resetSimulation()
    # split impulse = Bullet's default penetration recovery (keeps initially
    # interpenetrating authored bodies from gaining bounce energy)
    p.setPhysicsEngineParameter(fixedTimeStep=DT, numSolverIterations=10,
                                numSubSteps=1, deterministicOverlappingPairs=1,
                                useSplitImpulse=1,
                                splitImpulsePenetrationThreshold=-0.02)
    p.setGravity(0, GRAVITY, 0)
    w = RefWorld(pmx)

    frames = int(round(DURATIONS[name] * FPS))
    chain_rec = {c: [] for c in w.chains}
    anchor_rec = []

    def record(Mrot, Mpos, Hrot):
        pos, quat, _, _ = w.read_state()
        R = quats_to_mats(quat)
        for cname, ch in w.chains.items():
            idx = ch["bodies"]
            bp = pos[idx] + np.einsum("nij,nj->ni", R[idx], ch["off"])
            chain_rec[cname].append([[round(float(v), 5) for v in row] for row in bp])
        hq = mat_to_quat(Mrot @ Hrot)
        hp = Mrot @ w.head_pos + Mpos
        anchor_rec.append([round(float(hp[0]), 5), round(float(hp[1]), 5),
                           round(float(hp[2]), 5)] + [round(float(v), 7) for v in hq])

    Mrot, Mpos, Hrot, _ = scenario_motion(name, 0.0, w.upm)
    w.set_kinematic(Mrot, Mpos, Hrot)
    record(Mrot, Mpos, Hrot)

    t0 = time.time()
    for k in range(1, frames + 1):
        t = k / float(FPS)
        # three.js MMDPhysics: kinematic bodies follow the animation once per FRAME,
        # then the substeps run against the new anchor pose.
        Mrot, Mpos, Hrot, _ = scenario_motion(name, t, w.upm)
        w.set_kinematic(Mrot, Mpos, Hrot, frame_dt=1.0 / FPS)
        for _ in range(SUBSTEPS):
            pos, quat, vel, avl = w.read_state()
            w.solve_substep(pos, quat, vel, avl)
            p.stepSimulation()
        record(Mrot, Mpos, Hrot)
    print("  %s: %d frames, %.1fs wall" % (name, frames + 1, time.time() - t0))

    out = {
        "scenario": name,
        "fps": FPS,
        "unitsPerMeter": w.upm,
        "anchor": anchor_rec,
        "chains": {c: {"bones": w.chains[c]["bones"], "frames": chain_rec[c]}
                   for c in w.chains},
    }
    path = os.path.join(HERE, "ref_%s.json" % name)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, separators=(",", ":"))
    print("  wrote", path, "(%.1f MB)" % (os.path.getsize(path) / 1e6))
    return out


def main():
    pmx = pmx_parse.load(PMX_PATH)
    print("model: %s  height=%.3f units  unitsPerMeter=%.4f" %
          (pmx.name_en, pmx.vert_max_y - pmx.vert_min_y,
           (pmx.vert_max_y - pmx.vert_min_y) / 1.6))
    p.connect(p.DIRECT)
    scen = {}
    only = sys.argv[1:] or list(DURATIONS.keys())
    for name in only:
        print("scenario:", name)
        scen[name] = run_scenario(pmx, name)
    p.disconnect()

    if set(scen) == set(DURATIONS):
        import cloth_metrics
        metrics = cloth_metrics.compute_metrics(scen)
        mpath = os.path.join(HERE, "ref_metrics.json")
        with open(mpath, "w", encoding="utf-8") as f:
            json.dump(metrics, f, ensure_ascii=False, indent=2)
        print("wrote", mpath)
        print(json.dumps(metrics, indent=2))


if __name__ == "__main__":
    main()
