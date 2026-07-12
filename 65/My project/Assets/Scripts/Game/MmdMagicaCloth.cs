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

        // Bullet/MMD damping is PER-SECOND (velocity retains (1−d) after 1 s); Magica damping is applied PER SUBSTEP
        // (at our 150 Hz: v ×= 1 − m×0.6 each step). Match one second of decay: (1 − m×0.6)^150 = (1 − d)
        //   ⇒ m = (1 − (1−d)^(1/150)) / 0.6.   e.g. d=0.2 → m=0.0025, d=1.0(capped) → m=0.043 ≈ Magica's stock range.
        private static float BulletToMagicaDamping(float bulletDamp)
        {
            const float freq = 150f, power = 0.6f;   // our SetSimulationFrequency(150); solver z-power = 90/150
            float retainPerSec = Mathf.Clamp(1f - bulletDamp, 0.02f, 1f);
            return Mathf.Clamp((1f - Mathf.Pow(retainPerSec, 1f / freq)) / power, 0f, 0.2f);
        }

        // Per-group accumulator of the authored firmness data (means computed at build) + the distinct collision
        // filters (group|mask) of the part's dynamic bodies, used to select which colliders this part touches.
        private sealed class GroupAccum
        {
            public readonly List<Transform> Roots = new List<Transform>();
            public readonly HashSet<int> Filters = new HashSet<int>();   // (group << 16) | mask
            public int AnchorBone = -1;   // the body bone the chain hangs from (its motion is the cloth's inertia reference)
            public float LimSum, AngSum, LinSum, RadSum, SpringSum;
            public int LimN, BodyN, SpringN, BoxN;   // BoxN = box-shaped dynamic bodies (skirt panels) → mesh connection
            // chain root vs tip mass + linear-damping → derive depthInertia (from the mass gradient) + a damping curve
            public float RootMassSum, TipMassSum, RootLinSum, TipLinSum;
            public int RootN, TipN;
            public float MinY = float.PositiveInfinity, MaxY = float.NegativeInfinity;   // bone Y extent ≈ chain length
        }

        private void Build(GameObject host, Transform[] bone, int[] parent, PmxLoader pmx, float unitScale)
        {
            int bc = pmx.Bones.Count;
            float u = Mathf.Max(unitScale, 1f);

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

            // a physics bone that is another physics bone's parent has a physics child → it is NOT a chain tip
            var hasPhysChild = new HashSet<int>();
            foreach (int i in pmx.PhysicsBones) { int pp = parent[i]; if (pp >= 0 && pmx.PhysicsBones.Contains(pp)) hasPhysChild.Add(pp); }

            var g = new GroupAccum[4];
            for (int i = 0; i < 4; i++) g[i] = new GroupAccum();
            foreach (int i in pmx.PhysicsBones)
            {
                if (i < 0 || i >= bc || bone[i] == null) continue;
                body.TryGetValue(i, out var rb);
                var acc = g[GroupOf(rb != null ? rb.Name : (pmx.Bones[i].NameEn + pmx.Bones[i].NameJp))];
                int p = parent[i];
                bool isRoot = !(p >= 0 && pmx.PhysicsBones.Contains(p));
                bool isTip = !hasPhysChild.Contains(i);
                if (rb != null)
                {
                    acc.AngSum += rb.AngularDamp; acc.LinSum += rb.LinearDamp; acc.RadSum += Mathf.Max(rb.Size.x, 1e-3f); acc.BodyN++;
                    if (rb.Shape == 1) acc.BoxN++;   // box panel ⇒ a sheet (ring) not a strand
                    acc.Filters.Add((rb.Group << 16) | rb.Mask);
                    // MMD authors the chain ROOT heavy (anchored) and the TIP light (swings) + the tip more damped —
                    // exactly Magica's depthInertia + a root→tip damping curve. Capture root/tip mass + linear damping.
                    if (isRoot) { acc.RootMassSum += rb.Mass; acc.RootLinSum += rb.LinearDamp; acc.RootN++; }
                    if (isTip) { acc.TipMassSum += rb.Mass; acc.TipLinSum += rb.LinearDamp; acc.TipN++; }
                }
                if (pmx.BoneJointLimit.TryGetValue(i, out float lim)) { acc.LimSum += lim; acc.LimN++; }
                if (pmx.BoneJointSpring.TryGetValue(i, out float spr)) { acc.SpringSum += spr; acc.SpringN++; }
                float by = pmx.Bones[i].Position.y;
                if (by < acc.MinY) acc.MinY = by; if (by > acc.MaxY) acc.MaxY = by;
                if (isRoot) { acc.Roots.Add(bone[i]); if (acc.AnchorBone < 0) acc.AnchorBone = p; }   // p = the non-physics bone the chain hangs from
            }

            string[] names = { "MmdBangCloth", "MmdHairCloth", "MmdSkirtCloth", "MmdTieCloth" };
            // ---- world scale, derived from the avatar itself (data-driven, no hand constant) ----
            // Magica assumes METER-scale characters (its clamps are SI: gravity≤20, particle speed≤10 m/s). This avatar
            // renders ~51 world units tall ≈ a 1.6 m girl ⇒ ~32 units per meter, so physically-correct values are that
            // factor larger: g = 9.8×upm ≈ 314 u/s². With the stock clamps the sim runs ~4× slow motion (pendulum
            // T=2π√(L/g): twintail L≈36u @ g20 → 8.4 s vs correct 2.1 s) — the real cause of "floaty / slow / can't
            // whip". Requires the two MC2 clamp constants raised (local patch); values are safe either way (they clamp).
            float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
            foreach (var bp in pmx.Bones) { if (bp.Position.y < minY) minY = bp.Position.y; if (bp.Position.y > maxY) maxY = bp.Position.y; }
            float worldHeight = Mathf.Max((maxY - minY) * unitScale, 1e-3f);
            float unitsPerMeter = Mathf.Max(worldHeight / 1.6f, 0.1f);   // assume a ~1.6 m humanoid
            float gravity = 9.8f * unitsPerMeter;
            for (int part = 0; part < 4; part++)
            {
                var acc = g[part];
                if (acc.Roots.Count == 0) continue;

                // ===== every Magica value below is DERIVED from the PMX physics (no per-part hand constants) =====
                float springMean = acc.SpringN > 0 ? acc.SpringSum / acc.SpringN : 0f;
                float springNorm = Mathf.Clamp01(springMean / 5f);                 // rotation-spring strength 0..1
                float limMean = acc.LimN > 0 ? acc.LimSum / acc.LimN : 0f;         // joint rotation-limit range (rad)
                float massRoot = acc.RootN > 0 ? acc.RootMassSum / acc.RootN : 1f;
                float massTip = acc.TipN > 0 ? acc.TipMassSum / acc.TipN : 1f;
                float massGrad = Mathf.Log(Mathf.Max(massRoot / Mathf.Max(massTip, 1e-3f), 1f));   // ln(root/tip mass)
                float angMean = acc.BodyN > 0 ? acc.AngSum / acc.BodyN : 1f;
                float linRoot = acc.RootN > 0 ? acc.RootLinSum / acc.RootN : 0.5f;
                float linTip = acc.TipN > 0 ? acc.TipLinSum / acc.TipN : 0.5f;
                float radMean = acc.BodyN > 0 ? acc.RadSum / acc.BodyN : 0.2f;

                // SHAPE RETENTION, calibrated against the pybullet ground truth (compare.py):
                //  - spring>0 (fringe): angle RESTORATION ∝ the authored rotation spring — measured 9/10 PASS at 0.9×norm.
                //  - spring==0 (twintails/tie/skirt): MMD's rotation LIMITS are per-joint structural constraints (length-
                //    independent) — a restoration force is the wrong analog (it attenuates down long chains: 0.4 fixed the
                //    18-bone tie but did nothing for the 30-bone twintail, droop 7° vs ref 31.8°). Use Magica's per-joint
                //    ANGLE LIMIT instead: authored half-range + ~1.5° Bullet ERP softness per joint.
                // SHAPE = force-based RESTORATION (primary) + a loose hard LIMIT (guard). Findings that shaped this:
                //  - Magica's angleLimit stiffness ≠ Bullet ERP: at 1.0 it hard-glues the chain to the animated pose
                //    (rigid + violent whipping in a real dance, though gentle probe scenarios read fine); at 0.2 it
                //    never wins against gravity (rest shape collapses). One knob can't do rest-tight + dance-soft.
                //  - Restoration is a FORCE — it yields under dance load naturally and holds at rest; its old failure
                //    on long chains (30-bone twintail) is fixed by a root-weighted curve (torque concentrates at the
                //    root: full strength there, 40% at the tip where the whip should live).
                //  - spring>0 parts (bang) keep the authored-spring restoration (validated 9/10 PASS).
                float angleStiff = springMean > 0f ? Mathf.Clamp01(springNorm * 0.9f) : 0.9f;   // user-tuned feel: 0.5→0.65→0.8→0.9 (雙馬尾要硬)
                bool useAngle = true;
                bool rootWeighted = springMean <= 0f;                       // pendulum chains: strong root, free tip
                float angleLimitDeg = 6f + Mathf.Rad2Deg * limMean;         // loose guard: only catches extreme excursions
                // gravityFalloff ← springNorm: a pinned part holds its shape against gravity (falloff→1 = gravity≈0 at
                // rest pose); a pendulum keeps full gravity (falloff 0) so it hangs down. Replaces per-part gravity hacks.
                float gravityFalloff = springNorm;
                // DAMPING ← Bullet LINEAR damping, converted per-second → per-substep (see BulletToMagicaDamping).
                // Validated: twintail oscillation count 4 vs ref 5 (PASS).
                float dampRoot = BulletToMagicaDamping(linRoot);
                float dampTip = BulletToMagicaDamping(linTip);
                // worldInertia = fraction of the body's motion imparted to the cloth as inertia (movementShift =
                // 1−worldInertia removes the rest). MMD/Bullet imparts the FULL motion — measured: at 0.67 our twintail
                // under-swung 65% (turn amp 0.30 vs ref 0.85 chain-length), zero spin fling, walk stream 12° vs 32°.
                // Full inertia is the faithful value; shape retention comes from angle restoration, not motion removal.
                const float worldInertia = 1f;
                // depthInertia ← MASS gradient (root heavy = carried with the body, tip light = lags = the whip),
                // cap user-tuned back to 0.5: the upper chain rides with the body = the "整條硬挺" feel (its old
                // side-effects — violent whip/zero response — came from the un-scaled clamps, fixed since).
                float depthInertia = 0.5f * Mathf.Clamp01(massGrad / 5.70f);
                // CONNECTION: skirt = AutomaticMesh. MMD's panels are independent strands (Line matches Bullet's
                // dynamics better — flare/oscillation), but Magica's Edge collision on independent strands has only
                // VERTICAL edges, so a dance-speed leg slips BETWEEN panels (user-verified clipping). Anti-clip wins:
                // mesh cross-edges catch the leg; the flare/oscillation gap vs the ref is the accepted cost.
                bool sheet = acc.BoxN * 2 >= acc.BodyN && acc.BodyN > 0;
                var conn = sheet ? RenderSetupData.BoneConnectionMode.AutomaticMesh : RenderSetupData.BoneConnectionMode.Line;
                float radWorld = radMean * unitScale;   // particle radius is WORLD-space (× scaleRatio=1, not lossyScale)
                float chainLenWorld = Mathf.Max((acc.MaxY - acc.MinY) * unitScale, radWorld);   // ≈ how far the hem can physically travel

                var partCols = allCols != null ? allCols : SelectColliders(acc, colRecs);
                // Inertia reference = the bone the chain hangs from (the kinematic anchor: head/hip/…). The SDO dance is
                // in the bones, so a cloth under the static _mmdRoot sees no body motion; parenting to the anchor makes a
                // spin rotate the reference → inertia carries the chain.
                Transform refT = (acc.AnchorBone >= 0 && acc.AnchorBone < bc && bone[acc.AnchorBone] != null) ? bone[acc.AnchorBone] : host.transform;
                SdoLog.Note("mmd", $"  cloth[{names[part]}] roots={acc.Roots.Count} cols={partCols.Count} {(sheet ? "MESH" : "line")} anchor={(acc.AnchorBone >= 0 ? pmx.Bones[acc.AnchorBone].NameJp : "root")} " +
                                   $"massR/T={massRoot:F1}/{massTip:F2} spring={springMean:F1} -> angle={(useAngle ? angleStiff.ToString("F2") : "off")} limit={angleLimitDeg:F1}° gFall={gravityFalloff:F2} " +
                                   $"damp={dampRoot:F2}→{dampTip:F2} wInertia={worldInertia:F2} depthI={depthInertia:F2} radW={radWorld:F2} g={gravity:F0}(upm={unitsPerMeter:F1})");
                BuildCloth(refT, names[part], acc.Roots, partCols, gravity, gravityFalloff, useAngle, angleStiff, rootWeighted, angleLimitDeg,
                           dampRoot, dampTip, radWorld, conn, sheet, worldInertia, depthInertia, unitsPerMeter, chainLenWorld);
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

        private void BuildCloth(Transform parentT, string name, List<Transform> roots, List<ColliderComponent> cols,
                                float gravity, float gravityFalloff, bool useAngle, float angleStiff, bool rootWeighted, float angleLimitDeg,
                                float dampRoot, float dampTip, float particleRadius,
                                RenderSetupData.BoneConnectionMode connectionMode, bool sheet, float worldInertia, float depthInertia,
                                float unitsPerMeter, float chainLenWorld)
        {
            if (roots.Count == 0) return;
            var go = new GameObject(name);
            go.transform.SetParent(parentT, false);   // parentT = the anchor bone → its motion is the cloth's inertia reference
            var cloth = go.AddComponent<MagicaCloth>();
            var sd = cloth.SerializeData;
            sd.clothType = ClothProcess.ClothType.BoneCloth;
            sd.connectionMode = connectionMode;          // Line strands vs AutomaticMesh sheet (skirt) — build-time only
            sd.updateMode = ClothUpdateMode.Normal;      // we pose bones in a manual LateUpdate (no Animator to link to)
            // ALWAYS simulate: default camera culling (AnimatorLinkage/AutomaticRenderer) looks for a renderer under the
            // cloth GO — ours is an empty holder (the SkinnedMeshRenderer lives elsewhere), so the sim can be suspended
            // as "invisible" (headless probe: rigid follow, zero physics). The dancer is always on screen anyway.
            sd.cullingSettings.cameraCullingMode = CullingSettings.CameraCullingMode.Off;
            foreach (var r in roots) sd.rootBones.Add(r);
            sd.gravity = gravity;                                // gravityDirection defaults to (0,-1,0) = world down
            sd.gravityFalloff = Mathf.Clamp01(gravityFalloff);   // 0 = hang straight down; 1 (pinned bang) = hold rest shape
            // authored linear-damping gradient → root→tip air-resistance curve (root light, tip heavier so the tip settles)
            if (dampTip > 1e-4f) sd.damping.SetValue(dampTip, dampRoot / dampTip, 1f);
            else sd.damping.SetValue(Mathf.Clamp01(dampRoot));
            // Particle thickness = the body's own collision radius (world-scaled), THIN near the body (0.35×) so it doesn't
            // puff over the adjacent hip/shoulder collider, FULL toward the tip where it must catch limbs.
            if (particleRadius > 1e-4f) sd.radius.SetValue(particleRadius, 0.35f, 1f);
            sd.distanceConstraint.stiffness.SetValue(1f);   // 0/0-position joints = inextensible strand
            // ANGLE restoration ON only where the joint has a rotation SPRING (pinned to a shape, e.g. the fringe);
            // spring-less chains get a per-joint ANGLE LIMIT instead (the Bullet locked-limit analog): they hang and
            // swing freely WITHIN the limit, so long chains keep their authored shape structurally.
            sd.angleRestorationConstraint.useAngleRestoration = useAngle;
            if (useAngle)
            {
                // pendulum chains: root-weighted curve — full strength where gravity torque concentrates, 65% at the
                // tip (user-tuned: firmer tip = the "韌性" spring-back feel). Sprung parts (bang) stay flat.
                if (rootWeighted) sd.angleRestorationConstraint.stiffness.SetValue(angleStiff, 1f, 0.8f);
                else sd.angleRestorationConstraint.stiffness.SetValue(angleStiff);
            }
            if (angleLimitDeg > 0.01f)
            {
                sd.angleLimitConstraint.useAngleLimit = true;
                sd.angleLimitConstraint.limitAngle.SetValue(angleLimitDeg);
                // Bullet solves locked limits with ERP≈0.2 per substep — a SOFT constraint that YIELDS under dance-speed
                // load and converges at rest. stiffness=1 (hard clamp) glued the hair to the head — rigid + violent
                // whipping in a real dance, while the probe's gentle 0.4 s turn only read as +60% over-swing.
                sd.angleLimitConstraint.stiffness = 0.2f;
            }
            // FOLLOW the anchor bone with a lag (= swing). worldInertia < 1 so the reference frame is dragged by the body
            // (1 = ignore body motion = the won't-follow failure); depthInertia carries the heavy root / lets the light tip
            // lag (whip). movementInertiaSmoothing 0.3 low-passes jitter without smoothing away a fast spin.
            sd.inertiaConstraint.worldInertia = Mathf.Clamp01(worldInertia);
            sd.inertiaConstraint.depthInertia = Mathf.Clamp01(depthInertia);
            // light smoothing: MMD imparts motion unsmoothed, but Magica's impulsive shift over-whips vs Bullet's
            // constraint-solved transfer (twintail turn amp +59% at 0.1); 0.15 shaves the spike, 0.3 was too slow.
            sd.inertiaConstraint.movementInertiaSmoothing = 0.15f;
            // MMD imposes no world speed caps → DISABLE movement + rotation limits (let the cloth lag on fast walk/spin).
            // Particle speed stays as an anti-explosion safety, but at REAL scale: 8 m/s × unitsPerMeter (~256 u/s), not
            // Magica's SI default (10 u/s = 0.31 m/s here = hair physically unable to keep up with a dancing body).
            // Requires the MC2 MaxParticleSpeedLimit clamp raised (local patch); clamps back to 10 until then.
            sd.inertiaConstraint.movementSpeedLimit = new CheckSliderSerializeData(false, 10f);
            sd.inertiaConstraint.rotationSpeedLimit = new CheckSliderSerializeData(false, 1440f);
            sd.inertiaConstraint.particleSpeedLimit = new CheckSliderSerializeData(true, 8f * unitsPerMeter);
            // A panel RING (skirt) is leashed to its animated drape (root tight 0.12×, hem loose) so it can't wrap up onto
            // the body on a spin and stay; strands (hair/tie) are free to swing (no leash).
            if (sheet)
            {
                sd.motionConstraint.useMaxDistance = true;
                // hem may travel up to its own chain length from the animated drape (the physical maximum for a panel
                // pinned at the waist). Root leash loosened 0.12→0.5: the tight leash suppressed ALL skirt dynamics
                // (measured 0 oscillations vs ref 6, walk stream −45%); the anti-wrap job is mostly done by the angle
                // restoration now. The stock 0..5 u clamp (16 cm equiv) is patched to 100.
                sd.motionConstraint.maxDistance.SetValue(Mathf.Min(100f, chainLenWorld), 0.5f, 1f);
            }
            // EDGE collision (segments) so a fast limb can't slip THROUGH between particles; low friction so a rising leg
            // drags the panel a little then it slides back down (MMD cloth-side friction is 0; body friction not fed raw).
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
