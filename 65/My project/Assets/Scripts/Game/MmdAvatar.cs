using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sdo.Game
{
    /// <summary>
    /// Displays a parsed MMD model (<see cref="PmxLoader"/>) in place of the native SDO avatar, driven by the SAME
    /// SDO motion. It builds a Unity <see cref="SkinnedMeshRenderer"/> on the MMD skeleton and RETARGETS the pose each
    /// frame from a hidden driver <see cref="SdoAvatar"/> (whose HRC skeleton is animated by the game's MOT/DPS).
    ///
    /// Retarget = AIM: for every MMD bone with a mapped child (<see cref="MmdBoneMap"/>), point the bone toward where
    /// the corresponding HRC bone points (bone→child direction from the driver's animated world positions), after a
    /// global facing yaw <c>Qroot</c>. Aim is immune to rest-pose differences — the SDO bind is a T-pose but MMD models
    /// rest in an A-pose, and a naive world-delta over-rotates the arms (they cross); aim just makes the limb point the
    /// right way. Leaf bones with no mapped child (hand/fingertips) follow their parent; the head takes the world-delta
    /// so it stays upright. MMD 付与 append bones (the leg "D" chain the mesh is skinned to) then copy their FK source's
    /// local rotation. <see cref="MmdRetargetPlan"/> owns that per-bone decision — including the two rules that keep the
    /// feet on the floor (センター takes translation only; 足首 aims at つま先).
    ///
    /// The rig is parented UNDER the driver's transform (placement/facing/walk inherited); a uniform scale matches the
    /// MMD model's height to the SDO avatar's.
    /// </summary>
    [DefaultExecutionOrder(100)]   // run our LateUpdate AFTER the driver SdoAvatar's, so _animWorld is fresh this frame
    public sealed class MmdAvatar : MonoBehaviour
    {
        public SdoAvatar Driver;
        public bool DriveRootTranslation = true;
        /// <summary>Aim retarget (default). OFF falls back to a pure world-delta (kept for A/B comparison).</summary>
        public bool UseAim = true;
        /// <summary>把腳踝 IK 回 SDO 動作原本的位置(<see cref="MmdFootIk"/>)。關掉 = 只有 aim,腳會因為 MMD 腿
        /// 比 SDO 短而踩不準 —— 留著開關是為了 A/B 對照。</summary>
        public bool FootIk = true;
        /// <summary>Show MMD sphere maps (matcap sheen/glow). Toggle live to compare.</summary>
        public bool ShowSphere = true;
        /// <summary>Cel-shading toon ramp (N·L, fixed light). Toggle live.</summary>
        public bool ShowToon = true;
        /// <summary>MMD pencil outline (inverted-hull edge). Toggle live.</summary>
        public bool ShowOutline = true;
        /// <summary>Flip the mesh UV V (uv.y = 1-uv.y) — the canonical MMD→Unity fix (PMX UVs are V-down). Toggle live
        /// to find the orientation whose atlas maps correctly (green necktie, not skin).</summary>
        public bool FlipV = true;

        /// <summary>
        /// The parts of a built MMD model that do NOT depend on which dancer wears it — the skinned MESH (172k verts for
        /// Miku), its MATERIALS (+ the expensive per-texture alpha scan that classifies them), and the head box. Built
        /// once per model and handed to every rig: with the stage dancer, the room walker, the room 頭貼, the 結算 headshot
        /// and BOTH gender previews alive, that is 6 rigs off ONE mesh instead of six copies of it.
        ///
        /// This is safe because the mesh's BINDPOSES are rig-independent: bindpose = bone.worldToLocal × mesh.localToWorld,
        /// the MMD rest bones carry identity rotation and unit scale, and the rig root's own scale/rotation/placement
        /// appears in both matrices and cancels — so it reduces to translate(−bonePos), the same for every rig regardless
        /// of its unit scale (each dancer height-matches the model differently: 3.04, 3.34, 3.02 …). What each rig DOES
        /// own is its bone Transforms, so they all pose independently off the same mesh.
        /// </summary>
        private sealed class Shared
        {
            public Mesh Mesh;
            public Vector2[] UvVerbatim, UvFlipped;
            public bool FlipVApplied = true;                 // which UV set is currently on Mesh (avoids re-uploading 172k UVs)
            public Material[] Materials;
            public bool[] Hide;                              // material not drawn (morph-hidden / overlay)
            public readonly List<KeyValuePair<Material, float>> SphereMats = new List<KeyValuePair<Material, float>>();
            public readonly List<Material> ToonMats = new List<Material>();
            public readonly List<KeyValuePair<Material, float>> EdgeMats = new List<KeyValuePair<Material, float>>();
            // 這批材質是哪個著色後端建的（見 UseLilToon），以及三個顯示開關在那個後端要寫哪個屬性 —— SetSphere/
            // SetToon/SetOutline 因此完全不用知道後端是誰，只是把各自清單裡記著的「開啟時的值」寫回這個屬性。
            public bool LilToon;
            public string SphereProp = "_SphereMode", ToonProp = "_UseToon", EdgeProp = "_EdgeSize";
            public bool HasHead; public int HeadBone = -1; public Bounds HeadLocal; public Vector3 HeadRestPos;
        }
        private static readonly Dictionary<PmxLoader, Shared> _sharedByModel = new Dictionary<PmxLoader, Shared>();
        private Shared _sh;

        /// <summary>
        /// 用 <b>lilToon</b>（Assets/lilToon，MIT）當著色後端，而不是 <c>Sdo/MmdModel</c>（MMD 固定管線的忠實移植）。
        /// 由 <c>MmdAvatarSwap</c> 從 config.ini <c>[Mmd] mmdLilToon</c> 設進來，換值要重建身體（材質是共用的，
        /// 見 <see cref="GetShared"/> 的快取比對）。翻譯規則與差在哪見 <see cref="MmdLilToon"/>。
        /// </summary>
        public static bool UseLilToon { get; set; }

        private Transform _mmdRoot;
        private Transform[] _bone;
        private Dictionary<string, int> _bip01ToBone;   // SDO 骨名 → 這具身體的骨(見 BoneForBip01)
        private int[] _parent;
        private int[] _order;
        private int[] _hrcIndex;                  // HRC bone each MMD bone is driven from, or -1
        private Quaternion[] _hrcRestInv;          // inverse(HRC bind-world rotation) — delta fallback for leaf bones
        private bool[] _aim;                       // this bone uses aim (has a mapped child)
        private int[] _aimChildHrc;                // HRC child bone index the aim targets
        private Vector3[] _aimRestDir;             // MMD rest bone→child direction (root-local, normalised)
        private bool[] _useDelta;                  // non-aimed bone drives by world-delta (root + head: need absolute
                                                   // orientation) vs following its parent (hand/foot/fingertip: stable)
        private bool[] _isPhysics;                 // hair/skirt/tie bones — owned by the cloth sim (Magica/spring); the
                                                   // retarget MUST NOT write them each frame or it fights the sim (jitter)
        private Quaternion[] _rwLocal;             // scratch: world-in-root-local rotation per bone this frame
        private Quaternion[] _animLocalRot;        // per-bone local rotation this frame (append source)
        private int[] _appendParent;               // PMX 付与 parent per bone (-1 none)
        private float[] _appendWeight;
        private int[] _appendOrder;
        private Quaternion _qroot = Quaternion.identity, _qrootInv = Quaternion.identity;
        private float _unitScale = 1f;
        private int _rootBone = -1;
        private int _hrcRootIndex = -1;
        private Vector3 _hrcRootRestPos, _rootRestLocal;
        private MmdSpringBones _spring;
        private MmdMagicaCloth _magica;   // preferred cloth solver (Magica Cloth 2); _spring is the fallback
        private MmdClothProfile _profile; // the model's physics.ini, when it has one (null = tuning converted from the .pmx)
        private bool _visible = true, _physicsOn = true;   // physics runs only when BOTH hold (independent toggles)
        private bool _ready;

        /// <summary>剛建好/剛顯示出來時,要把布料黏在動作姿勢上幾幀(見 <see cref="_settleFrames"/>)。
        /// 3 幀就夠:第 1 幀重定向把身體擺到當下的動作,第 2、3 幀吸收驅動器自己那一兩幀的暖機
        /// (走路的角色第一幀的根位移還沒算出來)。</summary>
        private const int SettleFrames = 3;

        /// <summary>還要把布料重設到當前姿勢幾幀。&gt;0 的期間頭髮/裙擺完全不模擬,就貼在骨頭上 ——
        /// 所以「進房間的第一眼」看到的就是已經垂好的樣子,不是從半空盪下來的過程。</summary>
        private int _settleFrames;

        /// <summary>Is the MMD body currently the one being drawn (vs the native SDO body)?</summary>
        public bool Visible => _visible && _ready;

        /// <summary>Does this rig have a cloth solver at all? False for the head portraits, which are built without one
        /// (see the <c>cloth</c> argument of <see cref="Build"/>).</summary>
        public bool HasCloth => _magica != null || _spring != null;

        /// <summary>The Magica Cloth rig, when this body has one (null for the portrait rigs and for the spring-bone
        /// fallback). The debug panel asks for it to save the current tuning into the model's physics.ini.</summary>
        public MmdMagicaCloth Cloth => _magica;

        /// <summary><paramref name="cloth"/> false builds the rig with NO hair/skirt simulation — the physics bones just
        /// hold their styled rest pose and ride the head. That is what the head portraits (room 頭貼 / 結算頭貼) use: at
        /// that size the sway is invisible, and a cloth solver per rig is the most expensive part of a build.</summary>
        public static MmdAvatar Build(SdoAvatar driver, PmxLoader pmx, string textureDir, int layer, bool cloth = true,
                                      string searchRoot = null)
        {
            if (driver == null || driver.Hrc == null || pmx == null || pmx.Bones.Count == 0 || pmx.VertexCount == 0)
                return null;
            var rootGo = new GameObject("MmdAvatar");
            rootGo.transform.SetParent(driver.transform, false);
            var self = rootGo.AddComponent<MmdAvatar>();
            self.Driver = driver;
            self._mmdRoot = rootGo.transform;
            try { self.Construct(pmx, textureDir, layer, cloth, searchRoot); }
            catch (Exception e) { Debug.LogWarning("[mmd] build fail: " + e.Message + "\n" + e.StackTrace); UnityEngine.Object.Destroy(rootGo); return null; }
            return self;
        }

        private void Construct(PmxLoader pmx, string textureDir, int layer, bool cloth, string searchRoot)
        {
            float t0 = Time.realtimeSinceStartup;
            int bc = pmx.Bones.Count;
            var hrc = Driver.Hrc;

            // ---- height match ----
            float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
            foreach (var p in pmx.Positions) { if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y; }
            float mmdHeight = Mathf.Max(maxY - minY, 1e-3f);
            float feetY = Driver.FeetYAt(0f);
            float hrcHeight = Driver.HeadYAt(0f) - feetY;
            if (!(hrcHeight > 1e-2f) || float.IsNaN(feetY))   // CPU-skin extents unavailable → HRC bind extents
            {
                float bMin = float.PositiveInfinity, bMax = float.NegativeInfinity;
                for (int i = 0; i < hrc.Names.Length; i++) { float y = hrc.BindWorld[i].GetColumn(3).y; if (y < bMin) bMin = y; if (y > bMax) bMax = y; }
                feetY = bMin; hrcHeight = bMax - bMin;
            }
            hrcHeight = Mathf.Max(hrcHeight, 1e-2f);
            // Models ship at wildly different sizes, so the base scale ALIGNS the model's height to this dancer's;
            // config.ini's mmdScale (設定面板「模型大小」) then multiplies that when a particular model still reads as
            // too big or too small. Everything downstream is derived from _unitScale (cloth gravity, particle radius,
            // speed limits), so the physics follows the chosen size instead of being tuned for the automatic one.
            _unitScale = hrcHeight / mmdHeight * Mathf.Clamp(Sdo.Settings.RoomConfig.mmdScale, 0.3f, 3f);

            // ---- bone hierarchy (rest) ----
            _bone = new Transform[bc]; _parent = new int[bc];
            for (int i = 0; i < bc; i++)
            {
                var b = pmx.Bones[i];
                _parent[i] = (b.Parent >= 0 && b.Parent < bc) ? b.Parent : -1;
                _bone[i] = new GameObject("b" + i).transform;
            }
            for (int i = 0; i < bc; i++)
            {
                Transform par = _parent[i] >= 0 ? _bone[_parent[i]] : _mmdRoot;
                _bone[i].SetParent(par, false);
                Vector3 parPos = _parent[i] >= 0 ? pmx.Bones[_parent[i]].Position : Vector3.zero;
                _bone[i].localPosition = pmx.Bones[i].Position - parPos;
                _bone[i].localRotation = Quaternion.identity;
                _bone[i].localScale = Vector3.one;
            }

            _qroot = ComputeFacingAlign(pmx);
            _qrootInv = Quaternion.Inverse(_qroot);
            _mmdRoot.localScale = new Vector3(_unitScale, _unitScale, _unitScale);
            _mmdRoot.localRotation = _qroot;
            _mmdRoot.localPosition = new Vector3(0f, feetY - minY * _unitScale, 0f);

            // ---- mesh + materials: SHARED across every rig of this model (see Shared / GetShared) ----
            _sh = GetShared(pmx, textureDir, searchRoot);
            var meshGo = new GameObject("MmdMesh");
            meshGo.transform.SetParent(_mmdRoot, false);
            var smr = meshGo.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = _sh.Mesh;      // bindposes are rig-independent — see GetShared
            smr.bones = _bone;              // …but the BONES are this rig's own, so each dances on its own
            smr.rootBone = _mmdRoot;
            smr.updateWhenOffscreen = true;
            smr.sharedMaterials = _sh.Materials;

            // ---- retarget wiring: WHICH bone is driven HOW is decided by MmdRetargetPlan (pure, tested) ----
            _hrcIndex = new int[bc]; _hrcRestInv = new Quaternion[bc];
            _aim = new bool[bc]; _aimChildHrc = new int[bc]; _aimRestDir = new Vector3[bc]; _useDelta = new bool[bc];
            var bip01ToMmd = new Dictionary<string, int>();
            var mmdNames = new string[bc]; var mmdPos = new Vector3[bc];
            for (int i = 0; i < bc; i++)
            {
                mmdNames[i] = pmx.Bones[i].NameJp; mmdPos[i] = pmx.Bones[i].Position;
                if (MmdBoneMap.TryGetBip01(mmdNames[i], out string bip01) && !bip01ToMmd.ContainsKey(bip01)) bip01ToMmd[bip01] = i;
                if (mmdNames[i] == MmdBoneMap.RootMmdBone && _rootBone < 0) _rootBone = i;
            }
            _bip01ToBone = bip01ToMmd;                                 // 掛特效用(BoneForBip01)

            var hrcBindPos = new Vector3[hrc.Names.Length];
            for (int i = 0; i < hrcBindPos.Length; i++) hrcBindPos[i] = hrc.BindWorld[i].GetColumn(3);
            var plan = MmdRetargetPlan.Build(mmdNames, mmdPos, hrc.Names, hrc.Parent, hrcBindPos);
            int aimed = 0;
            for (int i = 0; i < bc; i++)
            {
                _hrcIndex[i] = plan[i].Hrc;
                if (plan[i].Hrc >= 0) _hrcRestInv[i] = Quaternion.Inverse(hrc.BindWorld[plan[i].Hrc].rotation);
                if (plan[i].Mode == MmdDriveMode.Aim)
                { _aim[i] = true; _aimChildHrc[i] = plan[i].AimChildHrc; _aimRestDir[i] = plan[i].AimRestDir; aimed++; }
                else if (plan[i].Mode == MmdDriveMode.WorldDelta) _useDelta[i] = true;
            }
            _legs = BuildLegs(bip01ToMmd, mmdPos);
            if (_rootBone >= 0) _rootRestLocal = _bone[_rootBone].localPosition;
            if (hrc.Index.TryGetValue("Bip01", out int rootH)) { _hrcRootIndex = rootH; _hrcRootRestPos = hrc.BindWorld[rootH].GetColumn(3); }

            _isPhysics = new bool[bc];
            foreach (int i in pmx.PhysicsBones) if (i >= 0 && i < bc) _isPhysics[i] = true;   // cloth-owned; skip in retarget
            _order = TopoOrder(_parent);
            _rwLocal = new Quaternion[bc]; _animLocalRot = new Quaternion[bc];
            _appendParent = new int[bc]; _appendWeight = new float[bc];
            for (int i = 0; i < bc; i++)
            {
                var b = pmx.Bones[i];
                bool ok = b.AppendRotation && b.AppendParent >= 0 && b.AppendParent < bc;
                _appendParent[i] = ok ? b.AppendParent : -1;
                _appendWeight[i] = b.AppendWeight;
            }
            _appendOrder = BuildAppendOrder(bc);

            SetLayer(_mmdRoot.gameObject, layer);
            if (cloth)   // head portraits build without one — the hair then holds its styled rest pose and rides the head
            {
                // A physics.ini beside the .pmx (if the model has one) overrides the values converted from its rigid
                // bodies/joints; no file → pure conversion, exactly as before. See MmdClothProfile.
                _profile = MmdClothProfile.Load(textureDir);
                _magica = MmdMagicaCloth.Setup(_mmdRoot.gameObject, _bone, _parent, pmx, _unitScale, _profile);   // Magica Cloth 2 (preferred)
                if (_magica == null)   // package missing / setup failed → hand-rolled spring bones
                {
                    _spring = MmdSpringBones.Attach(_mmdRoot.gameObject, _bone, _parent, pmx, _unitScale, _mmdRoot);
                    BuildColliders(pmx, _unitScale);
                }
            }
            _ready = true;
            _settleFrames = SettleFrames;   // 布料先黏在動作姿勢上幾幀,不要從 rest 姿勢盪下來
            string phys = _magica != null ? $"magica({_magica.ClothCount} cloth,{_magica.ColliderCount} col{(_magica.ProfilePath != null ? ",physics.ini" : "")})" : (_spring != null ? "spring" : (cloth ? "none" : "OFF (portrait)"));
            LogMilestone($"[mmd] built '{pmx.NameJp}' in {(Time.realtimeSinceStartup - t0) * 1000f:F0} ms: {pmx.VertexCount} verts, {pmx.Materials.Count} mats, {bc} bones, " +
                         $"scale={_unitScale:F3}, facing={_qroot.eulerAngles.y:F0}°, driven={CountDriven()}/{bc}, aimed={aimed}, " +
                         $"sphere={_sh.SphereMats.Count}, toon={_sh.ToonMats.Count}, edge={_sh.EdgeMats.Count}, physics={pmx.PhysicsBones.Count}({phys})");
        }

        // One entry per leg whose thigh AND calf both aim and whose ankle is driven — anything less and there is no
        // two-bone chain to solve, so that leg just keeps the aim result.
        private Leg[] BuildLegs(Dictionary<string, int> bip01ToMmd, Vector3[] mmdPos)
        {
            var legs = new List<Leg>(2);
            foreach (string s in new[] { "L", "R" })
            {
                if (!bip01ToMmd.TryGetValue($"Bip01_{s}_Thigh", out int thigh) ||
                    !bip01ToMmd.TryGetValue($"Bip01_{s}_Calf", out int calf) ||
                    !bip01ToMmd.TryGetValue($"Bip01_{s}_Foot", out int ankle)) continue;
                if (!_aim[thigh] || !_aim[calf] || _hrcIndex[ankle] < 0) continue;
                float a = Vector3.Distance(mmdPos[thigh], mmdPos[calf]);
                float b = Vector3.Distance(mmdPos[calf], mmdPos[ankle]);
                if (!(a > 1e-4f) || !(b > 1e-4f)) continue;
                legs.Add(new Leg { Thigh = thigh, Calf = calf, Ankle = ankle, HrcAnkle = _hrcIndex[ankle], A = a, B = b });
            }
            return legs.Count > 0 ? legs.ToArray() : null;
        }

        private int CountDriven() { int n = 0; if (_hrcIndex != null) foreach (var h in _hrcIndex) if (h >= 0) n++; return n; }

        private void LateUpdate()
        {
            if (!_ready || Driver == null || Driver.Hrc == null || _order == null) return;
            for (int k = 0; k < _order.Length; k++)
            {
                int i = _order[k];
                if (_isPhysics[i]) continue;                             // cloth sim (Magica/spring) owns this bone — don't fight it
                int p = _parent[i];
                Quaternion parentRw = p >= 0 ? _rwLocal[p] : Quaternion.identity;
                Quaternion rw;
                if (_hrcIndex[i] < 0) rw = parentRw;                     // unmapped → follow parent (rest)
                else if (UseAim && _aim[i])
                {
                    // AIM (direction, immune to A/T-pose rest mismatch) + TWIST (roll about the bone axis, copied from
                    // the SDO bone so a body spin / torso twist is reproduced — aim alone loses it → body turns wrong).
                    int h = _hrcIndex[i];
                    Vector3 tgt = (Vector3)Driver.BoneAnimWorld(_aimChildHrc[i]).GetColumn(3) - (Vector3)Driver.BoneAnimWorld(h).GetColumn(3);
                    if (tgt.sqrMagnitude > 1e-8f)
                    {
                        Quaternion swing = Quaternion.FromToRotation(_aimRestDir[i], (_qrootInv * tgt).normalized);
                        Quaternion deltaH = Driver.BoneAnimWorld(h).rotation * _hrcRestInv[i];       // SDO world delta
                        Quaternion twist = _qrootInv * TwistAbout(deltaH, tgt.normalized) * _qroot;   // its roll about the aim axis
                        rw = twist * swing;
                    }
                    else rw = parentRw;
                }
                else if (!UseAim || _useDelta[i])
                {
                    // world-delta (absolute orientation): the aim-OFF comparison mode, and the root + head — the head's
                    // bind≈rest so it doesn't over-rotate, and absolute keeps it upright regardless of the neck's tilt.
                    Quaternion deltaH = Driver.BoneAnimWorld(_hrcIndex[i]).rotation * _hrcRestInv[i];
                    rw = _qrootInv * deltaH * _qroot;
                }
                else rw = parentRw;   // other leaf mapped (hand/foot/fingertips) → follow parent: stable, avoids the
                                      // world-delta over-rotation that crosses the wrists/ankles
                _rwLocal[i] = rw;
                Quaternion local = Quaternion.Inverse(parentRw) * rw;
                _bone[i].localRotation = local;
                _animLocalRot[i] = local;
            }

            // Root translation runs BEFORE the foot IK — the IK reads bone POSITIONS, and this is what puts the whole
            // rig at this frame's height/offset. (It only writes a localPosition, so it cannot disturb the rotations
            // solved above.)
            if (DriveRootTranslation && _rootBone >= 0 && _hrcRootIndex >= 0)
            {
                Vector3 d = (Vector3)Driver.BoneAnimWorld(_hrcRootIndex).GetColumn(3) - _hrcRootRestPos;
                _bone[_rootBone].localPosition = _rootRestLocal + (_qrootInv * d) / _unitScale;
            }

            // Foot IK: put the ankles back where the SDO motion actually puts them (aim copies direction, not bone
            // length — see MmdFootIk). Before the 付与 pass, so the 足D chain the mesh is skinned to picks it up.
            if (FootIk && _legs != null)
                for (int k = 0; k < _legs.Length; k++) SolveLeg(_legs[k]);

            // 付与 append pass (足D chain copies FK legs so the skinned mesh follows)
            if (_appendOrder != null)
                for (int k = 0; k < _appendOrder.Length; k++)
                {
                    int i = _appendOrder[k]; int src = _appendParent[i];
                    Quaternion s = _animLocalRot[src];
                    Quaternion app = _appendWeight[i] == 1f ? s : Quaternion.SlerpUnclamped(Quaternion.identity, s, _appendWeight[i]);
                    Quaternion fin = _animLocalRot[i] * app;
                    _bone[i].localRotation = fin;
                    _animLocalRot[i] = fin;
                }

            // 骨頭已經擺到這一幀該有的姿勢了 → 剛建好/剛顯示出來的那幾幀把布料黏過去(見 _settleFrames)。
            if (_settleFrames > 0)
            {
                _settleFrames--;
                _magica?.ResetToCurrentPose();
                _spring?.ResetToCurrentPose();
            }
        }

        /// <summary>大腿/小腿的 rest 骨長,加上腳踝要跟哪根 HRC 骨走 —— 一條腿的 IK 需要的全部。</summary>
        private struct Leg { public int Thigh, Calf, Ankle, HrcAnkle; public float A, B; }
        private Leg[] _legs;

        // Solve one leg so its ankle lands on the SDO ankle, then write the two bones back. The ankle's own ORIENTATION
        // is untouched (it aims at the toe — that is what levels the sole); only its parent moved, so its local
        // rotation has to be recomputed against the new calf.
        private void SolveLeg(Leg leg)
        {
            Vector3 target = (_qrootInv * ((Vector3)Driver.BoneAnimWorld(leg.HrcAnkle).GetColumn(3) - _mmdRoot.localPosition)) / _unitScale;
            Vector3 hip = _mmdRoot.InverseTransformPoint(_bone[leg.Thigh].position);
            Vector3 kneeHint = _mmdRoot.InverseTransformPoint(_bone[leg.Calf].position);
            if (!MmdFootIk.Solve(hip, target, kneeHint, leg.A, leg.B, out Vector3 thighDir, out Vector3 kneePos)) return;

            Quaternion rwT = _rwLocal[leg.Thigh];
            WriteBone(leg.Thigh, Quaternion.FromToRotation(rwT * _aimRestDir[leg.Thigh], thighDir) * rwT);

            Vector3 calfDir = target - kneePos;
            if (calfDir.sqrMagnitude > 1e-10f)
            {
                Quaternion rwC = _rwLocal[leg.Calf];
                WriteBone(leg.Calf, Quaternion.FromToRotation(rwC * _aimRestDir[leg.Calf], calfDir.normalized) * rwC);
            }
            WriteBone(leg.Ankle, _rwLocal[leg.Ankle]);   // same world orientation, new parent → new local
        }

        // Set a bone's world-in-root-local rotation, keeping _rwLocal/_animLocalRot (the 付与 source) in step.
        private void WriteBone(int i, Quaternion rw)
        {
            int p = _parent[i];
            Quaternion parentRw = p >= 0 ? _rwLocal[p] : Quaternion.identity;
            _rwLocal[i] = rw;
            Quaternion local = Quaternion.Inverse(parentRw) * rw;
            _bone[i].localRotation = local;
            _animLocalRot[i] = local;
        }

        // The twist component of rotation q about a (normalised) axis — swing-twist decomposition. Used to copy the
        // SDO bone's roll about its own direction onto the aimed MMD bone (aim gives direction but zero twist).
        private static Quaternion TwistAbout(Quaternion q, Vector3 axis)
        {
            Vector3 v = new Vector3(q.x, q.y, q.z);
            float dot = Vector3.Dot(v, axis);
            var twist = new Quaternion(axis.x * dot, axis.y * dot, axis.z * dot, q.w);
            float n = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
            if (n < 1e-6f) return Quaternion.identity;   // 180° swing singularity → no defined twist
            twist.x /= n; twist.y /= n; twist.z /= n; twist.w /= n;
            return twist;
        }

        private Quaternion ComputeFacingAlign(PmxLoader pmx)
        {
            var hrc = Driver.Hrc;
            Vector3 hrcRight = HrcBonePos(hrc, "Bip01_R_UpperArm") - HrcBonePos(hrc, "Bip01_L_UpperArm");
            Vector3 mmdRight = MmdBonePos(pmx, "右腕") - MmdBonePos(pmx, "左腕");
            hrcRight.y = 0f; mmdRight.y = 0f;
            if (hrcRight.sqrMagnitude < 1e-6f || mmdRight.sqrMagnitude < 1e-6f) return Quaternion.identity;
            return Quaternion.AngleAxis(Vector3.SignedAngle(mmdRight.normalized, hrcRight.normalized, Vector3.up), Vector3.up);
        }

        private static Vector3 HrcBonePos(HrcLoader hrc, string name) => hrc.Index.TryGetValue(name, out int i) ? (Vector3)hrc.BindWorld[i].GetColumn(3) : Vector3.zero;
        private static Vector3 MmdBonePos(PmxLoader pmx, string nameJp) { foreach (var b in pmx.Bones) if (b.NameJp == nameJp) return b.Position; return Vector3.zero; }

        private static int[] TopoOrder(int[] parent)
        {
            int n = parent.Length; var depth = new int[n];
            for (int i = 0; i < n; i++) { int d = 0, p = parent[i], g = 0; while (p >= 0 && g++ < n) { d++; p = parent[p]; } depth[i] = d; }
            var order = new int[n]; for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) => depth[a].CompareTo(depth[b]));
            return order;
        }

        private int[] BuildAppendOrder(int n)
        {
            var list = new List<int>();
            for (int i = 0; i < n; i++) if (_appendParent[i] >= 0) list.Add(i);
            var depth = new int[n];
            foreach (int i in list) { int d = 0, p = _appendParent[i], g = 0; while (p >= 0 && _appendParent[p] >= 0 && g++ < n) { d++; p = _appendParent[p]; } depth[i] = d; }
            list.Sort((a, b) => depth[a].CompareTo(depth[b]));
            return list.ToArray();
        }

        // ---- shared per-model assets: built for the first rig, reused by every rig after it ----
        private static Shared GetShared(PmxLoader pmx, string textureDir, string searchRoot)
        {
            if (_sharedByModel.TryGetValue(pmx, out var s) && s.Mesh != null && s.Materials != null &&
                s.Materials.Length > 0 && s.Materials[0] != null && s.LilToon == UseLilToon)
            {
                LogMilestone($"[mmd] reusing the shared mesh/materials ({pmx.VertexCount} verts, {s.Materials.Length} mats) — not rebuilt");
                return s;
            }

            var t0 = Time.realtimeSinceStartup;
            s = new Shared();
            s.Materials = BuildMaterials(pmx, textureDir, s, searchRoot);   // sets s.Hide (+ the sphere/toon/edge lists)
            var tMat = Time.realtimeSinceStartup;
            s.Mesh = BuildMesh(pmx, s);                         // skips the hidden submeshes
            var tMesh = Time.realtimeSinceStartup;

            // Bindposes: the MMD rest bones have identity rotation and unit scale, and the rig root's transform cancels
            // out of bone.worldToLocal × mesh.localToWorld — so the bindpose is just translate(−bonePos), identical for
            // every rig. That is what makes ONE mesh serve rigs built at different unit scales.
            var binds = new Matrix4x4[pmx.Bones.Count];
            for (int i = 0; i < binds.Length; i++) binds[i] = Matrix4x4.Translate(-pmx.Bones[i].Position);
            s.Mesh.bindposes = binds;

            if (MmdHeadBounds.TryCompute(pmx, out int hb, out var hl))
            {
                s.HasHead = true; s.HeadBone = hb; s.HeadLocal = hl; s.HeadRestPos = pmx.Bones[hb].Position;
            }

            // Same reason as the texture cache (see LoadTexture): these are script-created assets held only by the static
            // dictionary above, so Resources.UnloadUnusedAssets — which SceneManager.LoadScene runs — is free to reclaim
            // them between screens. That is the 「換場景要重新讀取」 hitch: it would re-skin 172k verts and rebuild every
            // material from scratch on the next rig. Pin them; one set per model, alive for the process, by design.
            s.Mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
            foreach (var m in s.Materials) if (m != null) m.hideFlags = HideFlags.DontUnloadUnusedAsset;

            _sharedByModel[pmx] = s;
            LogMilestone($"[mmd] shared mesh+materials built in {(Time.realtimeSinceStartup - t0) * 1000f:F0} ms " +
                         $"(貼圖+材質 {(tMat - t0) * 1000f:F0} ms, mesh {(tMesh - tMat) * 1000f:F0} ms; " +
                         $"{pmx.VertexCount} verts, {s.Materials.Length} mats) — every rig reuses these");
            return s;
        }

        /// <summary>Build this model's shared mesh/materials/textures WITHOUT building a rig — so the cost is paid once,
        /// on the boot loading screen, instead of on the first room/song entry. No-op when they are already cached.
        /// See <c>MmdAvatarSwap.PrewarmCo</c>.</summary>
        public static void Prewarm(PmxLoader pmx, string textureDir, string searchRoot = null)
        {
            if (pmx != null && !string.IsNullOrEmpty(textureDir)) GetShared(pmx, textureDir, searchRoot);
        }

        /// <summary>Decode ONE of the model's textures into the shared cache. The measured cost of building a model's
        /// shared assets is ~95% texture decode (Miku: 1401 of 1438 ms — ten 2048² PNGs, each ~140 ms of decode +
        /// mipmap generation), and <see cref="Prewarm"/> does them all back-to-back in one frame. Calling this per
        /// texture with a <c>yield</c> between spreads that over frames, so a boot progress bar keeps moving instead of
        /// freezing. Returns false when the index is out of range (the caller's loop bound).</summary>
        public static bool PrewarmTexture(PmxLoader pmx, string textureDir, int index, string searchRoot = null)
        {
            if (pmx?.TexturePaths == null || index < 0 || index >= pmx.TexturePaths.Length) return false;
            LoadTexture(textureDir, (pmx.TexturePaths[index] ?? "").Replace('\\', '/'), searchRoot);   // caches; null/missing is fine
            return true;
        }

        /// <summary>How many textures <see cref="PrewarmTexture"/> can be called for (＝ the model's texture table).</summary>
        public static int TextureCount(PmxLoader pmx) => pmx?.TexturePaths?.Length ?? 0;

        // ---- mesh ----
        private static Mesh BuildMesh(PmxLoader pmx, Shared sh)
        {
            int vc = pmx.VertexCount;
            var mesh = new Mesh { name = "mmd_" + pmx.NameEn };
            if (vc > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = pmx.Positions; mesh.normals = pmx.Normals;
            sh.UvVerbatim = pmx.Uvs;
            sh.UvFlipped = new Vector2[vc];
            for (int k = 0; k < vc; k++) sh.UvFlipped[k] = new Vector2(pmx.Uvs[k].x, 1f - pmx.Uvs[k].y);
            mesh.uv = sh.UvFlipped;   // PMX UVs are V-down; flip for Unity (SetFlipV toggles it live)
            sh.FlipVApplied = true;
            var bw = new BoneWeight[vc];
            for (int v = 0; v < vc; v++)
            {
                int o = v * 4;
                bw[v] = new BoneWeight
                {
                    boneIndex0 = Mathf.Max(pmx.BoneIdx[o], 0), weight0 = pmx.BoneWt[o],
                    boneIndex1 = Mathf.Max(pmx.BoneIdx[o + 1], 0), weight1 = pmx.BoneWt[o + 1],
                    boneIndex2 = Mathf.Max(pmx.BoneIdx[o + 2], 0), weight2 = pmx.BoneWt[o + 2],
                    boneIndex3 = Mathf.Max(pmx.BoneIdx[o + 3], 0), weight3 = pmx.BoneWt[o + 3],
                };
            }
            mesh.boneWeights = bw;
            mesh.subMeshCount = pmx.Materials.Count;
            for (int s = 0; s < pmx.Materials.Count; s++)
            {
                // calculateBounds:false — every SetTriangles otherwise re-scans all 172k vertices to recompute the
                // bounds, once per submesh (53× here). RecalculateBounds below does it once.
                if (sh.Hide != null && sh.Hide[s]) { mesh.SetTriangles(Array.Empty<int>(), s, false); continue; }
                var m = pmx.Materials[s];
                var tris = new int[m.IndexCount];
                Array.Copy(pmx.Indices, m.IndexStart, tris, 0, m.IndexCount);
                mesh.SetTriangles(tris, s, false);
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---- materials (MMD shader: authored alpha chooses visibility; texture alpha chooses opaque/cutout/blend) ----
        // Built with every effect ON; MmdAvatarSwap re-applies config.ini's [Mmd] toggles to each rig right after it is built,
        // and since the materials are shared those writes land on the same materials for all of them.
        private static Material[] BuildMaterials(PmxLoader pmx, string dir, Shared sh, string searchRoot)
        {
            // 兩個著色後端共用這一整段：貼圖是同一批、alpha 分類是同一套、三個顯示開關記的也是同一份清單。
            // 分岔只在「拿哪支 shader、把這些值寫進哪些屬性」——見 MmdLilToon。
            bool lil = UseLilToon;
            sh.LilToon = lil;
            sh.SphereProp = lil ? MmdLilToon.SphereProperty : "_SphereMode";
            sh.ToonProp = lil ? MmdLilToon.ToonProperty : "_UseToon";
            sh.EdgeProp = lil ? MmdLilToon.OutlineWidthProperty : "_EdgeSize";
            if (lil) MmdKeyLight.Ensure();   // lilToon 吃光照，而這個專案本來一顆燈都沒有（其它東西全是 unlit）

            var shader = Shader.Find("Sdo/MmdModel") ?? Shader.Find("Unlit/Texture");
            var mats = new Material[pmx.Materials.Count];
            var _hide = sh.Hide = new bool[pmx.Materials.Count];
            var alpha = MeasureMaterialAlpha(pmx, dir, searchRoot);   // 逐材質、只看它自己的 UV 區(見 MeasureMaterialAlpha)
            for (int i = 0; i < pmx.Materials.Count; i++)
            {
                var pm = pmx.Materials[i];
                if (pm.Diffuse.a < 0.05f)   // morph-hidden (duplicate hair / body-hide / sphere pupils)
                {
                    _hide[i] = true; mats[i] = new Material(shader);
                    SdoLog.Note("mmd", $"  mat[{i}] '{pm.NameJp}' a=0 -> HIDDEN");
                    continue;
                }
                string texName = (pm.TextureIndex >= 0 && pm.TextureIndex < pmx.TexturePaths.Length) ? pmx.TexturePaths[pm.TextureIndex] : null;
                Texture2D tex = texName != null ? LoadTexture(dir, texName, searchRoot) : null;
                bool missingBaseTexture = texName != null && tex == null;
                float midFrac = alpha[i].x, holeFrac = alpha[i].y;
                var renderMode = MmdMaterialClassifier.Classify(pm.Diffuse.a, midFrac, holeFrac, pm.DoubleSided);

                // sphere map (matcap): the MMD "shine" — eyes/skin/metal. Sampled by view normal, so NOT UV-flipped.
                Texture2D sphereTex = null; float sphereMode = 0f;
                if ((pm.SphereMode == 1 || pm.SphereMode == 2) && pm.SphereIndex >= 0 && pm.SphereIndex < pmx.TexturePaths.Length)
                {
                    sphereTex = LoadTexture(dir, pmx.TexturePaths[pm.SphereIndex], searchRoot);
                    if (sphereTex != null) sphereMode = pm.SphereMode;
                }

                // toon ramp (cel shading): a vertical light→shadow gradient sampled by N·L. Either a per-material toon
                // TEXTURE (ToonIndex) or a built-in SHARED toon (ToonShared 0..9) → a synthesized 2-tone ramp fallback.
                Texture2D toon = pm.ToonIndex >= 0 && pm.ToonIndex < pmx.TexturePaths.Length ? LoadTexture(dir, pmx.TexturePaths[pm.ToonIndex], searchRoot) : null;
                if (toon == null && pm.ToonShared >= 0) toon = DefaultToonRamp();
                bool hasToon = toon != null;
                if (hasToon) toon.wrapMode = TextureWrapMode.Clamp;

                // pencil outline: only edge-flagged materials get a non-zero edge size.
                // The MMD outline pass is opaque and writes depth. Disable it for blended surfaces so translucent
                // cloth does not acquire an opaque black/depth hull; cutout and opaque outlines remain unchanged.
                bool hasEdge = pm.HasEdge && renderMode != MmdMaterialRenderMode.Blend;

                Material mat;
                string matName = pm.NameEn ?? pm.NameJp ?? ("mat" + i);
                if (lil)
                {
                    // lilToon 把「不透明/裁切/透明 × 有無描邊」拆成各自的 shader（描邊是多一個 pass），所以這裡選 shader
                    // 就等於選了那兩件事。找不到（lilToon 沒裝 / build 把它剝掉了）就退回 MMD 那份，畫面還在。
                    var ls = FindShader(MmdLilToon.ShaderNameFor(renderMode, hasEdge));
                    mat = new Material(ls ?? shader) { name = matName };
                    if (ls != null)
                        MmdLilToon.Configure(mat, tex ?? Texture2D.whiteTexture, pm.Diffuse, pm.DoubleSided, renderMode,
                                             sphereTex, (int)sphereMode, toon, pm.EdgeColor, hasEdge ? pm.EdgeSize : 0f);
                    else
                        ConfigureMmd(mat, tex, pm, renderMode, sphereTex, sphereMode, toon, hasEdge);
                }
                else
                {
                    mat = new Material(shader) { name = matName };
                    ConfigureMmd(mat, tex, pm, renderMode, sphereTex, sphereMode, toon, hasEdge);
                }

                // 三個顯示開關記的是「打開時要寫回去的值」。後端不同、屬性不同、值也不同（lilToon 的 matcap 是
                // 開/關而不是乘/加，描邊是它自己的 0~1 刻度），但 SetSphere/SetToon/SetOutline 一律只寫 sh.*Prop。
                if (sphereMode > 0f) sh.SphereMats.Add(new KeyValuePair<Material, float>(mat, lil ? 1f : sphereMode));
                if (hasToon) sh.ToonMats.Add(mat);
                if (hasEdge) sh.EdgeMats.Add(new KeyValuePair<Material, float>(mat, lil ? MmdLilToon.OutlineWidth(pm.EdgeSize) : pm.EdgeSize));

                mats[i] = mat;
                string baseTexLabel = texName != null ? Path.GetFileName(texName) : "(none)";
                SdoLog.Note("mmd", $"  mat[{i}] '{pm.NameJp}' tex='{baseTexLabel}' {renderMode.ToString().ToUpperInvariant()}{(missingBaseTexture ? " FALLBACK-colour" : "")}{(pm.DoubleSided ? " 2sided" : "")}{(sphereMode > 0 ? " +sphere" + (int)sphereMode : "")}{(hasToon ? " +toon" : "")}{(hasEdge ? " +edge" : "")}{(lil ? " [lilToon]" : "")}");
            }
            return mats;
        }

        /// <summary>Sdo/MmdModel 那一份的屬性寫入（MMD 固定管線的忠實移植）。</summary>
        private static void ConfigureMmd(Material mat, Texture2D tex, PmxLoader.Material pm, MmdMaterialRenderMode renderMode,
                                         Texture2D sphereTex, float sphereMode, Texture2D toon, bool hasEdge)
        {
            // A PMX diffuse texture is optional. White preserves the authored diffuse colour while keeping the same
            // cull/alpha/sphere/toon/edge path; a referenced-but-missing file uses the same visible fallback.
            mat.SetTexture("_MainTex", tex ?? Texture2D.whiteTexture);
            mat.SetColor("_Color", pm.Diffuse);
            mat.SetFloat("_Cull", pm.DoubleSided ? 0f : 2f);           // Off : Back
            mat.SetFloat("_Cutoff", 0.5f);
            MmdMaterialClassifier.Apply(mat, renderMode);

            if (sphereTex != null) mat.SetTexture("_SphereTex", sphereTex);
            mat.SetFloat("_SphereMode", sphereMode);

            if (toon != null) mat.SetTexture("_ToonTex", toon);
            mat.SetFloat("_UseToon", toon != null ? 1f : 0f);

            mat.SetColor("_EdgeColor", pm.EdgeColor);
            mat.SetFloat("_EdgeSize", hasEdge ? pm.EdgeSize : 0f);
        }

        // Shader.Find 每次都掃一遍全域 shader 表，而這裡是每個材質問一次（初音 53 個）→ 記起來。
        private static readonly Dictionary<string, Shader> _shaderCache = new Dictionary<string, Shader>();
        private static Shader FindShader(string name)
        {
            if (_shaderCache.TryGetValue(name, out var s) && s != null) return s;
            s = Shader.Find(name);
            _shaderCache[name] = s;
            return s;
        }

        /// <summary>Live toggle: turn all sphere maps on/off (restores each material's authored sphere mode).</summary>
        public void SetSphere(bool on) { ShowSphere = on; if (_sh == null) return; foreach (var kv in _sh.SphereMats) if (kv.Key != null) kv.Key.SetFloat(_sh.SphereProp, on ? kv.Value : 0f); }

        /// <summary>Live toggle: flip the mesh UV V (find the atlas-correct orientation without a recompile).</summary>
        public void SetFlipV(bool on)
        {
            FlipV = on;
            if (_sh == null || _sh.Mesh == null || _sh.UvVerbatim == null || _sh.FlipVApplied == on) return;   // shared mesh: don't re-upload 172k UVs per rig
            _sh.Mesh.uv = on ? _sh.UvFlipped : _sh.UvVerbatim;
            _sh.FlipVApplied = on;
        }

        /// <summary>Live toggle: cel-shading toon ramp on/off.</summary>
        public void SetToon(bool on) { ShowToon = on; if (_sh == null) return; foreach (var m in _sh.ToonMats) if (m != null) m.SetFloat(_sh.ToonProp, on ? 1f : 0f); }

        // Synthesized shared-toon ramp (shadow at V=0 → lit at V=1) for materials that reference a built-in MMD toon
        // (toon01..toon10) we don't bundle. Cached; the shader samples it at (0.5, N·L) so lit=top, shadow=bottom.
        private static Texture2D _defToon;
        private static Texture2D DefaultToonRamp()
        {
            if (_defToon != null) return _defToon;
            const int h = 32;
            var t = new Texture2D(1, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[h];
            for (int y = 0; y < h; y++) { byte b = (byte)(Mathf.SmoothStep(0.55f, 1f, y / (float)(h - 1)) * 255f); px[y] = new Color32(b, b, b, 255); }
            t.SetPixels32(px); t.Apply(false);
            t.hideFlags = HideFlags.DontUnloadUnusedAsset;   // same pin as the model textures — see LoadTexture
            _defToon = t; return t;
        }

        /// <summary>Live toggle: pencil outline on/off (restores each material's authored edge size).</summary>
        public void SetOutline(bool on) { ShowOutline = on; if (_sh == null) return; foreach (var kv in _sh.EdgeMats) if (kv.Key != null) kv.Key.SetFloat(_sh.EdgeProp, on ? kv.Value : 0f); }

        /// <summary>Live toggle / tune of the hair-skirt spring-bone sway.</summary>
        public void SetPhysics(bool on) { _physicsOn = on; UpdateSpring(); }
        public void TunePhysics(float stiffness, float drag, float gravMul)
        {
            if (_spring != null) _spring.SetTuning(stiffness, drag, gravMul);
            if (_magica != null) _magica.Tune(gravMul, stiffness / 0.12f);   // 0.12 = panel default → stiffMul 1
        }
        public void SetColliderRadius(float mul) { if (_spring != null) _spring.ColliderMul = mul; if (_magica != null) _magica.SetColliderRadius(mul); }
        private void UpdateSpring() { bool on = _visible && _physicsOn; if (_spring != null) _spring.enabled = on; if (_magica != null) _magica.SetEnabled(on); }

        // Body colliders so hair/skirt tails don't sink into the body: CAPSULES down the legs + torso (they cover the
        // gaps that spheres leave between thigh/knee), plus hip + head spheres. Radius ∝ leg spacing (half hip width).
        private void BuildColliders(PmxLoader pmx, float unitScale)
        {
            if (_spring == null) return;
            float hipHalf = (MmdBonePos(pmx, "左足") - MmdBonePos(pmx, "右足")).magnitude * 0.5f * unitScale;
            if (hipHalf < 1e-3f) hipHalf = (MmdBonePos(pmx, "左腕") - MmdBonePos(pmx, "右腕")).magnitude * 0.3f * unitScale;
            if (hipHalf < 1e-3f) return;
            var a = new List<Transform>(); var b = new List<Transform>(); var r = new List<float>();
            AddCapsule(a, b, r, pmx, "上半身2", "下半身", hipHalf * 0.85f);   // torso
            AddCapsule(a, b, r, pmx, "左足", "左ひざ", hipHalf * 0.55f);       // left thigh
            AddCapsule(a, b, r, pmx, "左ひざ", "左足首", hipHalf * 0.45f);     // left shin
            AddCapsule(a, b, r, pmx, "右足", "右ひざ", hipHalf * 0.55f);       // right thigh
            AddCapsule(a, b, r, pmx, "右ひざ", "右足首", hipHalf * 0.45f);     // right shin
            AddCapsule(a, b, r, pmx, "下半身", "下半身", hipHalf * 1.0f);      // hips (sphere)
            AddCapsule(a, b, r, pmx, "頭", "頭", hipHalf * 0.9f);             // head (sphere) — keep hair off the crown
            if (a.Count > 0) _spring.SetColliders(a.ToArray(), b.ToArray(), r.ToArray());
        }
        private void AddCapsule(List<Transform> a, List<Transform> b, List<float> r, PmxLoader pmx, string n0, string n1, float radius)
        {
            int i0 = FindBoneIndex(pmx, n0), i1 = FindBoneIndex(pmx, n1);
            if (i0 >= 0 && i1 >= 0 && _bone[i0] != null && _bone[i1] != null) { a.Add(_bone[i0]); b.Add(_bone[i1]); r.Add(radius); }
        }
        private static int FindBoneIndex(PmxLoader pmx, string nameJp) { for (int i = 0; i < pmx.Bones.Count; i++) if (pmx.Bones[i].NameJp == nameJp) return i; return -1; }

        /// <summary>取樣上限:一個材質最多看這麼多個三角形(每個取三個頂點 + 重心 = 4 個 texel)。</summary>
        private const int MaxSampledTriangles = 8000;

        /// <summary>
        /// 每個材質的 (半透明佔比, 洞佔比) —— <b>只統計這個材質自己貼到的那塊 UV</b>。
        ///
        /// 🔴 這裡以前是拿「整張貼圖」的統計去餵 <see cref="MmdMaterialClassifier"/>,而 MMD 模型幾乎都是
        /// 一張 atlas 餵好幾個材質:YYB 初音的 C.png(外套/袖子/裙子共 7 個材質)整張有 27% 的 texel 落在
        /// 225~254 那條雜訊帶,可是袖子與外套真正貼到的那幾塊是**全不透明**的。整張統計 → 那 7 個材質全被
        /// 判成半透明 → 全進 Transparent 佇列 → 同一個 SkinnedMeshRenderer 內改照材質順序畫 → 雙馬尾
        /// (mat 22) 蓋過袖子 (mat 11~14)。同一份 body.png 也讓臉/身體那 8 個材質被別人的洞拖去當 cutout。
        ///
        /// 走訪順序是**按貼圖分組**,不是按材質:<c>GetPixels32</c> 會把整張圖複製到 managed 記憶體
        /// (2048² = 16 MB),一次只留一張,量完就丟 —— 峰值記憶體與材質數無關。
        /// </summary>
        private static Vector2[] MeasureMaterialAlpha(PmxLoader pmx, string dir, string searchRoot)
        {
            var outv = new Vector2[pmx.Materials.Count];
            // 貼圖 → 用到它的材質。同一張只解一次 GetPixels32。
            var byTexture = new Dictionary<Texture2D, List<int>>();
            for (int i = 0; i < pmx.Materials.Count; i++)
            {
                var pm = pmx.Materials[i];
                if (pm.Diffuse.a < 0.05f) continue;                    // 反正會被藏起來,不用量
                if (pm.TextureIndex < 0 || pm.TextureIndex >= pmx.TexturePaths.Length) continue;
                var tex = LoadTexture(dir, pmx.TexturePaths[pm.TextureIndex], searchRoot);   // 已快取,不會重讀
                if (tex == null) continue;
                if (!byTexture.TryGetValue(tex, out var list)) byTexture[tex] = list = new List<int>();
                list.Add(i);
            }
            foreach (var kv in byTexture)
            {
                Color32[] px;
                try { px = kv.Key.GetPixels32(); } catch { continue; }
                foreach (int i in kv.Value)
                    outv[i] = MeasureUvRegion(px, kv.Key.width, kv.Key.height, pmx, pmx.Materials[i]);
                px = null;   // 下一張之前就放掉這 16 MB
            }
            return outv;
        }

        /// <summary>一個材質貼到的那塊 UV 的 (半透明佔比, 洞佔比)。</summary>
        private static Vector2 MeasureUvRegion(Color32[] px, int w, int h, PmxLoader pmx, PmxLoader.Material m)
        {
            if (px == null || w <= 0 || h <= 0 || px.Length < w * h) return Vector2.zero;
            int triCount = m.IndexCount / 3;
            if (triCount <= 0) return Vector2.zero;
            int step = Mathf.Max(1, triCount / MaxSampledTriangles);
            int mid = 0, hole = 0, n = 0;
            var quad = new Vector2[4];
            for (int t = 0; t < triCount; t += step)
            {
                int o = m.IndexStart + t * 3;
                if (o + 2 >= pmx.Indices.Length) break;
                int i0 = pmx.Indices[o], i1 = pmx.Indices[o + 1], i2 = pmx.Indices[o + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= pmx.Uvs.Length || i1 >= pmx.Uvs.Length || i2 >= pmx.Uvs.Length) continue;
                quad[0] = pmx.Uvs[i0]; quad[1] = pmx.Uvs[i1]; quad[2] = pmx.Uvs[i2];
                quad[3] = (quad[0] + quad[1] + quad[2]) / 3f;   // 重心:大三角形內部也要有樣本,不能只看邊
                for (int k = 0; k < 4; k++)
                {
                    float u = quad[k].x, v = quad[k].y;
                    u -= Mathf.Floor(u); v -= Mathf.Floor(v);                    // wrap = Repeat,與 shader 一致
                    int col = Mathf.Clamp((int)(u * w), 0, w - 1);
                    // PMX 的 UV 是 V-down(v=0 ＝ 圖的上緣);GetPixels32 的第 0 列是圖的**下**緣。
                    int row = Mathf.Clamp((int)((1f - v) * h), 0, h - 1);
                    byte a = px[row * w + col].a;
                    if (MmdMaterialClassifier.IsHole(a)) hole++;
                    else if (MmdMaterialClassifier.IsTranslucent(a)) mid++;
                    n++;
                }
            }
            return n > 0 ? new Vector2((float)mid / n, (float)hole / n) : Vector2.zero;
        }

        // Resolve + decode a PMX texture. NO vertical flip: the PMX's verbatim (D3D) UVs sample correctly against this
        // project's texel layout — the SDO DDS pipeline puts image-top at texel row 0, and Unity's PNG/BMP decode here
        // matches, so a flip actually scrambled the clothing atlas (skin bled onto the necktie). Verified by rendering
        // the model both ways: unflipped = correct Miku (green tie, right costume), flipped = broken.
        //
        // 🔴 這裡有三條解碼路徑(TGA / BMP / Unity 內建的 PNG-JPG),**它們的上下方向必須一致** —— 一個 MMD
        // 模型可以三種格式混著用(LaplusDarknesss:頭髮 .png、臉/身體/眼睛/皮膚 .tga),方向不一致的症狀是
        // 「一部分貼圖正、一部分上下顛倒」,而且那跟 UV 無關:調 mmdFlipV 只會把本來正的那部分也弄反。
        // DdsLoader.LoadTga 預設是 SDO 自己那套 D3D 列序(圖的上緣放在 SetPixels32 的第 0 列),與 Unity 的
        // LoadImage 差一個翻轉 → 外來模型一律要 sdoRowOrder:false。DecodeBmp 本來就與 LoadImage 同向。
        private static readonly Dictionary<string, Texture2D> _texCache = new Dictionary<string, Texture2D>();
        private static Texture2D LoadTexture(string dir, string rel, string searchRoot)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(rel)) return null;
            string path = ResolvePath(dir, rel.Replace('\\', '/'), searchRoot);
            if (path == null) return null;
            // The same model is now built several times over (stage dancer, room walker, room 頭貼, 結算頭貼, both gender
            // previews) — decode each texture once and share it, or every extra rig re-reads the whole texture set.
            if (_texCache.TryGetValue(path, out var hit) && hit != null) return hit;
            byte[] b; try { b = File.ReadAllBytes(path); } catch { return null; }
            string ext = Path.GetExtension(path).ToLowerInvariant();
            Texture2D tex = null;
            try
            {
                // sdoRowOrder:false ＝ 與 LoadImage 同向(見上面);readable:true ＝ 留著 CPU 那一份,
                // MeasureMaterialAlpha 要 GetPixels32 才分得出不透明/裁切/半透明(不可讀 → 整批誤判成不透明)。
                if (ext == ".tga") tex = DdsLoader.LoadTga(b, sdoRowOrder: false, readable: true);
                else if (b.Length > 2 && b[0] == 'B' && b[1] == 'M') tex = DecodeBmp(b);
                else
                {
                    var t = new Texture2D(2, 2, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
                    tex = t.LoadImage(b) ? t : null;
                }
            }
            catch { return null; }
            if (tex != null)
            {
                // Pin it: SceneManager.LoadScene (結算「重玩」走那條) runs Resources.UnloadUnusedAssets, and a
                // script-created Texture2D that no live GameObject references at that instant is exactly what it
                // reclaims. Losing it = the whole texture set is re-read + re-decoded on the next rig — the
                // 「換場景又要重讀一次」that this cache exists to prevent. There is one set per installed model and
                // it is meant to live for the process, so never unloading it is the intent, not a leak.
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                _texCache[path] = tex;
            }
            return tex;
        }

        /// <summary>
        /// PMX 裡的一條貼圖路徑 → 磁碟上真正的檔案(找不到 null)。三段:
        /// ① 照字面(相對於 .pmx 的資料夾);② 同一個資料夾裡不分大小寫、可換副檔名地找;
        /// ③ 還是沒有 → 在**整包**模型資料夾裡照檔名找(<paramref name="searchRoot"/>,見 <see cref="PackIndex"/>)。
        /// </summary>
        public static string ResolveTexturePath(string dir, string rel, string searchRoot)
            => string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(rel) ? null
             : ResolvePath(dir, rel.Replace('\\', '/'), searchRoot);

        private static string ResolvePath(string dir, string rel, string searchRoot)
        {
            string full = Path.Combine(dir, rel);
            if (File.Exists(full)) return full;
            string sub = Path.GetDirectoryName(full), file = Path.GetFileName(full);
            string stem = Path.GetFileNameWithoutExtension(file).ToLowerInvariant(), want = file.ToLowerInvariant();
            if (!string.IsNullOrEmpty(sub) && Directory.Exists(sub))
            {
                string stemHit = null;
                foreach (var f in Directory.GetFiles(sub))
                {
                    string fn = Path.GetFileName(f).ToLowerInvariant();
                    if (fn == want) return f;
                    if (stemHit == null && Path.GetFileNameWithoutExtension(f).ToLowerInvariant() == stem) stemHit = f;
                }
                if (stemHit != null) return stemHit;
            }
            // 照字面找不到 → 在**整包**模型資料夾裡照檔名找(見 PackIndex)。
            return FindInPack(searchRoot, want, stem);
        }

        /// <summary>
        /// 一包模型底下所有圖檔的「檔名 → 路徑」索引。<c>null</c> 代表這一包沒給搜尋根(退化成舊行為)。
        ///
        /// 🔴 為什麼需要:PMX 裡的貼圖路徑是相對於 .pmx 的,但「組立キット」型的包會把 .pmx 和貼圖分在
        /// 完全不同的樹枝上 —— 十六夜咲夜Ver2.20 的 .pmx 在 <c>01-モデル/十六夜咲夜/</c>,它引用的 28 張貼圖
        /// 全部在隔壁的 <c>02-共通テクスチャ/</c>,而且 PMX 裡寫的是**純檔名、沒有目錄**。照字面找 28 張全部
        /// 落空 → 模型讀得到但整隻沒有貼圖。作者的用法是「在 PMXEditor 裡組裝完再另存」,我們不能要求
        /// 使用者做那一步,所以退一步:整包裡只要有同名檔就用它。
        ///
        /// 一包建一次,之後免費。同名檔案以「路徑排序後的第一個」為準 —— 與檔案系統的列舉順序無關。
        /// </summary>
        private static readonly Dictionary<string, Dictionary<string, string>> _packIndex =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>一包最多索引這麼多個圖檔(防呆:有人把搜尋根指到一棵很大的樹)。</summary>
        private const int MaxPackIndexFiles = 20000;

        private static readonly string[] ImageExtensions =
            { ".png", ".bmp", ".tga", ".jpg", ".jpeg", ".dds", ".spa", ".sph", ".tif", ".tiff", ".gif" };

        private static string FindInPack(string searchRoot, string wantLower, string stemLower)
        {
            if (string.IsNullOrEmpty(searchRoot)) return null;
            var index = PackIndex(searchRoot);
            if (index == null) return null;
            if (index.TryGetValue(wantLower, out var hit)) return hit;
            return index.TryGetValue(stemLower, out hit) ? hit : null;   // 副檔名被換過(.tga 存成 .png)
        }

        private static Dictionary<string, string> PackIndex(string searchRoot)
        {
            if (_packIndex.TryGetValue(searchRoot, out var cached)) return cached;
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            try
            {
                var all = Directory.GetFiles(searchRoot, "*", SearchOption.AllDirectories);
                Array.Sort(all, StringComparer.OrdinalIgnoreCase);   // 同名檔的取捨要與列舉順序無關
                foreach (var f in all)
                {
                    if (n >= MaxPackIndexFiles) break;
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (Array.IndexOf(ImageExtensions, ext) < 0) continue;
                    n++;
                    string fn = Path.GetFileName(f);
                    if (!index.ContainsKey(fn)) index[fn] = f;
                    string stem = Path.GetFileNameWithoutExtension(f);
                    if (!index.ContainsKey(stem)) index[stem] = f;
                }
            }
            catch { /* 讀不到就當這一包沒有可搜尋的貼圖 */ }
            SdoLog.Note("mmd", $"[mmd] 貼圖索引 '{searchRoot}': {n} 個圖檔");
            _packIndex[searchRoot] = index;
            return index;
        }

        private static Texture2D DecodeBmp(byte[] d)
        {
            if (d == null || d.Length < 54 || d[0] != 'B' || d[1] != 'M') return null;
            int dataOff = BitConverter.ToInt32(d, 10), w = BitConverter.ToInt32(d, 18), h = BitConverter.ToInt32(d, 22);
            int bpp = BitConverter.ToUInt16(d, 28), comp = BitConverter.ToInt32(d, 30);
            if (comp != 0 || (bpp != 24 && bpp != 32) || w <= 0 || h == 0) return null;
            bool topDown = h < 0; int H = Mathf.Abs(h), bpe = bpp / 8, stride = ((w * bpe + 3) / 4) * 4;
            if (dataOff + stride * H > d.Length) return null;
            var px = new Color32[w * H];
            for (int y = 0; y < H; y++)
            {
                int srcRow = dataOff + (topDown ? (H - 1 - y) : y) * stride, dstRow = y * w;
                for (int x = 0; x < w; x++) { int s = srcRow + x * bpe; byte a = bpe == 4 ? d[s + 3] : (byte)255; px[dstRow + x] = new Color32(d[s + 2], d[s + 1], d[s], a); }
            }
            var tex = new Texture2D(w, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat };
            tex.SetPixels32(px); tex.Apply(false);
            return tex;
        }

        private static void SetLayer(GameObject go, int layer) { go.layer = layer; foreach (Transform c in go.transform) SetLayer(c.gameObject, layer); }
        private static void LogMilestone(string m) { Debug.Log(m); SdoLog.Note("mmd", m); }

        /// <summary>Re-layer the whole MMD rig (the portrait / preview cameras cull by layer, and the driver's layer can
        /// be assigned AFTER the SDO parts are built — so the rig has to be able to follow it).</summary>
        public void SetLayer(int layer) { if (_mmdRoot != null) SetLayer(_mmdRoot.gameObject, layer); }

        /// <summary>
        /// 這具 MMD 身體上、對應某根 SDO 骨(<c>Bip01_*</c>)的骨頭;這個模型沒有對應的骨就 null。
        ///
        /// 「掛在骨頭上的特效」(手部光條…)一定要走這裡,不能直接掛 SDO 骨架的骨:retarget 只把 MMD 的骨
        /// **指向**跟 SDO 一樣的方向,骨頭本身多長是模型自己的。初音的肩→手腕鏈只有 SDO 的 77%(等高縮放後
        /// 14.65 vs 18.98,差 4.33 ≈ 身高的 8%),所以手一伸直,SDO 的 Bip01_*_Hand 就落在畫面上那隻手外面
        /// 一截 —— 光條看起來就是「跟手隔了一段空的」。
        /// </summary>
        public Transform BoneForBip01(string bip01)
        {
            if (!_ready || _bone == null || _bip01ToBone == null || string.IsNullOrEmpty(bip01)) return null;
            if (!_bip01ToBone.TryGetValue(bip01, out int i) || i < 0 || i >= _bone.Length) return null;
            return _bone[i];
        }

        /// <summary>手部光條要掛的那兩根骨:手腕 + 拇指根(<c>Hand</c> + <c>Finger0</c>,＝官方光條的內外兩緣)。
        /// 拇指根是很多 MMD 模型省略的一根(親指０),沒有就退食指/中指根 —— 光條要的只是「掌心到某根指根」
        /// 這條掌寬向量,哪一根指都成立。<paramref name="left"/> false = 右手。兩根都湊不齊回 false,呼叫端
        /// 就留在原本的 SDO 錨點上。</summary>
        public bool TryHandBones(bool left, out Transform hand, out Transform finger)
        {
            string p = left ? "Bip01_L_" : "Bip01_R_";
            hand = BoneForBip01(p + "Hand");
            finger = BoneForBip01(p + "Finger0");
            if (finger == null) finger = BoneForBip01(p + "Finger1");
            if (finger == null) finger = BoneForBip01(p + "Finger2");
            return hand != null && finger != null;
        }

        // Portrait framing for an MMD head box, calibrated against the SDO head portrait so the swapped-in model's head
        // lands where the official one does. The SDO cam uses dist = 1.9×box and aim = centre − 0.11×box, but ITS box is
        // the FACE+HAIR renderer bounds — which include the hair hanging past the chin, so it is ~40% taller than the head
        // itself. Feeding the MMD head box (chin→crown, nothing below) into those same numbers frames far too tight (chin
        // at 75% down the frame vs the official 62%). These put the MMD head at 55% of the frame height, centred at 36%
        // down — i.e. spanning 0.09…0.64, which is where the SDO head sits (0.05…0.62).
        //
        // 🔴 這組常數是照「**只有頭、什麼都沒戴**的框」訂的 —— 餵進來的框一定要是 MmdHeadBounds 用「頭」骨 tail
        //    算出來的那個。2026-08-05 之前餵的是量幾何的框,而綁在頭骨上的**不只有頭**:髮皮/前髪(Ika +21%、
        //    YYB +11%)、角(La+ Darknesss 高到臉頂的 1.9 倍)、帽子、髮飾、呆毛…… 每包模型多的東西都不一樣
        //    → 相機退太遠,臉只佔畫面高 45%~29%,而不是這裡設計的 54.9%:結算那一排裡 MMD 那格的頭就比旁邊
        //    SDO 的小一圈、位置也低一截(使用者回報兩次)。修的是量法,不是常數。
        public const float PortraitFrameDist = 2.2f;
        public const float PortraitAimUp = 0.25f;

        /// <summary>頭貼相機該怎麼框這一顆 MMD 頭(<paramref name="headBoxWorld"/> = <see cref="TryHeadBounds"/>/
        /// <see cref="TryHeadBoundsRest"/> 給的框)。<paramref name="aimX"/> 把臉往側邊擺正(結算那格用
        /// <c>headAimOffset.x</c>,房間那格是 0)。
        ///
        /// 🔴 回傳的 <paramref name="dist"/> 是**世界單位**(框本身已經含了 avatar 的 scale),跟 SDO 那條
        /// <see cref="HeadBoneFraming"/> 的「模型單位、相對頭骨」**不是同一種量**。兩邊的值互相指派 = 結算列
        /// 其他人的頭會大小跑掉(見 ScreenGameplay.Hud 的 UpdateHeadPortraitCam)。</summary>
        public static void FramePortrait(Bounds headBoxWorld, float zoom, float aimX, out Vector3 aim, out float dist)
        {
            float h = Mathf.Max(headBoxWorld.size.y, 1e-4f);
            aim = headBoxWorld.center + new Vector3(aimX, -PortraitAimUp * h, 0f);
            dist = h * PortraitFrameDist * Mathf.Max(0.05f, zoom);
        }

        /// <summary>Where a head-portrait camera should aim, in world space: an upright box the size of this model's head,
        /// anchored on the LIVE head bone — the room 頭貼 re-aims at this every frame, keeping the head locked in the
        /// middle of the slot as the dancer walks and sways. Frame it with <see cref="PortraitFrameDist"/> /
        /// <see cref="PortraitAimUp"/>, NOT the SDO constants. False if the model has no usable head (see
        /// <see cref="MmdHeadBounds"/>), in which case the caller keeps its own framing.</summary>
        public bool TryHeadBounds(out Bounds world)
        {
            world = default;
            if (_sh == null || !_sh.HasHead || !_ready || _bone == null || _sh.HeadBone < 0 || _sh.HeadBone >= _bone.Length || _bone[_sh.HeadBone] == null) return false;
            return StableHeadBox(_bone[_sh.HeadBone].position, _bone[_sh.HeadBone].lossyScale.y, out world);
        }

        /// <summary>The same box in the REST pose (head bone unrotated, the dancer's own transform still applied) — for a
        /// FIXED portrait cam (結算頭貼), so the idle head-bob plays out inside the frame instead of being chased.</summary>
        public bool TryHeadBoundsRest(out Bounds world)
        {
            world = default;
            if (_sh == null || !_sh.HasHead || !_ready || _mmdRoot == null) return false;
            return StableHeadBox(_mmdRoot.TransformPoint(_sh.HeadRestPos), _mmdRoot.lossyScale.y, out world);
        }

        // An UPRIGHT box of the REST-measured head size, anchored at a head position. Deliberately not the axis-aligned
        // bounds of the POSED head+hair: an oriented box's AABB changes size and centre as it rotates, so framing that
        // pumps the camera in and out as the head nods (reads as the model swaying toward you) and drags the centre off
        // toward whichever way the twintails swing (the face slides out of the slot as the dancer turns). Only the ANCHOR
        // is allowed to move — so a walk/bob re-centres the head, but a head-turn does not disturb the framing at all.
        private bool StableHeadBox(Vector3 headWorld, float scale, out Bounds world)
        {
            Vector3 size = _sh.HeadLocal.size * scale;
            world = new Bounds(headWorld + Vector3.up * (_sh.HeadLocal.center.y * scale), size);
            return size.y > 1e-4f;
        }

        public void SetVisible(bool on)
        {
            var smr = GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr != null) smr.enabled = on;
            enabled = on;
            // 藏起來的期間 LateUpdate 沒跑(enabled=false),骨頭停在藏起來的那一刻;再顯示出來時動作已經
            // 走遠了 → 布料會把那段差當成瞬移。跟剛建好一樣先黏幾幀。
            if (on && !_visible) _settleFrames = SettleFrames;
            _visible = on; UpdateSpring();   // spring runs only when visible AND physics-enabled (no toggle clobber)
        }
    }
}
