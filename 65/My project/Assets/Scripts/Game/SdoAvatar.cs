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
        // rigid bone-followers: each frame the target's LOCAL transform = the bone's animated model-space world matrix
        // (full TRS). Used to carry a rigid (no-weight) mapobj mesh on a single bone so its .mot plays (e.g. a screen
        // that spins 360° yaw). The target is parented under this avatar's transform, so it picks up the instance pose.
        private readonly List<(int bone, Transform t, bool applyBindScale)> _boneFollowers = new List<(int, Transform, bool)>();
        // Rest-relative "skin" followers: each frame the target's LOCAL transform = the bone's REST-RELATIVE rigid motion
        // (animWorld·invBindWorld — identity at the bind pose). Unlike _boneFollowers (full bone TRS, which double-offsets a
        // child authored in body model space), this lets a prop AUTHORED in body model space — the animated _G wing rig,
        // whose root sits at the back attach point — rigidly TRACK a body bone: at bind it stays at its authored spot, and
        // as the body idle bobs / walks / leans, the wing follows that bone. (Fixes "翅膀沒跟著身體上下動" — the old identity
        // mount pinned the rig to the static body root, so the idle's vertical bob detached it.) Body-shape scale excluded.
        private readonly List<(int bone, Transform t)> _skinFollowers = new List<(int, Transform)>();
        // Like _boneFollowers, but instead of a TRS Transform (which Unity decomposes into rotation+lossyScale and
        // CANNOT reconstruct a rotation+non-uniform-scale matrix — a spinning DING wheel sheared/squished each frame),
        // these BAKE the bone's FULL model-space matrix into the mesh verts every frame (faithful for any matrix).
        private readonly List<(int bone, Mesh mesh, Vector3[] src, bool applyScale)> _meshBakers = new List<(int, Mesh, Vector3[], bool)>();
        private Matrix4x4[] _animWorld, _rigidWorld, _skinMat;
        private Vector3[] _motScale;   // per-bone MOT scale this frame (Vector3.one unless the bone's scale track varies)
        private bool[] _motScaleActive;   // per-bone: does the .mot drive this bone's scale? (then USE it, ignore bind scale)
        // body-shape (體型): per-bone local scale that fattens/thins the dancer's cross-section without changing bone
        // length — faithful port of decompiled AvatarHelper_ScaleBones (see SdoBodyShape). identity until SetBodyShape.
        private Matrix4x4[] _scaleMat; private bool _hasBodyScale;
        // motion crossfade: when the active clip switches (dance<->idle, dance clip A->B) blend each bone's LOCAL
        // transform from the outgoing displayed pose toward the new clip over BlendSec, so it isn't a hard cut.
        public float BlendSec = 1.0f;                        // crossfade duration (s); 0 -> instant
        private Matrix4x4[] _dispLocal;                      // last displayed per-bone local (blend source / continuity)
        private Quaternion[] _blendFromQ; private Vector3[] _blendFromP;   // snapshot of the displayed pose at the switch
        private float _blendStart = -1f;                     // Time.time the current crossfade began (<0 = none)
        private float _blendDur = 1f;                         // duration (s) of the crossfade in progress, captured at its start
        private float _blendNextSec = -1f;                    // >=0 -> the NEXT clip switch crossfades over this (one-shot), then reverts to BlendSec
        private bool _snapNextBlend;                          // true -> next clip switch is a HARD CUT (see SnapNextClip)
        private bool _haveDisp;                              // _dispLocal holds a valid previous pose
        private MotLoader _lastMot;                          // active clip last frame (to detect a switch)
        // DPS slice identity last frame. The pose source is (clip, choreography, row) — NOT the clip alone: two
        // consecutive DPS rows often share one .mot, and comparing MotLoader references would hard-cut that seam.
        // The original always restarts its blend on a slice boundary (see DpsLoader.Sample), and ~1% of the official
        // rows step BACKWARD in frame there, which read as the dancer rewinding a beat before carrying on.
        private DpsLoader _lastDps; private int _lastDpsRow = -1;

        public float Fps = 30f;          // MOT frame rate (time = integer frame index)
        public bool Animate = true;      // false -> hold bind pose (verification)
        public float PhaseOffsetSec = 0f;  // per-instance start-time offset for the auto-loop, so a crowd of NPCs
                                           // sharing one clip doesn't move in lockstep (the original staggers them)
        public float FrameOverride = -1; // >=0 -> use this frame instead of the wall clock (DPS sync hook)

        /// <summary>The clip this avatar posed with on the last LateUpdate (idle / walk / one-shot / DPS clip alike).</summary>
        public MotLoader CurrentClip => _mot;
        /// <summary>The frame this avatar posed at on the last LateUpdate. Together with <see cref="CurrentClip"/> this
        /// is the avatar's whole visible pose, so a second avatar fed the same pair shows the SAME pose — which is how
        /// the room head portrait stays locked to the room avatar (官方頭貼畫的就是本體, so it cannot drift).</summary>
        public float CurrentPoseTime { get; private set; }

        // ---- GPU skinning (experimental) ----
        // When true, the per-vertex blend is done by Unity's SkinnedMeshRenderer on the GPU instead of the CPU
        // loop below: each frame we still compute the bone FK (animWorld) on the CPU — cheap, O(bones) — and write
        // it to a FLAT array of bone Transforms; the SMR's bindposes are the SAME invbind matrices the CPU path
        // uses, so the GPU reproduces skin[b] = animWorld[b]·(scale)·invbind[b] exactly. No per-vertex CPU work and
        // no mesh.vertices upload. FeetYAt (CPU vertex read-back) is unavailable in this mode — fine for props that
        // are placed at explicit positions. The flat bones inherit the avatar root's (uniform) instance scale, and
        // a bone's body-shape scale rides in its localScale, so both factor through correctly.
        public bool GpuSkinning = false;
        private Transform[] _gpuBones;
        private bool _gpuInit;
        private readonly List<SkinnedMeshRenderer> _gpuParts = new List<SkinnedMeshRenderer>();

        // DPS dance-sync: choreography sequencing motion slices to the song clock
        public DpsLoader Dps;
        public System.Func<string, MotLoader> MotResolver;   // motion name -> loaded MotLoader (cached by caller)
        public System.Func<float> DanceTimeSec;              // current song time in seconds (negative before the dance starts)
        public MotLoader RestMot;                            // standby idle clip, looped before the DPS dance starts and after it ends (rest cat 0x15)
        public System.Func<bool> DanceEnabled;               // returns false -> hold the standby idle even inside the DPS window (8-beat dance-gate stop)

        /// <summary>目前顯示的 clip **就是 <see cref="RestMot"/> 這個物件本身嗎** —— 比參照,不比內容。
        /// 存在的理由只有一個:守住「同一段 .MOT 不可以被 parse 成兩個實例」(換動作的偵測是比參照的,
        /// 兩個實例 = 誤判成換了動作 = 多混一次 crossfade;見 AvatarClipIdentityTests 與 2e90e0d)。
        ///
        /// 🔴 **不要拿它當「現在沒在跳舞」的閘門。** 之前的飛行懸浮就是那樣用的(<c>IsRestPose ? 0 : Δ</c>),
        /// 待機與勝負定格被判成 rest 而抬 0、跳舞才抬 → 人一沉一浮。懸浮與姿勢無關(見 c5fd72f /
        /// ScreenGameplay.UpdateFlyHover / FlyHoverGameplayTest)。</summary>
        public bool IsShowingRestClip => RestMot != null && _mot == RestMot;

        // one-shot motion (結算 win/lose 定格 pose, decompiled cat5/cat4): play a single clip once from t=0; when
        // hold, clamp on the last frame (定格). Takes priority over DPS/idle/override while active. Set via
        // PlayOneShot, cleared via ClearOneShot (e.g. when the background replay resumes the DPS dance).
        private MotLoader _oneShot; private float _oneShotStart = -1f; private bool _oneShotHold;
        /// <summary>True once a held one-shot pose has reached its last frame (定格完成).</summary>
        public bool OneShotHeld => _oneShot != null && _oneShotHold && _oneShotStart >= 0f
            && (Time.time - _oneShotStart) * Fps >= _oneShot.MaxTime;


        /// <summary>Play a single motion clip once from t=0. When <paramref name="hold"/> it clamps on the last
        /// frame; otherwise it loops. Takes priority over DPS/idle until <see cref="ClearOneShot"/>.</summary>
        public void PlayOneShot(MotLoader mot, bool hold)
        {
            if (mot == null) return;
            _oneShot = mot; _oneShotHold = hold; _oneShotStart = Time.time;
        }
        public void ClearOneShot() { _oneShot = null; _oneShotStart = -1f; }

        /// <summary>Force the NEXT clip switch to be a HARD CUT (no crossfade). Used when the result-screen
        /// background replay resumes the DPS dance, so the win/lose 定格 pose snaps straight to the dance pose
        /// instead of blending through it over BlendSec.</summary>
        public void SnapNextClip() { _snapNextBlend = true; }

        /// <summary>Make ONLY the next clip switch crossfade over <paramref name="sec"/> seconds instead of
        /// <see cref="BlendSec"/> (one-shot, mirrors <see cref="SnapNextClip"/>; reverts to BlendSec afterwards). Used
        /// for the 脱下飛行翅膀 settle (flystay→地面 idle over 1s) without changing the room's default idle↔walk blend.</summary>
        public void BlendNextClip(float sec) { _blendNextSec = Mathf.Max(0f, sec); }

        /// <summary>Pose <paramref name="from"/> now as the crossfade SOURCE and mark it the displayed clip, so the very
        /// next <see cref="SetClip"/> to a different clip crossfades FROM this pose. Used when an avatar is rebuilt with a
        /// new outfit but should visually settle from the previous motion (脱飛行翅膀:flystay 浮空 → 地面 idle) instead of
        /// popping straight to the new idle. No-op without a skeleton/clip.</summary>
        public void PrimeBlendFrom(MotLoader from)
        {
            if (_hrc == null || from == null) return;
            _mot = from;
            _blendStart = -1f;      // not blending yet — we're only establishing the displayed pose
            // 用**迴圈當下**那一幀,不是第 0 幀:被取代掉的那隻 avatar 顯示的就是這個相位(它也是 wall-clock 迴圈),
            // 從第 0 幀起混色 = 一開始就跳到一個畫面上根本沒出現過的姿勢。
            Pose(AutoLoopFrame);    // display 'from' → _dispLocal = from's pose, _haveDisp = true
            _lastMot = from;        // so SetClip(target) next is seen as a switch → crossfade from this pose
        }

        /// <summary>自動迴圈目前算到第幾幀 —— 與 <see cref="LateUpdate"/> 的待機/走路迴圈同一條公式(唯一來源)。
        /// 重建 avatar 時用它把顯示姿勢對回「畫面上原本那一格」,混色才不會從第 0 幀起跳。</summary>
        public float AutoLoopFrame =>
            LoopFrame(Time.time, PhaseOffsetSec, Fps, _mot != null ? _mot.MaxTime : 0f);

        /// <summary>Switch the active LOOPING clip (no DPS): used by the waiting room to flip between the standby idle
        /// and the walk clip as the local player moves. The change is picked up next LateUpdate, which crossfades from
        /// the displayed pose via MaybeStartBlend (so idle↔walk is a smooth blend, not a hard cut). No-op if null or
        /// already the active clip.</summary>
        public void SetClip(MotLoader mot) { if (mot != null) _mot = mot; }

        /// <summary>
        /// Pose the standby idle ONCE on creation and arm it as the active clip, so the first LateUpdate continues
        /// the idle with NO crossfade. Without this, the feet/chest measurement pose (frame 0 of the fallback clip,
        /// which is near the bind/T-pose) becomes the blend source, and the first idle visibly crossfades FROM a
        /// T-pose ("剛進去看到 T-pose 一下"). Call after Setup + RestMot + AddPart. No-op without an idle clip.
        /// </summary>
        public void PoseInitialIdle()
        {
            if (_hrc == null || RestMot == null) return;
            _mot = RestMot;
            _lastMot = RestMot;     // frame 1's _mot == _lastMot → MaybeStartBlend skips (no crossfade on the first idle)
            _blendStart = -1f;      // cancel any blend primed by the earlier measurement poses
            Pose(0f);               // display the idle now so the mesh is never left at the bind/T-pose
        }

        /// <summary>
        /// 把 <paramref name="mot"/> 當成「這隻角色本來就在播的動作」立刻擺好:設成作用中的 clip、
        /// 同步 <c>_lastMot</c>(第一次 LateUpdate 不會偵測到切換 → **不混色**),並擺到迴圈當下那一格。
        ///
        /// 用在角色**剛出現在畫面上**的時候。<see cref="PoseInitialIdle"/> 只認 <see cref="RestMot"/>(而且是第 0 幀),
        /// 所以建好之後才決定要播別支動作的路徑(房間旁觀者的 cat-0x21 看戲姿勢)以前是走 <see cref="SetClip"/> ——
        /// 那會被當成換動作,從 Build 的站立 idle 混 <see cref="BlendSec"/> 過去:使用者看到的是
        /// 「一進房間,原本在旁觀的人從立正慢慢彎下去坐好」。一進畫面就該是最終姿勢。
        ///
        /// 也把 <see cref="SnapNextClip"/>/<see cref="BlendNextClip"/> 的一次性旗標清掉 —— 這裡本來就是硬切,
        /// 留著會被下一次真的換動作(開始走路)吃掉。
        /// </summary>
        /// <param name="frame">要擺哪一格;負數 = 迴圈當下那一格(<see cref="AutoLoopFrame"/>)。
        /// 房間頭貼是鏡射本體的姿勢,傳本體那一格才不會第一幀對不上。</param>
        public void PoseInitialClip(MotLoader mot, float frame = -1f)
        {
            if (_hrc == null || mot == null) return;
            _mot = mot;
            _lastMot = mot;
            _blendStart = -1f;
            _snapNextBlend = false; _blendNextSec = -1f;
            Pose(frame >= 0f ? frame : AutoLoopFrame);   // 預設用迴圈當下那一格(不是第 0 幀:同一批人才不會整齊得像複製人,見 PhaseOffsetSec)
        }

        /// <summary>下一次 LateUpdate 會不會把現在掛著的 clip 當成「換了動作」(→ 混色)。
        /// 測試用的觀測點:一進畫面就該擺好的姿勢不該留下這個待處理的切換。</summary>
        public bool ClipSwitchPending => !ReferenceEquals(_mot, _lastMot);

        /// <summary>還有沒有排著一次性的換動作設定(<see cref="SnapNextClip"/> 硬切 / <see cref="BlendNextClip"/> 指定長度)。
        /// 測試用的觀測點:這種旗標沒被用掉就會留到**下一次**換動作才生效(走路那一下突然不混色)。</summary>
        public bool OneShotBlendPending => _snapNextBlend || _blendNextSec >= 0f;

        public void Setup(HrcLoader hrc, MotLoader mot)
        {
            _hrc = hrc; _mot = mot;
            int bc = hrc.Names.Length;
            _animWorld = new Matrix4x4[bc]; _rigidWorld = new Matrix4x4[bc]; _skinMat = new Matrix4x4[bc];
            _motScale = new Vector3[bc]; for (int i = 0; i < bc; i++) _motScale[i] = Vector3.one;
            _motScaleActive = new bool[bc];
            _dispLocal = new Matrix4x4[bc]; _blendFromQ = new Quaternion[bc]; _blendFromP = new Vector3[bc];
            _haveDisp = false; _lastMot = mot; _blendStart = -1f; _lastDps = null; _lastDpsRow = -1;
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

        // ---- GPU skinning setup ----

        // Bake a part's bind data onto its source mesh ONCE (shared across instances of a group): bindposes =
        // per-HRC-bone invbind (MSH inverse-bind where the part supplies one, else the HRC world inverse-bind —
        // matching the CPU path's per-bone choice), and 4-bone boneWeights from the part's palette. Idempotent.
        public static void PrepareGpuMesh(Mesh mesh, HrcLoader hrc, int[] boneIdx, float[] boneWt,
                                          Dictionary<int, Matrix4x4> mshInvByHrc)
        {
            if (mesh == null || hrc == null) return;
            int bc = hrc.Names.Length;
            var binds = new Matrix4x4[bc];
            for (int b = 0; b < bc; b++) binds[b] = hrc.InvBindWorld[b];
            if (mshInvByHrc != null)
                foreach (var kv in mshInvByHrc) if (kv.Key >= 0 && kv.Key < bc) binds[kv.Key] = kv.Value;  // MSH retarget invbind
            mesh.bindposes = binds;
            int vc = mesh.vertexCount;
            var bw = new BoneWeight[vc];
            for (int v = 0; v < vc; v++)
            {
                SetInfluence(ref bw[v], 0, boneIdx, boneWt, v, bc);
                SetInfluence(ref bw[v], 1, boneIdx, boneWt, v, bc);
                SetInfluence(ref bw[v], 2, boneIdx, boneWt, v, bc);
                SetInfluence(ref bw[v], 3, boneIdx, boneWt, v, bc);
            }
            mesh.boneWeights = bw;
        }

        private static void SetInfluence(ref BoneWeight w, int k, int[] bi, float[] bw, int v, int bc)
        {
            int idx = bi[v * 4 + k]; float wt = bw[v * 4 + k];
            if (idx < 0 || idx >= bc) { idx = 0; wt = 0f; }   // invalid influence -> zero weight (matches the CPU skip)
            switch (k)
            {
                case 0: w.boneIndex0 = idx; w.weight0 = wt; break;
                case 1: w.boneIndex1 = idx; w.weight1 = wt; break;
                case 2: w.boneIndex2 = idx; w.weight2 = wt; break;
                default: w.boneIndex3 = idx; w.weight3 = wt; break;
            }
        }

        private void EnsureGpuBones()
        {
            if (_gpuInit) return;
            _gpuInit = true;
            int bc = _hrc.Names.Length;
            _gpuBones = new Transform[bc];
            for (int b = 0; b < bc; b++)
            {
                var go = new GameObject("b" + b);
                go.transform.SetParent(transform, false);   // flat (no nesting): each holds the full model-space bone world
                _gpuBones[b] = go.transform;
            }
        }

        // Create a SkinnedMeshRenderer for an already-prepared mesh, sharing this avatar's flat bone array. The
        // caller assigns materials on the returned renderer. The mesh's bindposes/boneWeights must be set first
        // (PrepareGpuMesh). Drives on the GPU each frame from the bones written by WriteGpuBones.
        public SkinnedMeshRenderer AddGpuSmr(Mesh mesh, string name)
        {
            EnsureGpuBones();
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = _gpuBones;
            smr.rootBone = transform;
            smr.updateWhenOffscreen = true;   // bones are external; recompute bounds so it isn't wrongly culled
            _gpuParts.Add(smr);
            return smr;
        }

        // Push the current frame's bone FK to the SMR bone Transforms. animWorld is the bone's model-space world
        // (root = identity in the FK), so the flat bone's LOCAL transform = animWorld; the avatar root then applies
        // the instance position/scale. Body-shape scale (if any) rides in the bone's localScale.
        private void WriteGpuBones()
        {
            if (_gpuBones == null) return;
            int bc = _hrc.Names.Length;
            for (int b = 0; b < bc; b++)
            {
                var tb = _gpuBones[b]; if (tb == null) continue;
                Matrix4x4 m = _animWorld[b];
                tb.localPosition = m.GetColumn(3);
                tb.localRotation = m.rotation;
                tb.localScale = _hasBodyScale ? new Vector3(_scaleMat[b].m00, _scaleMat[b].m11, _scaleMat[b].m22) : Vector3.one;
            }
        }

        private float _lastMinY, _lastMaxY;
        /// <summary>Pose at <paramref name="frame"/> and return the lowest skinned vertex Y (model space) — the feet
        /// height for that pose. Used to rest the avatar's feet on the floor (honours the actual skin mode + pose).</summary>
        public float FeetYAt(float frame) { if (_hrc == null || _parts.Count == 0) return 0f; Pose(frame); return _lastMinY; }

        /// <summary>Same as <see cref="FeetYAt(float)"/> but measured with <paramref name="clip"/> instead of the
        /// active clip (restored afterwards). Callers that need a STABLE floor reference must use this: FeetYAt poses
        /// whatever clip is live, so measuring while a FLOATING clip is active (飛行翅膀 flystay, feet tucked up) folds
        /// that clip's altitude into the "feet offset" — and subtracting it pins the floating pose back on the floor,
        /// cancelling the hover. Measure against the ground idle and every clip's height reads relative to standing.</summary>
        public float FeetYAt(float frame, MotLoader clip)
        {
            if (clip == null) return FeetYAt(frame);
            var prev = _mot;
            _mot = clip;
            try { return FeetYAt(frame); }
            finally { _mot = prev; }
        }
        /// <summary>Pose at <paramref name="frame"/> and return the highest skinned vertex Y (model space) — the
        /// head/hair-top height for that pose. Used with FeetYAt to measure body height for per-model framing.</summary>
        public float HeadYAt(float frame) { if (_hrc == null || _parts.Count == 0) return 0f; Pose(frame); return _lastMaxY; }

        /// <summary>Find a bone index by name (for attaching hand-trail anchors etc.).</summary>
        public int BoneIndex(string name) => _hrc != null && _hrc.Index.TryGetValue(name, out int i) ? i : -1;

        /// <summary>Animated (model-space) position of a bone by name (after the last Pose). Used to anchor the dancer's
        /// chest to the .cv camera framing point.</summary>
        public Vector3 BoneModelPos(string name)
        { int i = BoneIndex(name); return (i >= 0 && _animWorld != null && i < _animWorld.Length) ? (Vector3)_animWorld[i].GetColumn(3) : Vector3.zero; }

        /// <summary>The loaded HRC skeleton (bone names, parents, rest/bind matrices). Exposed so an external retarget
        /// (e.g. driving an MMD model — <see cref="MmdAvatar"/>) can pair each HRC bone with its bind pose.</summary>
        public HrcLoader Hrc => _hrc;

        /// <summary>This bone's animated model-space world matrix from the last <see cref="Pose"/> (root = identity in
        /// the FK). Identity if not yet posed / out of range. The retarget reads its rotation (world-space delta vs the
        /// bind) and, for the root, its translation.</summary>
        public Matrix4x4 BoneAnimWorld(int i)
            => (_animWorld != null && i >= 0 && i < _animWorld.Length) ? _animWorld[i] : Matrix4x4.identity;

        /// <summary>Evaluate one frame immediately so newly attached props/effects can start from a valid bone pose
        /// before the first LateUpdate.</summary>
        public void PoseFrame(float frame)
        {
            if (_hrc == null || _animWorld == null) return;
            Pose(frame);
        }

        /// <summary>Register a child transform that tracks a bone's animated (model-space) position each frame.</summary>
        public void AddAnchor(int bone, Transform t) { if (bone >= 0) _anchors.Add((bone, t)); }

        /// <summary>Make <paramref name="t"/> rigidly follow a bone's animated world transform each frame (full TRS) —
        /// for a no-weight mapobj mesh attached to one bone (its .mot then plays as rigid motion, e.g. a yaw spin).</summary>
        public void AddBoneFollower(int bone, Transform t, bool applyBindScale = true)
        {
            if (bone >= 0 && t != null) _boneFollowers.Add((bone, t, applyBindScale));
        }

        /// <summary>Number of skeleton bones (for iterating <see cref="BoneBindModelPos"/> to pick an attach bone).</summary>
        public int BoneCount => _hrc != null && _hrc.InvBindWorld != null ? _hrc.InvBindWorld.Length : 0;

        /// <summary>Model-space (bind pose) position of <paramref name="bone"/> — lets a caller pick the body bone nearest
        /// a prop's authored anchor (e.g. the wing rig's back attach point) without hard-coding a bone name.</summary>
        public Vector3 BoneBindModelPos(int bone)
            => (_hrc != null && _hrc.InvBindWorld != null && bone >= 0 && bone < _hrc.InvBindWorld.Length)
                ? (Vector3)_hrc.InvBindWorld[bone].inverse.GetColumn(3) : Vector3.zero;

        /// <summary>Make <paramref name="t"/> rigidly follow <paramref name="bone"/>'s REST-RELATIVE motion each frame
        /// (identity at the bind pose), so a child authored in this avatar's body model space (the animated <c>_G</c> wing
        /// rig) stays glued to that bone as the body animates — without the double-offset a full-TRS
        /// <see cref="AddBoneFollower"/> would cause. Body-shape scale is excluded (clean rotation+translation follow).</summary>
        public void AddSkinFollower(int bone, Transform t)
        {
            if (bone >= 0 && t != null) _skinFollowers.Add((bone, t));
        }

        /// <summary>Rigidly attach a mesh to a bone by BAKING the bone's full model-space matrix into the mesh verts each
        /// frame (no TRS decomposition) — use for animated rigid props that ROTATE on a scaled chain, where a Transform
        /// follower would shear/squish (the SCN0011 DING wheel). <paramref name="mesh"/> is a per-instance clone whose
        /// verts get overwritten; <paramref name="src"/> is its original bone-local verts. The mesh's GameObject must sit
        /// at IDENTITY under the avatar root (the baked verts are already in model space).</summary>
        public void AddBoneMeshBaker(int bone, Mesh mesh, Vector3[] src, bool applyScale = true)
        {
            if (bone >= 0 && mesh != null && src != null) _meshBakers.Add((bone, mesh, src, applyScale));
        }

        // Bake each registered mesh from the just-computed bone world matrix (scale-bearing _rigidWorld when the prop's
        // bind chain carries scale, else the scale-free _animWorld). Called from Pose after the FK, on CPU and GPU paths.
        private void UpdateMeshBakers()
        {
            if (_meshBakers.Count == 0 || _animWorld == null) return;
            for (int k = 0; k < _meshBakers.Count; k++)
            {
                var (bone, mesh, src, applyScale) = _meshBakers[k];
                if (mesh == null || src == null || bone < 0 || bone >= _animWorld.Length) continue;
                Matrix4x4 m = (applyScale && _rigidWorld != null && bone < _rigidWorld.Length) ? _rigidWorld[bone] : _animWorld[bone];
                var dst = mesh.vertices;
                int n = Mathf.Min(dst.Length, src.Length);
                for (int i = 0; i < n; i++) dst[i] = m.MultiplyPoint3x4(src[i]);
                mesh.vertices = dst;
                mesh.RecalculateBounds();
            }
        }

        // Drive each rest-relative follower from animWorld·invBindWorld (= the bone's motion DELTA from its bind pose —
        // identity at bind, so a child authored in body model space stays at its authored spot at rest and tracks the bone
        // as the body animates). Body-shape scale is excluded → a clean rigid (rotation+translation) follow. Called from
        // Pose after the FK loop, on both the CPU and GPU paths (same site as UpdateBoneFollowers).
        private void UpdateSkinFollowers()
        {
            if (_skinFollowers.Count == 0 || _animWorld == null || _hrc == null || _hrc.InvBindWorld == null) return;
            for (int k = 0; k < _skinFollowers.Count; k++)
            {
                var (bone, t) = _skinFollowers[k];
                if (t == null || bone < 0 || bone >= _animWorld.Length || bone >= _hrc.InvBindWorld.Length) continue;
                Matrix4x4 m = _animWorld[bone] * _hrc.InvBindWorld[bone];
                t.localPosition = m.GetColumn(3);
                t.localRotation = m.rotation;
                t.localScale = Vector3.one;
            }
        }

        // Drive every rigid bone-follower from the just-computed _animWorld (model space; root = identity). Called from
        // Pose after the FK loop, on both the CPU and GPU paths.
        private void UpdateBoneFollowers()
        {
            if (_boneFollowers.Count == 0 || _animWorld == null) return;
            for (int k = 0; k < _boneFollowers.Count; k++)
            {
                var (bone, t, applyBindScale) = _boneFollowers[k];
                if (t == null || bone < 0 || bone >= _animWorld.Length) continue;
                Matrix4x4 m = (_rigidWorld != null && bone < _rigidWorld.Length) ? _rigidWorld[bone] : _animWorld[bone];
                t.localPosition = m.GetColumn(3);
                t.localRotation = m.rotation;
                // The FK is intentionally scale-free (see Pose), so _animWorld drops the SCALE a rigid prop's bind
                // chain carries on a parent bone (3ds-Max rigs put the prop's scale on a parent; the leaf is scale 1).
                // Without it an animated rigid prop renders at full mesh size — SCN0015 trees (bind scale ~0.1)
                // ballooned ~10×; SCN0011 ceiling props mis-sized. Re-inject the accumulated bind scale (what the
                // STATIC bake uses via BindWorld). Props whose chain has no scale (e.g. the spinning sea screen,
                // bind scale 1) are unaffected — so this can only correct the already-wrong, scaled-chain props.
                // SCALE: if the .mot drives this bone's scale, USE the MOT scale DIRECTLY — it IS the live scale (the
                // SCN0008 delta_line bars extend via scale.Y 0→2.028; their HRC BIND scale.Y is 0 = the collapsed rest
                // state, so bind×mot would zero the bars out → invisible). Bones WITHOUT a MOT scale track keep the
                // bind-scale re-injection (rigid mapobjs like the SCN0014 sea screen / SCN0015 trees — unchanged).
                t.localScale = applyBindScale ? m.lossyScale : Vector3.one;
            }
        }

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
            DpsLoader dps = null; int dpsRow = -1;   // DPS slice driving this frame (null/-1 = idle, one-shot or plain looping clip)
            if (_oneShot != null && _oneShot.MaxTime > 0f)   // 結算 win/lose 定格 pose — highest priority
            {
                float f = (Time.time - _oneShotStart) * Fps;
                f = _oneShotHold ? Mathf.Min(f, _oneShot.MaxTime) : f % (_oneShot.MaxTime + 1f);
                _mot = _oneShot; MaybeStartBlend(null, -1); CurrentPoseTime = f; Pose(f); return;
            }
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
                    Dps.Sample(dt, out string motName, out float frame, out int row);   // choreography: switch clip + frame
                    if (!string.IsNullOrEmpty(motName)) { var m = MotResolver(motName); if (m != null) _mot = m; }
                    t = frame; dps = Dps; dpsRow = row;
                }
            }
            else if (FrameOverride >= 0f) t = FrameOverride;
            else if (Animate) t = AutoLoopFrame;   // 同一條公式也給 PrimeBlendFrom 用(見 AutoLoopFrame)
            else t = 0f;
            MaybeStartBlend(dps, dpsRow);   // crossfade if the clip OR the DPS slice switched this frame
            CurrentPoseTime = t;
            Pose(t);
        }

        /// <summary>Frame of a free-running looping clip at wall-clock <paramref name="timeSec"/>. Because it is driven
        /// by the shared clock (not by when the avatar was created), two avatars with the SAME phase offset, fps and
        /// clip length are always on the same frame — that is why the official result portraits breathe in unison,
        /// and why a crowd needs a per-instance <see cref="PhaseOffsetSec"/> to look independent. Pure, unit-tested.</summary>
        public static float LoopFrame(float timeSec, float phaseOffsetSec, float fps, float maxTime)
            => maxTime > 0f ? ((timeSec + phaseOffsetSec) * fps) % (maxTime + 1f) : 0f;

        /// <summary>True when the pose source changed between two frames: a different clip, a different choreography,
        /// or a different DPS row of the same clip. Says nothing about whether the change needs a crossfade — a new row
        /// that runs straight on through the same clip does not (see <see cref="SliceNeedsBlend"/>). Pure, unit-tested.</summary>
        public static bool PoseSourceChanged(object prevClip, object clip, object prevDps, object dps, int prevRow, int row)
            => !ReferenceEquals(prevClip, clip) || !ReferenceEquals(prevDps, dps) || prevRow != row;

        /// <summary>
        /// True when a pose-source change has to be crossfaded. Every change does EXCEPT one: the next slice of the
        /// same choreography picking the same clip up where the last one left it (DpsLoader.SliceContinues) — there the
        /// frame ramp is already continuous, so blending only freezes the pose and then overshoots catching up. That
        /// shows up as a stall mid-move in fast phrases (wdance0238's 432-536 backflip, which 15085 / 10731 / 12459 all
        /// cut through the middle of). Pure, unit-tested.
        /// </summary>
        public static bool SliceNeedsBlend(object prevClip, object clip, DpsLoader prevDps, DpsLoader dps, int prevRow, int row)
        {
            if (!PoseSourceChanged(prevClip, clip, prevDps, dps, prevRow, row)) return false;
            if (dps == null || !ReferenceEquals(prevDps, dps)) return true;      // entering/leaving a dance, or a new script
            if (!ReferenceEquals(prevClip, clip)) return true;                    // a genuinely different clip
            return !dps.SliceContinues(prevRow, row);
        }

        // If the pose source (clip / choreography / DPS row) changed since last frame in a way that needs a hand-off,
        // snapshot the current displayed pose as the blend source and begin a crossfade. A slice that runs on through
        // the same clip (or no change at all) -> continuous playback, no blend.
        private void MaybeStartBlend(DpsLoader dps, int dpsRow)
        {
            bool switched = SliceNeedsBlend(_lastMot, _mot, _lastDps, dps, _lastDpsRow, dpsRow);
            _lastMot = _mot; _lastDps = dps; _lastDpsRow = dpsRow;
            if (!switched) return;
            if (_snapNextBlend) { _snapNextBlend = false; _blendStart = -1f; _blendNextSec = -1f; return; }   // hard cut requested -> no crossfade
            if (!_haveDisp || _dispLocal == null) { _blendNextSec = -1f; return; }     // nothing displayed yet -> nothing to blend from
            int bc = _hrc.Names.Length;
            for (int i = 0; i < bc; i++) { _blendFromQ[i] = _dispLocal[i].rotation; _blendFromP[i] = _dispLocal[i].GetColumn(3); }
            _blendStart = Time.time;
            _blendDur = _blendNextSec >= 0f ? _blendNextSec : BlendSec;   // one-shot override (BlendNextClip) or the default
            _blendNextSec = -1f;
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
            bool blending = _blendStart >= 0f && _blendDur > 1e-4f && _haveDisp;
            if (blending)
            {
                blendW = (Time.time - _blendStart) / _blendDur;
                if (blendW >= 1f) { _blendStart = -1f; blending = false; }     // crossfade finished
                // LINEAR, like the original (MotionDriver holds the outgoing pose's weight at clip+0xc and decays it by
                // a hard-coded 0.002/ms, i.e. 1 → 0 over 500 ms in a straight line). A smoothstep ease reads better on a
                // slow hand-off but its derivative is 0 at the start, so the first frames after a cut barely move: on the
                // wdance0238 backflip (up to 1170°/s) that turned a boundary into a visible stall — 2°/s against the
                // clip's own 160°/s — followed by an overshoot to 1.5× while the blend caught up.

            }
            for (int i = 0; i < bc; i++)
            {
                Matrix4x4 local;     // reference mot_player.compose_local / evaluate_pose (column-vector, no axis flip)
                Vector3 rigidScale;
                if (_motScale != null) { _motScale[i] = Vector3.one; _motScaleActive[i] = false; }
                if (Animate && _mot != null && _mot.Bones.TryGetValue(i, out var node))
                {
                    MotLoader.SampleRot(node, t, out float qx, out float qy, out float qz, out float qw);
                    local = QuatToLocal(qx, qy, qz, qw);    // quat_to_matrix then transpose the 3x3 (MOT quat is row-major)
                    Vector3 p = node.Pc >= 1 ? MotLoader.SamplePos(node, t) : (Vector3)_hrc.LocalRest[i].GetColumn(3);
                    local[0, 3] = p.x; local[1, 3] = p.y; local[2, 3] = p.z;   // translation from the MOT pos track
                    rigidScale = node.Sc >= 1 ? MotLoader.SampleScale(node, t) : _hrc.LocalRest[i].lossyScale;
                    if (_motScale != null && MotLoader.ScaleVaries(node)) { _motScale[i] = MotLoader.SampleScale(node, t); _motScaleActive[i] = true; }
                }
                else
                {
                    local = _hrc.LocalRest[i];             // bone not animated -> HRC rest (already column-vector)
                    rigidScale = _hrc.LocalRest[i].lossyScale;
                }
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
                Matrix4x4 rigidLocal = Matrix4x4.TRS((Vector3)local.GetColumn(3), local.rotation, rigidScale);
                _rigidWorld[i] = _hrc.Parent[i] < 0 ? rigidLocal : _rigidWorld[_hrc.Parent[i]] * rigidLocal;
                _skinMat[i] = _hasBodyScale ? _animWorld[i] * _scaleMat[i] * _hrc.InvBindWorld[i]
                                            : _animWorld[i] * _hrc.InvBindWorld[i];
            }
            UpdateBoneFollowers();   // rigid mapobj props carried on a single bone (e.g. the spinning sea screen)
            UpdateSkinFollowers();   // rest-relative followers — the animated _G wing rig tracks the body's back bone (idle bob/walk)
            UpdateMeshBakers();      // rotation-on-scaled-chain props (DING wheel) — bake the full matrix, no TRS shear
            if (GpuSkinning) { WriteGpuBones(); _haveDisp = true;   // GPU: drive the SMR bones; the GPU does the vertex blend
                foreach (var (bone, at) in _anchors) if (at) at.position = transform.TransformPoint((Vector3)_animWorld[bone].GetColumn(3));
                return; }
            float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
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
                    work[k] = acc; if (acc.y < minY) minY = acc.y; if (acc.y > maxY) maxY = acc.y;
                }
                pt.Mesh.vertices = work;
                pt.Mesh.RecalculateBounds();
            }
            if (!float.IsInfinity(minY)) { _lastMinY = minY; _lastMaxY = maxY; }   // lowest/highest skinned vertex (model space) -> floor / head-top
            _haveDisp = true;                                 // _dispLocal now holds a valid pose to blend from next switch
            // move trail anchors to their bone's animated WORLD position (anchors live at scene scale 1 so
            // the TrailRenderer width stays in world units, undistorted by the avatar's scale).
            foreach (var (bone, at) in _anchors) if (at) at.position = transform.TransformPoint((Vector3)_animWorld[bone].GetColumn(3));
        }
    }
}
