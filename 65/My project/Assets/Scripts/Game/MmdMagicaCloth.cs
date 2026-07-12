using System.Collections.Generic;
using UnityEngine;
using MagicaCloth2;

namespace Sdo.Game
{
    /// <summary>
    /// Drives the MMD model's physics bones (hair / skirt / tie) with <b>Magica Cloth 2</b> (a DOTS cloth solver).
    /// The physics is CONVERTED FROM THE MODEL'S OWN AUTHORED DATA (<see cref="PmxLoader"/> rigid bodies + joints)
    /// rather than hand-guessed:
    /// <list type="bullet">
    /// <item>KINEMATIC rigid bodies (mode 0) on the body bones are the author's exact collision shapes → Magica
    /// colliders 1:1 (sizes are raw PMX units; the single unitScale on _mmdRoot scales them via lossyScale).</item>
    /// <item>The author's COLLISION GROUPS/MASKS decide which colliders each part actually touches — e.g. the skirt is
    /// set NOT to touch the giant hip "flare" capsule and NOT the arms/fingers; the hair DOES touch the arms. Ignoring
    /// this (colliding every part with all bodies) made the back of the skirt ride up over that big hip capsule.</item>
    /// <item>DYNAMIC rigid bodies (mode 1/2) + their joints give each part its firmness: joint rotation-LIMIT tightness
    /// → angle-restoration stiffness (twintails' locked ≈0 limits ⇒ near-rigid), angular damping → air-damping, body
    /// radius → particle thickness.</item>
    /// </list>
    /// Collision uses EDGE mode (segments between particles, not just points) so a fast leg can't slip between skirt
    /// particles during big dance moves; the global simulation frequency is raised for the same reason. The tie is the
    /// one intentional deviation: the user wants it to hang freely, so its angle restoration is off. Live-tunable
    /// (gravity / stiffness / collider radius) via <see cref="Tune"/>.
    /// </summary>
    public sealed class MmdMagicaCloth
    {
        private readonly List<MagicaCloth> _cloths = new List<MagicaCloth>();
        private readonly List<float> _baseGrav = new List<float>();    // per-cloth base gravity (× user gravMul)
        private readonly List<float> _baseStiff = new List<float>();   // per-cloth base angle-restoration stiffness
        private readonly List<ColliderComponent> _colliders = new List<ColliderComponent>();
        private readonly List<Vector3> _colBaseSize = new List<Vector3>();   // (radius, radius|0, length|0); sphere = (r,0,0)

        public bool Any => _cloths.Count > 0;
        public int ClothCount => _cloths.Count;
        public int ColliderCount => _colliders.Count;

        // part groups (index into the per-group arrays below)
        private const int BANG = 0, HAIR = 1, SKIRT = 2, TIE = 3;

        // A collider + the authored collision group/mask of the body it came from (for per-part filtering).
        private struct ColRec { public ColliderComponent Col; public byte Group; public ushort Mask; }

        public static MmdMagicaCloth Setup(GameObject host, Transform[] bone, int[] parent, PmxLoader pmx, float unitScale)
        {
            if (pmx?.PhysicsBones == null || pmx.PhysicsBones.Count == 0) return null;
            var m = new MmdMagicaCloth();
            try { m.Build(host, bone, parent, pmx, unitScale); }
            catch (System.Exception e) { Debug.LogWarning("[mmd] Magica Cloth setup failed: " + e.Message + "\n" + e.StackTrace); return null; }
            return m.Any ? m : null;
        }

        // Which part a bone belongs to, from its DYNAMIC rigid body's label (the attached bone is usually generically
        // named, but the rigid body is labelled Bang / Twintail / Dress / Tie / …). Falls back to the bone name.
        private static int GroupOf(string label)
        {
            string nm = label ?? "";
            if (nm.Contains("Bang") || nm.Contains("前髪")) return BANG;
            if (nm.Contains("Dress") || nm.Contains("Skirt") || nm.Contains("スカート") || nm.Contains("裙")) return SKIRT;
            if (nm.Contains("Tie") || nm.Contains("ネクタイ") || nm.Contains("領帯") || nm.Contains("领带")) return TIE;
            return HAIR;   // twintails / hairlines / breast / misc
        }

        // Two rigid bodies collide iff each has the OTHER's group bit set in its collision-enable mask.
        private static bool Collide(byte gA, ushort mA, byte gB, ushort mB)
            => ((mA >> gB) & 1) == 1 && ((mB >> gA) & 1) == 1;

        // Per-group accumulator of the authored firmness data (means computed at build) + the distinct collision
        // filters (group|mask) of the part's dynamic bodies, used to select which colliders this part touches.
        private sealed class GroupAccum
        {
            public readonly List<Transform> Roots = new List<Transform>();
            public readonly HashSet<int> Filters = new HashSet<int>();   // (group << 16) | mask
            public float LimSum, AngSum, LinSum, RadSum;
            public int LimN, BodyN;
        }

        private void Build(GameObject host, Transform[] bone, int[] parent, PmxLoader pmx, float unitScale)
        {
            int bc = pmx.Bones.Count;
            float u = Mathf.Max(unitScale, 1f);
            // Gravity is physical (uniform); per-part firmness (below) decides how much each part resists it.
            float baseGravity = 10f * u;

            // ---- body colliders from the model's KINEMATIC rigid bodies (exact authored shapes + their groups) ----
            var colRecs = new List<ColRec>();
            BuildRigidBodyColliders(pmx, bone, colRecs);
            List<ColliderComponent> allCols = null;
            if (colRecs.Count == 0)   // model has no rigid-body data → hand-placed fallback, no group filtering
            {
                allCols = new List<ColliderComponent>();
                BuildFallbackColliders(pmx, bone, allCols, unitScale);
            }

            // ---- group the dynamic bones + accumulate authored firmness (joint limits, damping, radius) + filters ----
            var body = new Dictionary<int, PmxLoader.RigidBody>();
            foreach (var rb in pmx.RigidBodies)
                if (rb.Mode != 0 && rb.Bone >= 0 && !body.ContainsKey(rb.Bone)) body[rb.Bone] = rb;

            var g = new GroupAccum[4];
            for (int i = 0; i < 4; i++) g[i] = new GroupAccum();
            foreach (int i in pmx.PhysicsBones)
            {
                if (i < 0 || i >= bc || bone[i] == null) continue;
                body.TryGetValue(i, out var rb);
                var acc = g[GroupOf(rb != null ? rb.Name : (pmx.Bones[i].NameEn + pmx.Bones[i].NameJp))];
                if (rb != null)
                {
                    acc.AngSum += rb.AngularDamp; acc.LinSum += rb.LinearDamp; acc.RadSum += Mathf.Max(rb.Size.x, 1e-3f); acc.BodyN++;
                    acc.Filters.Add((rb.Group << 16) | rb.Mask);
                }
                if (pmx.BoneJointLimit.TryGetValue(i, out float lim)) { acc.LimSum += lim; acc.LimN++; }
                int p = parent[i];
                if (!(p >= 0 && pmx.PhysicsBones.Contains(p))) acc.Roots.Add(bone[i]);   // chain root (parent not physics)
            }

            string[] names = { "MmdBangCloth", "MmdHairCloth", "MmdSkirtCloth", "MmdTieCloth" };
            float[] gravMul = { 0.35f, 0.4f, 1.0f, 1.25f };   // head parts held (little gravity); skirt/tie hang (more)
            for (int part = 0; part < 4; part++)
            {
                var acc = g[part];
                if (acc.Roots.Count == 0) continue;
                float limMean = acc.LimN > 0 ? acc.LimSum / acc.LimN : 0.2f;
                float angMean = acc.BodyN > 0 ? acc.AngSum / acc.BodyN : 1f;
                float radMean = acc.BodyN > 0 ? acc.RadSum / acc.BodyN : 0.2f;
                // firmness ← authored joint-limit tightness: tight limits (limMean→0, e.g. twintails) hold the styled
                // shape (near-rigid); loose limits (limMean→0.45, e.g. bangs) swing.
                float stiff = Mathf.Lerp(0.85f, 0.05f, Mathf.Clamp01(limMean / 0.45f));
                float damping = Mathf.Lerp(0.05f, 0.12f, Mathf.InverseLerp(0.99f, 2.0f, angMean));
                float worldInertia = 1f, depthInertia = 0f;
                // Per-part FEEL, hand-tuned from user testing:
                //  - HAIR: the long twintails must move as ONE COHERENT chain that WHIPS root→tip on a spin, not each
                //    joint writhing on its own. depthInertia ANCHORS the root to the head (weak inertia at the root)
                //    while the tip keeps full inertia → a head turn propagates down and flings the tips out. A moderate
                //    (not floppy) angle stiffness holds the chain's shape so it swings as a unit; damping settles it.
                //  - BANG: firm AND low world-inertia so they just track the head with almost no sway.
                //  - TIE: hangs free (stiff 0) but damped so it doesn't bounce forever.
                if (part == HAIR) { stiff = 0.90f; damping = 0.14f; depthInertia = 0.85f; }   // very firm chain + strong root-anchor → whips as one stiff unit
                else if (part == BANG) { stiff = 0.90f; worldInertia = 0.2f; }
                else if (part == TIE) { stiff = 0f; damping = 0.20f; depthInertia = 0.5f; }

                // colliders this part actually touches (authored groups/masks); fallback = all colliders.
                var partCols = allCols != null ? allCols : SelectColliders(acc, colRecs);
                // A SKIRT is a closed ring of panels: mesh-connect its strands so a fast leg can't slip BETWEEN two
                // panels (independent Line strands leave gaps that Edge collision has no edge to catch → poke-through on
                // big moves). Hair/bangs/tie stay Line (they ARE independent strands).
                var conn = part == SKIRT ? RenderSetupData.BoneConnectionMode.AutomaticMesh
                                         : RenderSetupData.BoneConnectionMode.Line;
                // Magica's particle radius is applied in WORLD space (× scaleRatio, which is 1 here — NOT × the cloth's
                // lossyScale like colliders), so convert the authored raw radius to world by × unitScale, else the skirt
                // is ~unitScale× too thin to catch limbs (clipping).
                float radWorld = radMean * unitScale;
                SdoLog.Note("mmd", $"  cloth[{names[part]}] roots={acc.Roots.Count} cols={partCols.Count} conn={conn} " +
                                   $"-> stiff={stiff:F2} damp={damping:F2} wInertia={worldInertia:F2} depthI={depthInertia:F2} radiusW={radWorld:F2}");
                BuildCloth(host, names[part], acc.Roots, partCols, baseGravity * gravMul[part], stiff, damping, radWorld, u, conn, part == SKIRT, worldInertia, depthInertia);
            }

            // Fast dance = fast bones; raise the global solver rate (default 90) to its hard cap (150) so a limb moves
            // less per substep → fewer chances to tunnel through the cloth. Collider collision has no per-cloth
            // iteration knob, so substeps are the only anti-tunnelling lever. Global; guarded (no-op outside play mode).
            try { MagicaManager.SetSimulationFrequency(150); } catch { /* older API / edit mode */ }
        }

        // Colliders in colRecs that the part's dynamic bodies are authored to collide with (group/mask filter).
        private static List<ColliderComponent> SelectColliders(GroupAccum acc, List<ColRec> colRecs)
        {
            var outl = new List<ColliderComponent>();
            foreach (var cr in colRecs)
            {
                bool hit = false;
                foreach (int fp in acc.Filters)
                    if (Collide((byte)(fp >> 16), (ushort)(fp & 0xFFFF), cr.Group, cr.Mask)) { hit = true; break; }
                if (hit) outl.Add(cr.Col);
            }
            return outl;
        }

        // Convert every KINEMATIC (mode 0) rigid body on a NON-physics body bone into a Magica collider that follows
        // that bone, carrying its authored collision group/mask. Fingers (指) are skipped (small + many). Offsets/sizes
        // are raw PMX units — the collider inherits _mmdRoot's unitScale (Magica uses lossyScale).
        private void BuildRigidBodyColliders(PmxLoader pmx, Transform[] bone, List<ColRec> colRecs)
        {
            if (pmx.RigidBodies == null) return;
            foreach (var rb in pmx.RigidBodies)
            {
                if (rb.Mode != 0 || rb.Bone < 0 || rb.Bone >= bone.Length || bone[rb.Bone] == null) continue;
                if (pmx.PhysicsBones.Contains(rb.Bone)) continue;                 // an anchor on a cloth bone, not a body collider
                string bn = pmx.Bones[rb.Bone].NameJp ?? "";
                if (bn.Contains("指")) continue;                                  // fingers: tiny, hair never reaches them

                var goCol = new GameObject("col_" + bn);
                goCol.transform.SetParent(bone[rb.Bone], false);
                goCol.transform.localPosition = rb.Position - pmx.Bones[rb.Bone].Position;   // raw model-space offset
                goCol.transform.localRotation = Quaternion.Euler(rb.Rotation * Mathf.Rad2Deg);
                goCol.transform.localScale = Vector3.one;

                ColliderComponent c;
                if (rb.Shape == 2)   // capsule: MMD (radius, height) → Magica (startR, endR, length) along local Y
                {
                    // MMD Size.y is the CYLINDER length (sphere-centre to sphere-centre, Bullet btCapsuleShape); Magica's
                    // `length` is the TOTAL tip-to-tip (its sphere centres sit at ±(length/2 − r), tips at ±length/2).
                    // So total = MMD height + 2·radius — without the +2r every leg/torso capsule falls 2·radius short of
                    // the knee/hip and the skirt clips there.
                    float r = Mathf.Max(rb.Size.x, 1e-3f), len = rb.Size.y + 2f * r;
                    var cap = goCol.AddComponent<MagicaCapsuleCollider>();
                    cap.direction = MagicaCapsuleCollider.Direction.Y;
                    cap.alignedOnCenter = true;
                    cap.SetSize(r, r, len);
                    c = cap; _colBaseSize.Add(new Vector3(r, r, len));
                }
                else                 // sphere (0) — and box (1) approximated as a sphere of its largest half-extent
                {
                    float r = rb.Shape == 1 ? Mathf.Max(rb.Size.x, Mathf.Max(rb.Size.y, rb.Size.z)) : rb.Size.x;
                    r = Mathf.Max(r, 1e-3f);
                    var sp = goCol.AddComponent<MagicaSphereCollider>();
                    sp.SetSize(r);
                    c = sp; _colBaseSize.Add(new Vector3(r, 0f, 0f));
                }
                _colliders.Add(c);
                colRecs.Add(new ColRec { Col = c, Group = rb.Group, Mask = rb.Mask });
            }
        }

        // Fallback when the model ships no rigid-body data: the old hand-placed torso/hip/skull spheres + leg capsules.
        private void BuildFallbackColliders(PmxLoader pmx, Transform[] bone, List<ColliderComponent> cols, float unitScale)
        {
            float hipHalf = (MmdBonePos(pmx, "左足") - MmdBonePos(pmx, "右足")).magnitude * 0.5f * unitScale;
            if (hipHalf < 1e-3f) hipHalf = (MmdBonePos(pmx, "左腕") - MmdBonePos(pmx, "右腕")).magnitude * 0.3f * unitScale;
            if (hipHalf < 1e-3f) hipHalf = unitScale;

            void Sphere(string n, float f)
            {
                int i = Find(pmx, n); if (i < 0 || bone[i] == null) return;
                float r = hipHalf * f;
                var c = bone[i].gameObject.AddComponent<MagicaSphereCollider>();
                c.SetSize(r);
                cols.Add(c); _colliders.Add(c); _colBaseSize.Add(new Vector3(r, 0f, 0f));
            }
            void Capsule(string n0, string n1, float f)
            {
                int i0 = Find(pmx, n0), i1 = Find(pmx, n1); if (i0 < 0 || i1 < 0 || bone[i0] == null) return;
                float r = hipHalf * f, len = (pmx.Bones[i1].Position - pmx.Bones[i0].Position).magnitude * unitScale * 2f;
                var c = bone[i0].gameObject.AddComponent<MagicaCapsuleCollider>();
                c.direction = MagicaCapsuleCollider.Direction.Y;
                c.SetSize(r, r, len);
                cols.Add(c); _colliders.Add(c); _colBaseSize.Add(new Vector3(r, r, len));
            }
            Sphere("上半身2", 0.30f); Sphere("上半身", 0.32f); Sphere("下半身", 0.42f); Sphere("頭", 0.50f);
            Capsule("左足", "左ひざ", 0.34f); Capsule("左ひざ", "左足首", 0.26f);
            Capsule("右足", "右ひざ", 0.34f); Capsule("右ひざ", "右足首", 0.26f);
        }

        private void BuildCloth(GameObject host, string name, List<Transform> roots, List<ColliderComponent> cols,
                                float gravity, float angleStiff, float damping, float particleRadius, float unitScale,
                                RenderSetupData.BoneConnectionMode connectionMode, bool pinRoot, float worldInertia, float depthInertia)
        {
            if (roots.Count == 0) return;
            var go = new GameObject(name);
            go.transform.SetParent(host.transform, false);
            var cloth = go.AddComponent<MagicaCloth>();
            var sd = cloth.SerializeData;
            sd.clothType = ClothProcess.ClothType.BoneCloth;
            sd.connectionMode = connectionMode;          // Line strands vs AutomaticMesh sheet (skirt) — build-time only
            sd.updateMode = ClothUpdateMode.Normal;      // we pose bones in a manual LateUpdate (no Animator to link to)
            foreach (var r in roots) sd.rootBones.Add(r);
            sd.gravity = gravity;
            sd.damping.SetValue(Mathf.Clamp01(damping));               // authored angular damping → air resistance
            // Particle thickness as a root→tip CURVE: THIN near the body (0.35×) so the fabric next to the waist/neck
            // doesn't get puffed up over the hip/shoulder colliders, FULL toward the tip where it must catch the legs.
            if (particleRadius > 1e-4f) sd.radius.SetValue(particleRadius, 0.35f, 1f);
            // hold chain lengths (shape) with distance restoration; ANGLE restoration = authored firmness (0 = hang free
            // for the tie — gravity + distance only, no pull back to the styled pose).
            sd.distanceConstraint.stiffness.SetValue(1f);
            sd.angleRestorationConstraint.useAngleRestoration = angleStiff > 0.001f;
            if (angleStiff > 0.001f) sd.angleRestorationConstraint.stiffness.SetValue(angleStiff);
            // Inertia: the cloth must PICK UP the body's motion (spins especially). Keep full world inertia but only
            // LIGHTLY smooth it (0.3, near Magica's 0.4 default) — the earlier 0.9 over-smoothed it into "floaty, won't
            // react" hair. Raise the world-ROTATION cap far above the default 720°/s so a fast dance spin fully carries
            // the hair/tie out instead of being clamped ("速度帶不上來").
            sd.inertiaConstraint.worldInertia = worldInertia;   // per-part: bangs low (barely react), hair/skirt full
            sd.inertiaConstraint.depthInertia = depthInertia;   // anchor the root, free the tip → chain whips as a whole
            sd.inertiaConstraint.movementInertiaSmoothing = 0.3f;
            float uu = Mathf.Max(unitScale, 1f);
            sd.inertiaConstraint.movementSpeedLimit = new CheckSliderSerializeData(true, 5f * uu * 3f);
            sd.inertiaConstraint.rotationSpeedLimit = new CheckSliderSerializeData(true, 2000f);   // was default 720°/s
            sd.inertiaConstraint.particleSpeedLimit = new CheckSliderSerializeData(true, 4f * uu * 10f);
            // Keep each particle near where the ANIMATION drapes it — pinned tight at the root (0.12×), loose at the hem
            // (1.0×). Stops the skirt from riding up over the hips (near-body puff) and, on a body spin, from wrapping up
            // onto the body and not falling back (it can never travel more than maxDistance from its drape). Skirt only —
            // hair/tie must stay free to swing.
            if (pinRoot)
            {
                sd.motionConstraint.useMaxDistance = true;
                sd.motionConstraint.maxDistance.SetValue(4f, 0.12f, 1f);   // world units (clamped 0..5); depth²-biased
            }
            // EDGE collision (segments between particles) so a fast leg/arm can't slip THROUGH the cloth; a little
            // friction so a rising leg drags the panel along instead of shearing past — but LOW (0.15) so the skirt
            // slides back DOWN after a spin instead of sticking wrapped up on the body.
            sd.colliderCollisionConstraint.mode = ColliderCollisionConstraint.Mode.Edge;
            sd.colliderCollisionConstraint.friction = 0.15f;
            sd.colliderCollisionConstraint.colliderList.AddRange(cols);
            cloth.BuildAndRun();
            _cloths.Add(cloth); _baseGrav.Add(gravity); _baseStiff.Add(angleStiff);
        }

        public void SetEnabled(bool on) { foreach (var c in _cloths) if (c != null) c.enabled = on; }

        /// <summary>Live tune (matches the SDO debug panel). <paramref name="stiffMul"/> scales each part's base
        /// stiffness (clamped to 1), <paramref name="gravMul"/> scales gravity.</summary>
        public void Tune(float gravMul, float stiffMul)
        {
            for (int i = 0; i < _cloths.Count; i++)
            {
                var c = _cloths[i]; if (c == null) continue;
                c.SerializeData.gravity = _baseGrav[i] * gravMul;
                if (_baseStiff[i] > 0.001f)   // keep the free-hanging tie free (don't turn restoration back on)
                    c.SerializeData.angleRestorationConstraint.stiffness.SetValue(Mathf.Clamp01(_baseStiff[i] * stiffMul));
                c.SetParameterChange();
            }
        }

        public void SetColliderRadius(float mul)
        {
            for (int i = 0; i < _colliders.Count; i++)
            {
                var s = _colBaseSize[i];
                if (_colliders[i] is MagicaSphereCollider sp) sp.SetSize(s.x * mul);
                else if (_colliders[i] is MagicaCapsuleCollider cap) cap.SetSize(s.x * mul, s.y * mul, s.z);   // scale radius, keep length
            }
            foreach (var c in _cloths) if (c != null) c.SetParameterChange();
        }

        private static Vector3 MmdBonePos(PmxLoader pmx, string nameJp) { foreach (var b in pmx.Bones) if (b.NameJp == nameJp) return b.Position; return Vector3.zero; }
        private static int Find(PmxLoader pmx, string nameJp) { for (int i = 0; i < pmx.Bones.Count; i++) if (pmx.Bones[i].NameJp == nameJp) return i; return -1; }
    }
}
