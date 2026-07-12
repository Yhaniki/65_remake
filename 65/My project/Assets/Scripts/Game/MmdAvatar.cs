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
    /// right way. Leaf bones with no mapped child (hand/head/foot tips) fall back to the world-delta. MMD 付与 append
    /// bones (the leg "D" chain the mesh is skinned to) then copy their FK source's local rotation.
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
        /// <summary>Show MMD sphere maps (matcap sheen/glow). Toggle live to compare.</summary>
        public bool ShowSphere = true;
        /// <summary>Cel-shading toon ramp (N·L, fixed light). Toggle live.</summary>
        public bool ShowToon = true;
        /// <summary>MMD pencil outline (inverted-hull edge). Toggle live.</summary>
        public bool ShowOutline = true;
        /// <summary>Flip the mesh UV V (uv.y = 1-uv.y) — the canonical MMD→Unity fix (PMX UVs are V-down). Toggle live
        /// to find the orientation whose atlas maps correctly (green necktie, not skin).</summary>
        public bool FlipV = true;
        private Mesh _mesh; private Vector2[] _uvVerbatim, _uvFlipped;

        private Transform _mmdRoot;
        private Transform[] _bone;
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
        private readonly List<KeyValuePair<Material, float>> _sphereMats = new List<KeyValuePair<Material, float>>();  // (material, its sphere mode)
        private readonly List<Material> _toonMats = new List<Material>();
        private readonly List<KeyValuePair<Material, float>> _edgeMats = new List<KeyValuePair<Material, float>>();   // (material, its edge size)
        private MmdSpringBones _spring;
        private MmdMagicaCloth _magica;   // preferred cloth solver (Magica Cloth 2); _spring is the fallback
        private bool _visible = true, _physicsOn = true;   // physics runs only when BOTH hold (independent toggles)
        private bool[] _hide;                       // material not drawn (morph-hidden / overlay)
        private bool _ready;

        public static MmdAvatar Build(SdoAvatar driver, PmxLoader pmx, string textureDir, int layer)
        {
            if (driver == null || driver.Hrc == null || pmx == null || pmx.Bones.Count == 0 || pmx.VertexCount == 0)
                return null;
            var rootGo = new GameObject("MmdAvatar");
            rootGo.transform.SetParent(driver.transform, false);
            var self = rootGo.AddComponent<MmdAvatar>();
            self.Driver = driver;
            self._mmdRoot = rootGo.transform;
            try { self.Construct(pmx, textureDir, layer); }
            catch (Exception e) { Debug.LogWarning("[mmd] build fail: " + e.Message + "\n" + e.StackTrace); UnityEngine.Object.Destroy(rootGo); return null; }
            return self;
        }

        private void Construct(PmxLoader pmx, string textureDir, int layer)
        {
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
            _unitScale = hrcHeight / mmdHeight;

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

            // ---- materials (sets _hide) + mesh (skips hidden submeshes) ----
            var meshGo = new GameObject("MmdMesh");
            meshGo.transform.SetParent(_mmdRoot, false);
            var smr = meshGo.AddComponent<SkinnedMeshRenderer>();
            var mats = BuildMaterials(pmx, textureDir);
            var mesh = BuildMesh(pmx);
            smr.sharedMesh = mesh;
            smr.bones = _bone;
            smr.rootBone = _mmdRoot;
            smr.updateWhenOffscreen = true;
            smr.sharedMaterials = mats;

            var binds = new Matrix4x4[bc];
            for (int i = 0; i < bc; i++) binds[i] = _bone[i].worldToLocalMatrix * meshGo.transform.localToWorldMatrix;
            mesh.bindposes = binds;

            // ---- retarget wiring ----
            _hrcIndex = new int[bc]; _hrcRestInv = new Quaternion[bc];
            _aim = new bool[bc]; _aimChildHrc = new int[bc]; _aimRestDir = new Vector3[bc]; _useDelta = new bool[bc];
            var bip01ToMmd = new Dictionary<string, int>();
            for (int i = 0; i < bc; i++)
            {
                _hrcIndex[i] = -1;
                if (MmdBoneMap.TryGetBip01(pmx.Bones[i].NameJp, out string bip01))
                {
                    if (!bip01ToMmd.ContainsKey(bip01)) bip01ToMmd[bip01] = i;
                    if (hrc.Index.TryGetValue(bip01, out int h)) { _hrcIndex[i] = h; _hrcRestInv[i] = Quaternion.Inverse(hrc.BindWorld[h].rotation); }
                    if (bip01 == "Bip01_Head") _useDelta[i] = true;   // head: absolute orientation → stays upright in idle
                }
                if (pmx.Bones[i].NameJp == MmdBoneMap.RootMmdBone) _rootBone = i;
            }
            if (_rootBone >= 0) _useDelta[_rootBone] = true;           // root carries the whole-body rotation
            int aimed = BuildAim(pmx, hrc, bip01ToMmd);
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
            _magica = MmdMagicaCloth.Setup(_mmdRoot.gameObject, _bone, _parent, pmx, _unitScale);   // Magica Cloth 2 (preferred)
            if (_magica == null)   // package missing / setup failed → hand-rolled spring bones
            {
                _spring = MmdSpringBones.Attach(_mmdRoot.gameObject, _bone, _parent, pmx, _unitScale, _mmdRoot);
                BuildColliders(pmx, _unitScale);
            }
            _ready = true;
            string phys = _magica != null ? $"magica({_magica.ClothCount} cloth,{_magica.ColliderCount} col)" : (_spring != null ? "spring" : "none");
            LogMilestone($"[mmd] built '{pmx.NameJp}': {pmx.VertexCount} verts, {pmx.Materials.Count} mats, {bc} bones, " +
                         $"scale={_unitScale:F3}, facing={_qroot.eulerAngles.y:F0}°, driven={CountDriven()}/{bc}, aimed={aimed}, " +
                         $"sphere={_sphereMats.Count}, toon={_toonMats.Count}, edge={_edgeMats.Count}, physics={pmx.PhysicsBones.Count}({phys})");
        }

        // Precompute the aim target/direction for every mapped bone that has a mapped child.
        private int BuildAim(PmxLoader pmx, HrcLoader hrc, Dictionary<string, int> bip01ToMmd)
        {
            var mappedHrcNames = new HashSet<string>(MmdBoneMap.ToBip01.Values);
            var hrcChildren = new List<int>[hrc.Names.Length];
            for (int c = 0; c < hrc.Parent.Length; c++) { int p = hrc.Parent[c]; if (p < 0) continue; (hrcChildren[p] ?? (hrcChildren[p] = new List<int>())).Add(c); }
            int n = 0;
            for (int i = 0; i < pmx.Bones.Count; i++)
            {
                int h = _hrcIndex[i]; if (h < 0 || hrcChildren[h] == null) continue;
                int hrcChild = -1;
                foreach (int c in hrcChildren[h]) if (mappedHrcNames.Contains(hrc.Names[c])) { hrcChild = c; break; }
                if (hrcChild < 0 || !bip01ToMmd.TryGetValue(hrc.Names[hrcChild], out int mmdChild)) continue;
                Vector3 rd = pmx.Bones[mmdChild].Position - pmx.Bones[i].Position;          // MMD rest dir (root-local)
                Vector3 hd = (Vector3)hrc.BindWorld[hrcChild].GetColumn(3) - (Vector3)hrc.BindWorld[h].GetColumn(3);
                if (rd.sqrMagnitude < 1e-6f || hd.sqrMagnitude < 1e-6f) continue;           // degenerate (e.g. Bip01→Pelvis) → delta fallback
                _aim[i] = true; _aimChildHrc[i] = hrcChild; _aimRestDir[i] = rd.normalized; n++;
            }
            return n;
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

            if (DriveRootTranslation && _rootBone >= 0 && _hrcRootIndex >= 0)
            {
                Vector3 d = (Vector3)Driver.BoneAnimWorld(_hrcRootIndex).GetColumn(3) - _hrcRootRestPos;
                _bone[_rootBone].localPosition = _rootRestLocal + (_qrootInv * d) / _unitScale;
            }
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

        // ---- mesh ----
        private Mesh BuildMesh(PmxLoader pmx)
        {
            int vc = pmx.VertexCount;
            var mesh = new Mesh { name = "mmd_" + pmx.NameEn };
            if (vc > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = pmx.Positions; mesh.normals = pmx.Normals;
            _uvVerbatim = pmx.Uvs;
            _uvFlipped = new Vector2[vc];
            for (int k = 0; k < vc; k++) _uvFlipped[k] = new Vector2(pmx.Uvs[k].x, 1f - pmx.Uvs[k].y);
            mesh.uv = FlipV ? _uvFlipped : _uvVerbatim;   // PMX UVs are V-down; flip for Unity (toggle to verify)
            _mesh = mesh;
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
                if (_hide != null && _hide[s]) { mesh.SetTriangles(Array.Empty<int>(), s); continue; }
                var m = pmx.Materials[s];
                var tris = new int[m.IndexCount];
                Array.Copy(pmx.Indices, m.IndexStart, tris, 0, m.IndexCount);
                mesh.SetTriangles(tris, s);
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---- materials (MMD shader: base + sphere; alpha class → opaque/cutout; morph-overlay & a=0 hidden) ----
        private Material[] BuildMaterials(PmxLoader pmx, string dir)
        {
            var shader = Shader.Find("Sdo/MmdModel") ?? Shader.Find("Unlit/Texture");
            var col = Shader.Find("Unlit/Color") ?? shader;
            var mats = new Material[pmx.Materials.Count];
            _hide = new bool[pmx.Materials.Count];
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
                Texture2D tex = texName != null ? LoadTexture(dir, texName) : null;
                if (tex == null)
                {
                    mats[i] = new Material(col) { color = pm.Diffuse, name = pm.NameJp ?? ("mat" + i) };
                    SdoLog.Note("mmd", $"  mat[{i}] '{pm.NameJp}' tex='{texName ?? "(none)"}' -> FALLBACK colour");
                    continue;
                }
                AlphaStats(tex, out float midFrac, out float holeFrac);
                // A substantially semi-transparent texture on the face = a morph-driven expression/blush/shadow overlay
                // (照れ/表情/髪影). Without vertex/UV morphs these render wrong (the pink blob), so hide them.
                if (midFrac >= 0.15f)
                {
                    _hide[i] = true; mats[i] = new Material(shader);
                    SdoLog.Note("mmd", $"  mat[{i}] '{pm.NameJp}' tex='{Path.GetFileName(texName)}' mid={midFrac:P0} -> HIDDEN (morph overlay)");
                    continue;
                }
                bool cutout = holeFrac >= 0.02f || pm.DoubleSided;

                var mat = new Material(shader) { name = pm.NameEn ?? pm.NameJp ?? ("mat" + i) };
                mat.SetTexture("_MainTex", tex);
                mat.SetColor("_Color", new Color(pm.Diffuse.r, pm.Diffuse.g, pm.Diffuse.b, 1f));
                mat.SetFloat("_Cull", pm.DoubleSided ? 0f : 2f);           // Off : Back
                // opaque vs alpha-test cutout (blend overlays already hidden above)
                mat.SetFloat("_AlphaClip", cutout ? 1f : 0f);
                mat.SetFloat("_Cutoff", 0.5f);
                mat.SetFloat("_SrcBlend", 1f); mat.SetFloat("_DstBlend", 0f); mat.SetFloat("_ZWrite", 1f);
                mat.renderQueue = cutout ? (int)RenderQueue.AlphaTest : (int)RenderQueue.Geometry;

                // sphere map (matcap): the MMD "shine" — eyes/skin/metal. Sampled by view normal, so NOT UV-flipped.
                float sphereMode = 0f;
                if ((pm.SphereMode == 1 || pm.SphereMode == 2) && pm.SphereIndex >= 0 && pm.SphereIndex < pmx.TexturePaths.Length)
                {
                    var sph = LoadTexture(dir, pmx.TexturePaths[pm.SphereIndex]);
                    if (sph != null) { mat.SetTexture("_SphereTex", sph); sphereMode = pm.SphereMode; _sphereMats.Add(new KeyValuePair<Material, float>(mat, sphereMode)); }
                }
                mat.SetFloat("_SphereMode", ShowSphere ? sphereMode : 0f);

                // toon ramp (cel shading): a vertical light→shadow gradient sampled by N·L. Either a per-material toon
                // TEXTURE (ToonIndex) or a built-in SHARED toon (ToonShared 0..9) → a synthesized 2-tone ramp fallback.
                Texture2D toon = pm.ToonIndex >= 0 && pm.ToonIndex < pmx.TexturePaths.Length ? LoadTexture(dir, pmx.TexturePaths[pm.ToonIndex]) : null;
                if (toon == null && pm.ToonShared >= 0) toon = DefaultToonRamp();
                bool hasToon = toon != null;
                if (hasToon) { toon.wrapMode = TextureWrapMode.Clamp; mat.SetTexture("_ToonTex", toon); _toonMats.Add(mat); }
                mat.SetFloat("_UseToon", (ShowToon && hasToon) ? 1f : 0f);

                // pencil outline: only edge-flagged materials get a non-zero edge size.
                mat.SetColor("_EdgeColor", pm.EdgeColor);
                if (pm.HasEdge) _edgeMats.Add(new KeyValuePair<Material, float>(mat, pm.EdgeSize));
                mat.SetFloat("_EdgeSize", (ShowOutline && pm.HasEdge) ? pm.EdgeSize : 0f);

                mats[i] = mat;
                SdoLog.Note("mmd", $"  mat[{i}] '{pm.NameJp}' tex='{Path.GetFileName(texName)}' {(cutout ? "CUTOUT" : "opaque")}{(pm.DoubleSided ? " 2sided" : "")}{(sphereMode > 0 ? " +sphere" + (int)sphereMode : "")}{(hasToon ? " +toon" : "")}{(pm.HasEdge ? " +edge" : "")}");
            }
            return mats;
        }

        /// <summary>Live toggle: turn all sphere maps on/off (restores each material's authored sphere mode).</summary>
        public void SetSphere(bool on) { ShowSphere = on; foreach (var kv in _sphereMats) if (kv.Key != null) kv.Key.SetFloat("_SphereMode", on ? kv.Value : 0f); }

        /// <summary>Live toggle: flip the mesh UV V (find the atlas-correct orientation without a recompile).</summary>
        public void SetFlipV(bool on) { FlipV = on; if (_mesh != null && _uvVerbatim != null) _mesh.uv = on ? _uvFlipped : _uvVerbatim; }

        /// <summary>Live toggle: cel-shading toon ramp on/off.</summary>
        public void SetToon(bool on) { ShowToon = on; foreach (var m in _toonMats) if (m != null) m.SetFloat("_UseToon", on ? 1f : 0f); }

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
            t.SetPixels32(px); t.Apply(false); _defToon = t; return t;
        }

        /// <summary>Live toggle: pencil outline on/off (restores each material's authored edge size).</summary>
        public void SetOutline(bool on) { ShowOutline = on; foreach (var kv in _edgeMats) if (kv.Key != null) kv.Key.SetFloat("_EdgeSize", on ? kv.Value : 0f); }

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

        private static void AlphaStats(Texture2D tex, out float midFrac, out float holeFrac)
        {
            midFrac = 0f; holeFrac = 0f;
            Color32[] px; try { px = tex.GetPixels32(); } catch { return; }
            if (px.Length == 0) return;
            int step = Mathf.Max(1, px.Length / 20000), mid = 0, hole = 0, n = 0;
            for (int k = 0; k < px.Length; k += step) { byte a = px[k].a; if (a < 16) hole++; else if (a < 250) mid++; n++; }
            if (n > 0) { midFrac = (float)mid / n; holeFrac = (float)hole / n; }
        }

        // Resolve + decode a PMX texture. NO vertical flip: the PMX's verbatim (D3D) UVs sample correctly against this
        // project's texel layout — the SDO DDS pipeline puts image-top at texel row 0, and Unity's PNG/BMP decode here
        // matches, so a flip actually scrambled the clothing atlas (skin bled onto the necktie). Verified by rendering
        // the model both ways: unflipped = correct Miku (green tie, right costume), flipped = broken.
        private static Texture2D LoadTexture(string dir, string rel)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(rel)) return null;
            string path = ResolvePath(dir, rel.Replace('\\', '/'));
            if (path == null) return null;
            byte[] b; try { b = File.ReadAllBytes(path); } catch { return null; }
            string ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                if (ext == ".tga") return DdsLoader.LoadTga(b);
                if (b.Length > 2 && b[0] == 'B' && b[1] == 'M') return DecodeBmp(b);
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
                return t.LoadImage(b) ? t : null;
            }
            catch { return null; }
        }

        private static string ResolvePath(string dir, string rel)
        {
            string full = Path.Combine(dir, rel);
            if (File.Exists(full)) return full;
            string sub = Path.GetDirectoryName(full), file = Path.GetFileName(full);
            if (string.IsNullOrEmpty(sub) || !Directory.Exists(sub)) return null;
            string stem = Path.GetFileNameWithoutExtension(file).ToLowerInvariant(), want = file.ToLowerInvariant(), stemHit = null;
            foreach (var f in Directory.GetFiles(sub))
            {
                string fn = Path.GetFileName(f).ToLowerInvariant();
                if (fn == want) return f;
                if (stemHit == null && Path.GetFileNameWithoutExtension(f).ToLowerInvariant() == stem) stemHit = f;
            }
            return stemHit;
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

        public void SetVisible(bool on)
        {
            var smr = GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr != null) smr.enabled = on;
            enabled = on;
            _visible = on; UpdateSpring();   // spring runs only when visible AND physics-enabled (no toggle clobber)
        }
    }
}
