using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Drives an SDO avatar: HRC skeleton + MOT motion, CPU-skinning each .msh part every frame.
    /// Math validated against bms_sdo: animated local = rest with rotation replaced by the CONJUGATED MOT quat
    /// (row-vector), translation from the pos track if animated; convert X·Lᵀ·X; FK to animWorld.
    /// Skinning (reference mesh_skin.py): when the part supplies its MSH inverse-bind for a bone, use RETARGET
    /// skinMat = MSH_bind·inv(HRC_bind)·pose·inv(MSH_bind) (identity at bind, pivots the limb about the MSH bind
    /// origin -> no arm shear under MOT); else fall back to hrc_bind = pose·inv(HRC_bind).
    /// </summary>
    public sealed class SdoAvatar : MonoBehaviour
    {
        private sealed class Part
        {
            public Mesh Mesh; public Vector3[] Bind; public int[] Bi; public float[] Bw; public Vector3[] Work;
            // per-HRC-bone MSH bind matrices (Unity space) for RETARGET skinning; HasMsh[b]==false -> hrc_bind fallback
            public Matrix4x4[] MshInv, MshBind; public bool[] HasMsh; public Matrix4x4[] Skin;
        }

        private HrcLoader _hrc;
        private MotLoader _mot;
        private readonly List<Part> _parts = new List<Part>();
        private readonly List<(int bone, Transform t)> _anchors = new List<(int, Transform)>();
        private Matrix4x4[] _animWorld, _skinMat;
        // body-shape (體型): per-bone local scale that fattens/thins the dancer's cross-section without changing bone
        // length — faithful port of decompiled AvatarHelper_ScaleBones (see SdoBodyShape). identity until SetBodyShape.
        private Matrix4x4[] _scaleMat; private bool _hasBodyScale;
        // motion crossfade: when the active clip switches (dance<->idle, dance clip A->B) blend each bone's LOCAL
        // transform from the outgoing displayed pose toward the new clip over BlendSec, so it isn't a hard cut.
        public float BlendSec = 1.0f;                        // crossfade duration (s); 0 -> instant
        private Matrix4x4[] _dispLocal;                      // last displayed per-bone local (blend source / continuity)
        private Quaternion[] _blendFromQ; private Vector3[] _blendFromP;   // snapshot of the displayed pose at the switch
        private float _blendStart = -1f;                     // Time.time the current crossfade began (<0 = none)
        private bool _haveDisp;                              // _dispLocal holds a valid previous pose
        private MotLoader _lastMot;                          // active clip last frame (to detect a switch)

        public float Fps = 30f;          // MOT frame rate (time = integer frame index)
        public bool Animate = true;      // false -> hold bind pose (verification)
        public float FrameOverride = -1; // >=0 -> use this frame instead of the wall clock (DPS sync hook)

        // DPS dance-sync: choreography sequencing motion slices to the song clock
        public DpsLoader Dps;
        public System.Func<string, MotLoader> MotResolver;   // motion name -> loaded MotLoader (cached by caller)
        public System.Func<float> DanceTimeSec;              // current song time in seconds (negative before the dance starts)
        public MotLoader RestMot;                            // standby idle clip, looped before the DPS dance starts and after it ends (rest cat 0x15)
        public System.Func<bool> DanceEnabled;               // returns false -> hold the standby idle even inside the DPS window (8-beat dance-gate stop)

        public void Setup(HrcLoader hrc, MotLoader mot)
        {
            _hrc = hrc; _mot = mot;
            int bc = hrc.Names.Length;
            _animWorld = new Matrix4x4[bc]; _skinMat = new Matrix4x4[bc];
            _dispLocal = new Matrix4x4[bc]; _blendFromQ = new Quaternion[bc]; _blendFromP = new Vector3[bc];
            _haveDisp = false; _lastMot = mot; _blendStart = -1f;
            _scaleMat = new Matrix4x4[bc]; for (int i = 0; i < bc; i++) _scaleMat[i] = Matrix4x4.identity; _hasBodyScale = false;
        }

        /// <summary>
        /// Set the dancer's body shape (體型). <paramref name="weight"/> is the SDO body weight B: 1.0 = standard,
        /// &lt;1 thinner, &gt;1 fatter (e.g. <c>SdoBodyShape.WeightFromIndex(0, false)</c> for the game's thin female).
        /// Each torso/limb-root bone's cross-section is scaled, keeping bone length — exactly as the original engine.
        /// Call after <see cref="Setup"/> (needs the skeleton bone names). Recomputes the per-bone scale matrices.
        /// </summary>
        public void SetBodyShape(float weight)
        {
            if (_hrc == null || _scaleMat == null) return;
            _hasBodyScale = Mathf.Abs(weight - 1f) > 1e-4f;
            for (int i = 0; i < _hrc.Names.Length; i++)
                _scaleMat[i] = Matrix4x4.Scale(SdoBodyShape.ScaleFor(_hrc.Names[i], weight));
        }

        public void AddPart(Mesh mesh, Vector3[] bind, int[] boneIdx, float[] boneWt,
                            Dictionary<int, Matrix4x4> mshInvByHrc = null)
        {
            int bc = _hrc != null ? _hrc.Names.Length : 0;
            var part = new Part { Mesh = mesh, Bind = bind, Bi = boneIdx, Bw = boneWt, Work = new Vector3[bind.Length], Skin = new Matrix4x4[bc] };
            if (mshInvByHrc != null && bc > 0)
            {
                part.MshInv = new Matrix4x4[bc]; part.MshBind = new Matrix4x4[bc]; part.HasMsh = new bool[bc];
                foreach (var kv in mshInvByHrc)
                {
                    int b = kv.Key; if (b < 0 || b >= bc) continue;
                    part.MshInv[b] = kv.Value; part.MshBind[b] = kv.Value.inverse; part.HasMsh[b] = true;  // MSH bind = inv(MSH inv-bind)
                }
            }
            _parts.Add(part);
        }

        private float _lastMinY;
        /// <summary>Pose at <paramref name="frame"/> and return the lowest skinned vertex Y (model space) — the feet
        /// height for that pose. Used to rest the avatar's feet on the floor (honours the actual skin mode + pose).</summary>
        public float FeetYAt(float frame) { if (_hrc == null || _parts.Count == 0) return 0f; Pose(frame); return _lastMinY; }

        /// <summary>Find a bone index by name (for attaching hand-trail anchors etc.).</summary>
        public int BoneIndex(string name) => _hrc != null && _hrc.Index.TryGetValue(name, out int i) ? i : -1;

        /// <summary>Animated (model-space) position of a bone by name (after the last Pose). Used to anchor the dancer's
        /// chest to the .cv camera framing point.</summary>
        public Vector3 BoneModelPos(string name)
        { int i = BoneIndex(name); return (i >= 0 && _animWorld != null && i < _animWorld.Length) ? (Vector3)_animWorld[i].GetColumn(3) : Vector3.zero; }


        /// <summary>Register a child transform that tracks a bone's animated (model-space) position each frame.</summary>
        public void AddAnchor(int bone, Transform t) { if (bone >= 0) _anchors.Add((bone, t)); }

        /// <summary>
        /// The .msh per-vertex bone indices are a per-PART palette, not direct HRC indices. The palette
        /// (palette-idx -> HRC bone) lives in the skin-info footer; we locate the right run by matching each
        /// palette slot's weighted vertex centroid to the nearest HRC bone (mesh + skeleton share bind space).
        /// Verified in Python: gives exact palettes (feet->foot bones, hand->finger bones, etc.). Remaps in place.
        /// </summary>
        public static void RemapPalette(int[] boneIdx, float[] boneWt, Vector3[] bind, int[] footer, Vector3[] hrcBindPos, int boneCount)
        {
            int vc = bind.Length;
            int psize = 0;
            for (int i = 0; i < vc * 4; i++) if (boneWt[i] > 0f && boneIdx[i] + 1 > psize) psize = boneIdx[i] + 1;
            if (psize <= 0) return;
            var cen = new Vector3[psize]; var wsum = new float[psize];
            for (int v = 0; v < vc; v++)
                for (int k = 0; k < 4; k++)
                {
                    float w = boneWt[v * 4 + k]; if (w <= 0f) continue;
                    int pi = boneIdx[v * 4 + k]; if (pi < 0 || pi >= psize) continue;
                    cen[pi] += w * bind[v]; wsum[pi] += w;
                }
            for (int p = 0; p < psize; p++) if (wsum[p] > 0f) cen[p] /= wsum[p];

            int[] best = null; float bestSc = float.MaxValue;
            if (footer != null)
                for (int s = 0; s + psize <= footer.Length; s++)
                {
                    bool ok = true;
                    for (int p = 0; p < psize && ok; p++)
                    {
                        int val = footer[s + p];
                        if (val < 0 || val >= boneCount) { ok = false; break; }
                        for (int q = 0; q < p; q++) if (footer[s + q] == val) { ok = false; break; }  // palette bones are DISTINCT
                    }
                    if (!ok) continue;
                    float sc = 0f;
                    for (int p = 0; p < psize; p++) if (wsum[p] > 0f) sc += (hrcBindPos[footer[s + p]] - cen[p]).sqrMagnitude;
                    if (sc < bestSc) { bestSc = sc; best = new int[psize]; for (int p = 0; p < psize; p++) best[p] = footer[s + p]; }
                }
            if (best == null)   // no footer run -> direct proximity (nearest HRC bone per palette slot)
            {
                best = new int[psize];
                for (int p = 0; p < psize; p++)
                {
                    int nb = 0; float nd = float.MaxValue;
                    if (wsum[p] > 0f) for (int b = 0; b < boneCount; b++) { float dd = (hrcBindPos[b] - cen[p]).sqrMagnitude; if (dd < nd) { nd = dd; nb = b; } }
                    best[p] = nb;
                }
            }
            for (int i = 0; i < vc * 4; i++) { int pi = boneIdx[i]; boneIdx[i] = (pi >= 0 && pi < psize) ? best[pi] : 0; }
        }

        private void LateUpdate()
        {
            if (_hrc == null) return;
            float t;
            if (Dps != null && Dps.Rows != null && Dps.Rows.Length > 0 && MotResolver != null && DanceTimeSec != null)
            {
                float dt = DanceTimeSec();
                bool stopped = DanceEnabled != null && !DanceEnabled();   // 8-beat settlement decided to stop
                if (RestMot != null && (stopped || dt < 0f || dt >= Dps.Total))   // stopped, or before/after the choreography -> standby idle
                {
                    _mot = RestMot;
                    t = RestMot.MaxTime > 0f ? (Time.time * Fps) % (RestMot.MaxTime + 1f) : 0f;
                }
                else
                {
                    Dps.Sample(dt, out string motName, out float frame);   // choreography: switch clip + frame
                    if (!string.IsNullOrEmpty(motName)) { var m = MotResolver(motName); if (m != null) _mot = m; }
                    t = frame;
                }
            }
            else if (FrameOverride >= 0f) t = FrameOverride;
            else if (Animate && _mot != null && _mot.MaxTime > 0f) t = (Time.time * Fps) % (_mot.MaxTime + 1f);
            else t = 0f;
            MaybeStartBlend();   // crossfade if the active clip switched this frame
            Pose(t);
        }

        // If the active clip changed since last frame, snapshot the current displayed pose as the blend source and
        // begin a crossfade. Same clip reference -> continuous playback (no blend).
        private void MaybeStartBlend()
        {
            if (_mot == _lastMot) return;
            _lastMot = _mot;
            if (!_haveDisp || _dispLocal == null) return;     // nothing displayed yet -> nothing to blend from
            int bc = _hrc.Names.Length;
            for (int i = 0; i < bc; i++) { _blendFromQ[i] = _dispLocal[i].rotation; _blendFromP[i] = _dispLocal[i].GetColumn(3); }
            _blendStart = Time.time;
        }

        // reference mot_player.quat_to_matrix (column-vector) then m[:3,:3] = m[:3,:3].T (MOT quat is row-major).
        private static Matrix4x4 QuatToLocal(float qx, float qy, float qz, float qw)
        {
            float n = qx * qx + qy * qy + qz * qz + qw * qw;
            var m = Matrix4x4.identity;
            if (n < 1e-8f) return m;
            float s = 2f / n;
            float xx = qx * qx * s, yy = qy * qy * s, zz = qz * qz * s, xy = qx * qy * s, xz = qx * qz * s, yz = qy * qz * s, wx = qw * qx * s, wy = qw * qy * s, wz = qw * qz * s;
            // quat_to_matrix (column-vector): a[r,c]
            float a00 = 1f - (yy + zz), a01 = xy - wz, a02 = xz + wy;
            float a10 = xy + wz, a11 = 1f - (xx + zz), a12 = yz - wx;
            float a20 = xz - wy, a21 = yz + wx, a22 = 1f - (xx + yy);
            // store the TRANSPOSE of the 3x3
            m[0, 0] = a00; m[0, 1] = a10; m[0, 2] = a20;
            m[1, 0] = a01; m[1, 1] = a11; m[1, 2] = a21;
            m[2, 0] = a02; m[2, 1] = a12; m[2, 2] = a22;
            return m;
        }

        private void Pose(float t)
        {
            int bc = _hrc.Names.Length;
            float blendW = 1f;
            bool blending = _blendStart >= 0f && BlendSec > 1e-4f && _haveDisp;
            if (blending)
            {
                blendW = (Time.time - _blendStart) / BlendSec;
                if (blendW >= 1f) { _blendStart = -1f; blending = false; }     // crossfade finished
                else blendW = blendW * blendW * (3f - 2f * blendW);           // smoothstep ease
            }
            for (int i = 0; i < bc; i++)
            {
                Matrix4x4 local;     // reference mot_player.compose_local / evaluate_pose (column-vector, no axis flip)
                if (Animate && _mot != null && _mot.Bones.TryGetValue(i, out var node))
                {
                    MotLoader.SampleRot(node, t, out float qx, out float qy, out float qz, out float qw);
                    local = QuatToLocal(qx, qy, qz, qw);    // quat_to_matrix then transpose the 3x3 (MOT quat is row-major)
                    Vector3 p = node.Pc >= 1 ? MotLoader.SamplePos(node, t) : (Vector3)_hrc.LocalRest[i].GetColumn(3);
                    local[0, 3] = p.x; local[1, 3] = p.y; local[2, 3] = p.z;   // translation from the MOT pos track
                }
                else local = _hrc.LocalRest[i];             // bone not animated -> HRC rest (already column-vector)
                if (blending)   // ease the local transform from the snapshot toward the live clip (rotation slerp + translation lerp)
                {
                    Quaternion q = Quaternion.Slerp(_blendFromQ[i], local.rotation, blendW);
                    Vector3 p = Vector3.Lerp(_blendFromP[i], (Vector3)local.GetColumn(3), blendW);
                    local = Matrix4x4.TRS(p, q, Vector3.one);
                }
                if (_dispLocal != null) _dispLocal[i] = local;   // remember the displayed local (continuity for the next blend)
                // FK is UNSCALED: the skeleton keeps its true shape and a bone's body-shape scale must NOT propagate to
                // its children (decompiled Skeleton_ComputeWorldMtx_00409860 applies each bone's own scale to its world
                // matrix AFTER recursing children, so a fat neck never balloons the head). The scale enters ONLY each
                // bone's own skin matrix below — vertices are scaled about the bone's bind origin in its local axes.
                _animWorld[i] = _hrc.Parent[i] < 0 ? local : _animWorld[_hrc.Parent[i]] * local;
                _skinMat[i] = _hasBodyScale ? _animWorld[i] * _scaleMat[i] * _hrc.InvBindWorld[i]
                                            : _animWorld[i] * _hrc.InvBindWorld[i];
            }
            float minY = float.PositiveInfinity;
            foreach (var pt in _parts)
            {
                // per-part skin matrices. MSH_INVBIND (avatar_viewer.py default — set_force_skin_mode("msh_invbind")):
                // skin = pose·inv(MSH_bind); the bone rotates about its MSH bind origin so the body keeps the MSH
                // authoring proportions. Bones without an MSH bind fall back to hrc_bind (pose·inv(HRC_bind)).
                // body-shape scale is injected BETWEEN the bone's animated world and its inverse-bind so it scales the
                // vertex about the bone's own bind origin (local cross-section) without moving the bone or its children.
                for (int b = 0; b < bc; b++)
                    pt.Skin[b] = (pt.HasMsh != null && pt.HasMsh[b])
                        ? (_hasBodyScale ? _animWorld[b] * _scaleMat[b] * pt.MshInv[b] : _animWorld[b] * pt.MshInv[b])
                        : _skinMat[b];
                var work = pt.Work;
                for (int k = 0; k < pt.Bind.Length; k++)
                {
                    Vector3 bp = pt.Bind[k]; Vector3 acc = Vector3.zero;
                    for (int j = 0; j < 4; j++)
                    {
                        float w = pt.Bw[k * 4 + j]; if (w == 0f) continue;
                        int bi = pt.Bi[k * 4 + j]; if (bi < 0 || bi >= bc) continue;
                        acc += w * pt.Skin[bi].MultiplyPoint3x4(bp);
                    }
                    work[k] = acc; if (acc.y < minY) minY = acc.y;
                }
                pt.Mesh.vertices = work;
                pt.Mesh.RecalculateBounds();
            }
            if (!float.IsInfinity(minY)) _lastMinY = minY;   // lowest skinned vertex (model space) -> floor reference
            _haveDisp = true;                                 // _dispLocal now holds a valid pose to blend from next switch
            // move trail anchors to their bone's animated WORLD position (anchors live at scene scale 1 so
            // the TrailRenderer width stays in world units, undistorted by the avatar's scale).
            foreach (var (bone, at) in _anchors) if (at) at.position = transform.TransformPoint((Vector3)_animWorld[bone].GetColumn(3));
        }
    }
}
