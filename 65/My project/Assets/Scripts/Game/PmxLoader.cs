using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Runtime parser for MMD ".pmx" models (PMX 2.0 / 2.1, the "PMX " magic). Fits the same "load original art
    /// straight from disk at runtime" model the SDO loaders (HrcLoader / MshLoader / MotLoader) already use, so an
    /// MMD model can stand in for the native SDO avatar with no import step. Parses ONLY what the display + retarget
    /// pipeline needs — vertices (position / normal / UV / up-to-4 bone influences), the material-partitioned index
    /// list, texture paths, materials, the bone list (name + rest position + parent), and the physics section (rigid
    /// bodies → colliders + cloth, joints → per-bone firmness); morphs / display frames are consumed but not stored.
    ///
    /// Coordinate note: PMX and Direct3D9 are BOTH left-handed, Y-up — the same convention <see cref="HrcLoader"/>
    /// treats as "Unity space" (it does raw.Tᵀ with NO axis flip). So PMX vertex/bone positions map 1:1 into the SDO
    /// avatar's model space; the MMD skeleton and the HRC skeleton share a global orientation, which is what lets the
    /// world-space rotation-delta retarget in <see cref="MmdAvatar"/> transfer poses directly.
    /// </summary>
    public sealed class PmxLoader
    {
        public string NameJp = "", NameEn = "";
        public float Version;

        // ---- geometry (parallel per-vertex arrays; length = VertexCount) ----
        public Vector3[] Positions;
        public Vector3[] Normals;
        public Vector2[] Uvs;
        public int[] BoneIdx;      // length = VertexCount*4 (bone index into Bones, or -1 for an unused slot)
        public float[] BoneWt;     // length = VertexCount*4 (normalised so the used slots sum to 1)
        public int[] Indices;      // triangle vertex indices (length = 3*triangleCount)
        public float[] EdgeScale;  // per-vertex outline-width multiplier (length = VertexCount)

        public string[] TexturePaths;   // as stored (relative to the .pmx, may use '\' and sub-folders)

        public sealed class Material
        {
            public string NameJp, NameEn;
            public Color Diffuse = Color.white;
            public int TextureIndex = -1;       // into TexturePaths (-1 = none)
            public int SphereIndex = -1;
            public int SphereMode;              // 0 none, 1 multiply, 2 additive, 3 sub-tex
            public bool DoubleSided;            // draw-flag bit 0x01 (no back-face cull)
            public bool HasEdge;                // draw-flag bit 0x10 (pencil outline)
            public Color EdgeColor = Color.black;
            public float EdgeSize = 1f;
            public int ToonIndex = -1;          // toon texture index (into TexturePaths), or -1
            public int ToonShared = -1;         // internal shared toon 0..9, or -1
            public int IndexStart;              // first index in Indices belonging to this material
            public int IndexCount;              // number of indices (3 per triangle)
        }
        public List<Material> Materials = new List<Material>();

        /// <summary>Bone indices with a physics-mode (mode 1/2) rigid body — the hair/skirt/etc. that should sway
        /// (spring-bone sim). Empty if the physics section is absent/unparsable (mesh still works).</summary>
        public HashSet<int> PhysicsBones = new HashSet<int>();

        /// <summary>One MMD Bullet rigid body. The model author tunes these per bone: the KINEMATIC ones (Mode 0) are
        /// the body's collision shapes (head/torso/arms/legs capsules+spheres) and the DYNAMIC ones (Mode 1/2) are the
        /// hair/skirt/tie particles. <see cref="MmdMagicaCloth"/> converts both — kinematic → colliders, dynamic
        /// damping/size → cloth firmness/thickness — so the physics matches the authored values instead of guesses.</summary>
        public sealed class RigidBody
        {
            public string Name;                 // JP name — the canonical part label (Bang/Twintail/Dress/Tie/…), used
                                                // to group cloth by part (the ATTACHED bone is often generically named)
            public int Bone = -1;               // attached bone index (-1 = none)
            public byte Group;                  // collision group 0..15
            public ushort Mask;                 // collision-ENABLE mask: bit g set ⇒ collides with group g. Two bodies
                                                // collide iff each has the other's group bit set (authored filtering —
                                                // e.g. the skirt is set NOT to touch the giant hip flare capsule).
            public byte Shape;                  // 0 sphere, 1 box, 2 capsule
            public Vector3 Size;                // raw model units: sphere(r,_,_) box(hx,hy,hz) capsule(r,height,_)
            public Vector3 Position;            // rigid-body centre, model space (raw)
            public Vector3 Rotation;            // orientation, radians (model space euler)
            public byte Mode;                   // 0 kinematic (collider), 1 physics, 2 physics + bone
            public float Mass, LinearDamp, AngularDamp;
        }
        public List<RigidBody> RigidBodies = new List<RigidBody>();

        /// <summary>Per dynamic-body bone: the authored joint rotation-limit "tightness" (mean |max−min| over XYZ, in
        /// radians; the MIN across a bone's joints). Bullet 6DOF convention: lower==upper ⇒ locked (rigid), lower&gt;upper
        /// ⇒ free. Only a stiffness signal for chains WITHOUT a restoring spring — see <see cref="BoneJointSpring"/>.</summary>
        public Dictionary<int, float> BoneJointLimit = new Dictionary<int, float>();

        /// <summary>Per dynamic-body bone: the authored joint ROTATION SPRING (Bullet btGeneric6DofSpring setStiffness;
        /// mean |xyz|, MAX across the bone's joints). THIS is the real "springs back to its shape" signal: the fringe
        /// (前髪) uses ~5 (actively pinned to the forehead → must be stiff), the free-swinging twintails/tie use 0. The
        /// rotation-limit width is NOT a reliable stiffness signal (locked-looking 0/0 hair actually swings).</summary>
        public Dictionary<int, float> BoneJointSpring = new Dictionary<int, float>();

        public sealed class Bone
        {
            public string NameJp, NameEn;
            public Vector3 Position;            // rest position in model space
            public int Parent = -1;
            public int Layer;                   // transform level ("変形階層")
            public ushort Flags;
            // append / inherit ("付与") — this bone copies AppendParent's animated local rotation/translation × weight.
            // The 準標準ボーン leg-"D" bones the mesh is skinned to inherit the FK leg bones this way (flag 0x0100/0x0200).
            public int AppendParent = -1;
            public float AppendWeight;
            public bool AppendRotation, AppendTranslation;
        }
        public List<Bone> Bones = new List<Bone>();

        public int VertexCount => Positions?.Length ?? 0;

        // ---- header globals (byte[8]) ----
        private int _enc;          // 0 = UTF-16LE, 1 = UTF-8
        private int _extraUv;      // additional vec4 count (0..4)
        private int _vIdx, _tIdx, _mIdx, _bIdx, _mfIdx, _rIdx;   // index byte sizes

        /// <summary>Parse a .pmx byte blob. Returns null on a bad magic / truncated header, or throws nothing —
        /// callers get null and log. Fully in-memory; no Unity object is created here (that's <see cref="MmdAvatar"/>).</summary>
        public static PmxLoader Load(byte[] d)
        {
            if (d == null || d.Length < 8) return null;
            if (!(d[0] == (byte)'P' && d[1] == (byte)'M' && d[2] == (byte)'X' && d[3] == (byte)' ')) return null;
            var p = new PmxLoader();
            try { p.Parse(d); } catch (Exception e) { Debug.LogWarning("[pmx] parse fail: " + e.Message); return null; }
            return p;
        }

        private int _pos;
        private byte[] _d;

        private void Parse(byte[] d)
        {
            _d = d; _pos = 4;
            Version = F();
            int globals = U8();
            var g = new byte[Mathf.Max(globals, 8)];
            for (int i = 0; i < globals; i++) g[i] = U8();
            _enc = g[0]; _extraUv = g[1];
            _vIdx = g[2]; _tIdx = g[3]; _mIdx = g[4]; _bIdx = g[5]; _mfIdx = g[6]; _rIdx = g[7];

            NameJp = Text(); NameEn = Text();
            Text(); Text();   // comment local / universal (skip)

            ParseVertices();
            ParseFaces();
            ParseTextures();
            ParseMaterials();
            ParseBones();
            // morphs + display frames are skipped; rigid bodies + joints drive the physics (colliders + cloth firmness).
            // Guarded so a format hiccup here still leaves a working mesh + skeleton.
            try { ParseMorphs(); ParseDisplayFrames(); ParseRigidBodies(); ParseJoints(); }
            catch (Exception e) { Debug.LogWarning("[pmx] physics section skipped: " + e.Message); }
        }

        private void ParseVertices()
        {
            int vc = I32();
            Positions = new Vector3[vc];
            Normals = new Vector3[vc];
            Uvs = new Vector2[vc];
            EdgeScale = new float[vc];
            BoneIdx = new int[vc * 4];
            BoneWt = new float[vc * 4];
            for (int i = 0; i < vc; i++)
            {
                Positions[i] = V3();
                Normals[i] = V3();
                Uvs[i] = V2();
                for (int e = 0; e < _extraUv; e++) { F(); F(); F(); F(); }   // additional UV vec4s (skip)

                int deform = U8();
                int b0, b1, b2, b3; float w0, w1, w2, w3;
                switch (deform)
                {
                    case 0:   // BDEF1
                        b0 = BoneRef(); b1 = b2 = b3 = -1; w0 = 1f; w1 = w2 = w3 = 0f;
                        break;
                    case 1:   // BDEF2
                        b0 = BoneRef(); b1 = BoneRef(); w0 = F(); w1 = 1f - w0; b2 = b3 = -1; w2 = w3 = 0f;
                        break;
                    case 2:   // BDEF4
                        b0 = BoneRef(); b1 = BoneRef(); b2 = BoneRef(); b3 = BoneRef();
                        w0 = F(); w1 = F(); w2 = F(); w3 = F();
                        break;
                    case 3:   // SDEF — treat like BDEF2 (drop the C/R0/R1 spherical-deform correction)
                        b0 = BoneRef(); b1 = BoneRef(); w0 = F(); w1 = 1f - w0; b2 = b3 = -1; w2 = w3 = 0f;
                        V3(); V3(); V3();   // C, R0, R1 (skip)
                        break;
                    case 4:   // QDEF (2.1) — same layout as BDEF4 (dual-quaternion, approximate with linear blend)
                        b0 = BoneRef(); b1 = BoneRef(); b2 = BoneRef(); b3 = BoneRef();
                        w0 = F(); w1 = F(); w2 = F(); w3 = F();
                        break;
                    default:
                        throw new Exception("unknown weight deform " + deform + " @vert " + i);
                }
                EdgeScale[i] = F();   // per-vertex outline width multiplier

                // normalise the used influences so they sum to 1 (some models leave un-normalised weights)
                float sum = 0f;
                if (b0 >= 0 && w0 > 0f) sum += w0; else { b0 = -1; w0 = 0f; }
                if (b1 >= 0 && w1 > 0f) sum += w1; else { b1 = -1; w1 = 0f; }
                if (b2 >= 0 && w2 > 0f) sum += w2; else { b2 = -1; w2 = 0f; }
                if (b3 >= 0 && w3 > 0f) sum += w3; else { b3 = -1; w3 = 0f; }
                if (sum > 1e-6f) { float k = 1f / sum; w0 *= k; w1 *= k; w2 *= k; w3 *= k; }
                int o = i * 4;
                BoneIdx[o] = b0; BoneIdx[o + 1] = b1; BoneIdx[o + 2] = b2; BoneIdx[o + 3] = b3;
                BoneWt[o] = w0; BoneWt[o + 1] = w1; BoneWt[o + 2] = w2; BoneWt[o + 3] = w3;
            }
        }

        private void ParseFaces()
        {
            int n = I32();               // total index count (= 3 * triangles)
            Indices = new int[n];
            for (int i = 0; i < n; i++) Indices[i] = VertRef();
        }

        private void ParseTextures()
        {
            int n = I32();
            TexturePaths = new string[n];
            for (int i = 0; i < n; i++) TexturePaths[i] = Text();
        }

        private void ParseMaterials()
        {
            int n = I32();
            int idxCursor = 0;
            for (int i = 0; i < n; i++)
            {
                var m = new Material();
                m.NameJp = Text(); m.NameEn = Text();
                m.Diffuse = new Color(F(), F(), F(), F());
                F(); F(); F();          // specular RGB
                F();                    // specular strength
                F(); F(); F();          // ambient RGB
                int flags = U8();
                m.DoubleSided = (flags & 0x01) != 0;
                m.HasEdge = (flags & 0x10) != 0;
                m.EdgeColor = new Color(F(), F(), F(), F());   // edge colour RGBA
                m.EdgeSize = F();                              // edge width
                m.TextureIndex = TexRef();
                m.SphereIndex = TexRef();
                m.SphereMode = U8();
                int toonRef = U8();
                if (toonRef == 0) m.ToonIndex = TexRef();   // toon texture index
                else m.ToonShared = U8();                   // shared toon index (0..9)
                Text();                       // memo
                int surf = I32();             // index count for this material (partition of the face list)
                m.IndexStart = idxCursor;
                m.IndexCount = surf;
                idxCursor += surf;
                Materials.Add(m);
            }
        }

        private void ParseBones()
        {
            int n = I32();
            for (int i = 0; i < n; i++)
            {
                var b = new Bone();
                b.NameJp = Text(); b.NameEn = Text();
                b.Position = V3();
                b.Parent = BoneRef();
                b.Layer = I32();
                b.Flags = (ushort)U16();
                // --- variable tail block, depends on the flags ---
                bool indexedTail = (b.Flags & 0x0001) != 0;
                if (indexedTail) BoneRef(); else V3();                        // tail: bone index OR vec3 offset
                if ((b.Flags & 0x0100) != 0 || (b.Flags & 0x0200) != 0)       // inherit ("付与") rot/trans: parent + weight
                {
                    b.AppendParent = BoneRef(); b.AppendWeight = F();
                    b.AppendRotation = (b.Flags & 0x0100) != 0;
                    b.AppendTranslation = (b.Flags & 0x0200) != 0;
                }
                if ((b.Flags & 0x0400) != 0) V3();                           // fixed axis
                if ((b.Flags & 0x0800) != 0) { V3(); V3(); }                 // local coord X/Z
                if ((b.Flags & 0x2000) != 0) I32();                          // external parent key
                if ((b.Flags & 0x0020) != 0)                                 // IK
                {
                    BoneRef();                                               // IK target
                    I32();                                                   // loop count
                    F();                                                     // limit angle
                    int links = I32();
                    for (int l = 0; l < links; l++)
                    {
                        BoneRef();
                        int hasLimit = U8();
                        if (hasLimit != 0) { V3(); V3(); }                   // lower / upper angle
                    }
                }
                Bones.Add(b);
            }
        }

        // Morphs are skipped (no runtime morph support yet) but must be consumed to reach the physics section.
        private void ParseMorphs()
        {
            int n = I32();
            for (int i = 0; i < n; i++)
            {
                Text(); Text();   // names
                U8();             // panel
                int type = U8();
                int oc = I32();
                for (int o = 0; o < oc; o++)
                {
                    switch (type)
                    {
                        case 0: MorphRef(); F(); break;                                   // group
                        case 1: VertRef(); V3(); break;                                   // vertex
                        case 2: BoneRef(); V3(); F(); F(); F(); F(); break;               // bone (trans + quat)
                        case 3: case 4: case 5: case 6: case 7: VertRef(); F(); F(); F(); F(); break;   // UV / UV1-4
                        case 8: MatRef(); U8(); for (int k = 0; k < 28; k++) F(); break;   // material (op + 28 floats)
                        case 9: MorphRef(); F(); break;                                   // flip (2.1)
                        case 10: RbRef(); U8(); V3(); V3(); break;                        // impulse (2.1)
                        default: throw new Exception("unknown morph type " + type);
                    }
                }
            }
        }

        private void ParseDisplayFrames()
        {
            int n = I32();
            for (int i = 0; i < n; i++)
            {
                Text(); Text();   // name
                U8();             // special-frame flag
                int ec = I32();
                for (int e = 0; e < ec; e++) { int t = U8(); if (t == 0) BoneRef(); else MorphRef(); }
            }
        }

        // Rigid bodies: stored in full so MmdMagicaCloth can convert the author's collision shapes (kinematic bodies →
        // colliders) and cloth firmness (dynamic body damping/size). Also builds the dynamic-bone (mode 1/2) set.
        private void ParseRigidBodies()
        {
            int n = I32();
            RigidBodies = new List<RigidBody>(Mathf.Max(n, 0));
            for (int i = 0; i < n; i++)
            {
                var rb = new RigidBody();
                rb.Name = Text(); Text();  // JP name (kept) + EN name (skip)
                rb.Bone = BoneRef();
                rb.Group = U8();           // collision group 0..15
                rb.Mask = (ushort)U16();   // collision-enable mask (bit g ⇒ collides with group g)
                rb.Shape = U8();           // 0 sphere, 1 box, 2 capsule
                rb.Size = V3(); rb.Position = V3(); rb.Rotation = V3();
                rb.Mass = F(); rb.LinearDamp = F(); rb.AngularDamp = F(); F(); F();   // + restitution, friction
                rb.Mode = U8();            // 0 = follow bone (kinematic), 1 = physics, 2 = physics + bone alignment
                RigidBodies.Add(rb);
                if (rb.Mode != 0 && rb.Bone >= 0 && rb.Bone < Bones.Count) PhysicsBones.Add(rb.Bone);
            }
        }

        // Joints (6DOF springs): we capture only each DYNAMIC child body's rotation-limit tightness, mapped to its bone,
        // as the per-bone stiffness signal (tight limit = author held that bone rigid). Assumes the PMX 2.0 spring-6DOF
        // layout for every joint (what PmxEditor writes); wrapped in the physics-section try/catch upstream.
        private void ParseJoints()
        {
            int n = I32();
            for (int i = 0; i < n; i++)
            {
                Text(); Text();            // names
                U8();                      // type (2.0 = spring 6DOF)
                int rbA = RbRef(); int rbB = RbRef();
                V3(); V3();                // position, rotation
                V3(); V3();                // position limit lower / upper
                Vector3 rLo = V3(), rHi = V3();   // rotation limit lower / upper (radians)
                V3();                          // position spring (unused)
                Vector3 rSpr = V3();           // rotation spring = the "return to shape" stiffness (Bullet setStiffness)
                if (rbB < 0 || rbB >= RigidBodies.Count) continue;
                var child = RigidBodies[rbB];      // rbB = the constrained (child) body; its bone gets this firmness
                if (child.Mode == 0 || child.Bone < 0 || child.Bone >= Bones.Count) continue;
                float range = (Mathf.Abs(rHi.x - rLo.x) + Mathf.Abs(rHi.y - rLo.y) + Mathf.Abs(rHi.z - rLo.z)) / 3f;
                BoneJointLimit[child.Bone] = BoneJointLimit.TryGetValue(child.Bone, out float prev) ? Mathf.Min(prev, range) : range;
                float spring = (Mathf.Abs(rSpr.x) + Mathf.Abs(rSpr.y) + Mathf.Abs(rSpr.z)) / 3f;
                BoneJointSpring[child.Bone] = BoneJointSpring.TryGetValue(child.Bone, out float psp) ? Mathf.Max(psp, spring) : spring;
            }
        }

        // ---- primitive readers ----
        private byte U8() => _d[_pos++];
        private int U16() { int v = _d[_pos] | (_d[_pos + 1] << 8); _pos += 2; return v; }
        private int I32() { int v = BitConverter.ToInt32(_d, _pos); _pos += 4; return v; }
        private float F() { float v = BitConverter.ToSingle(_d, _pos); _pos += 4; return v; }
        private Vector2 V2() { return new Vector2(F(), F()); }
        private Vector3 V3() { return new Vector3(F(), F(), F()); }

        private string Text()
        {
            int n = I32();
            if (n <= 0) return "";
            string s = _enc == 0 ? Encoding.Unicode.GetString(_d, _pos, n)   // UTF-16LE
                                 : Encoding.UTF8.GetString(_d, _pos, n);
            _pos += n;
            return s;
        }

        // Signed index of the given byte size (-1 = "none"): sbyte / short / int.
        private int SignedIdx(int size)
        {
            switch (size)
            {
                case 1: return (sbyte)_d[_pos++];
                case 2: { short v = (short)U16(); return v; }
                default: return I32();
            }
        }
        // Unsigned vertex index of the given byte size (faces): byte / ushort / int.
        private int UnsignedIdx(int size)
        {
            switch (size)
            {
                case 1: return _d[_pos++];
                case 2: return U16();
                default: return I32();
            }
        }
        private int BoneRef() => SignedIdx(_bIdx);
        private int TexRef() => SignedIdx(_tIdx);
        private int VertRef() => UnsignedIdx(_vIdx);
        private int MorphRef() => SignedIdx(_mfIdx);
        private int MatRef() => SignedIdx(_mIdx);
        private int RbRef() => SignedIdx(_rIdx);
    }
}
