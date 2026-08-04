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
    /// <item>That firmness is derived PER CHAIN and only merged across chains that would get the same numbers anyway
    /// (<see cref="MmdClothChains"/>) — averaging a whole part together let 26 sprung tufts dictate the tuning of 420
    /// twintail bones.</item>
    /// </list>
    /// Collision uses EDGE mode (segments between particles, not just points) so a fast leg can't slip between skirt
    /// particles during big dance moves; the global simulation frequency is raised for the same reason. The tie is the
    /// one intentional deviation: the user wants it to hang freely, so its angle restoration is off. Live-tunable
    /// (gravity / stiffness / collider radius) via <see cref="Tune"/>.
    ///
    /// EVERY converted value is a <see cref="MmdClothPart"/>, and a <c>physics.ini</c> in the model's folder overrides
    /// whatever keys it names (<see cref="MmdClothProfile"/>) — have the file, it wins; no file, pure conversion. What
    /// the model decides (which bones are cloth, their collision shapes/filters, chain length, panel-vs-strand) is
    /// geometry and always comes from the .pmx; the file only carries TUNING.
    /// </summary>
    public sealed class MmdMagicaCloth
    {
        private readonly List<MagicaCloth> _cloths = new List<MagicaCloth>();
        private readonly List<float> _baseGrav = new List<float>();    // per-cloth base gravity (× user gravMul)
        private readonly List<float> _baseStiff = new List<float>();   // per-cloth base angle-restoration stiffness
        private readonly List<ColliderComponent> _colliders = new List<ColliderComponent>();
        private readonly List<Vector3> _colBaseSize = new List<Vector3>();   // (radius, radius|0, length|0); sphere = (r,0,0)

        // The resolved tuning per built CLOTH (converted, then overridden by physics.ini) — what Save writes back out.
        // A part can now own several cloths (chains that behave differently), so the bone count rides along: the
        // biggest cloth is the one that represents its part in the file, which has one section per part.
        private readonly List<KeyValuePair<MmdClothPartId, MmdClothPart>> _resolved =
            new List<KeyValuePair<MmdClothPartId, MmdClothPart>>();
        private readonly List<int> _resolvedBones = new List<int>();
        private float _simFreq = 150f;      // global solver rate (profile: [global] simulationFrequency)
        private float _profileColMul = 1f;  // profile's collider-radius multiplier; the panel's knob multiplies on top
        private float _liveGrav = 1f, _liveStiff = 1f, _liveCol = 1f;   // the debug panel's live knobs

        public bool Any => _cloths.Count > 0;
        public int ClothCount => _cloths.Count;
        public int ColliderCount => _colliders.Count;

        /// <summary>The physics.ini this rig was built with, or null when the tuning came straight from the .pmx.</summary>
        public string ProfilePath { get; private set; }

        /// <summary>The tuning the rig is running RIGHT NOW: the resolved values with the debug panel's live gravity /
        /// stiffness knobs folded in — i.e. exactly what <see cref="MmdClothProfile.Save"/> should write so that
        /// re-loading it reproduces what you are looking at.</summary>
        public IEnumerable<KeyValuePair<MmdClothPartId, MmdClothPart>> CurrentParts
        {
            get
            {
                // one entry per PART (physics.ini has one section per part) — represented by that part's biggest cloth
                for (int part = 0; part < 4; part++)
                {
                    int best = -1;
                    for (int i = 0; i < _resolved.Count; i++)
                        if ((int)_resolved[i].Key == part && (best < 0 || _resolvedBones[i] > _resolvedBones[best])) best = i;
                    if (best < 0) continue;
                    var p = _resolved[best].Value;
                    p.GravityMul *= _liveGrav;
                    if (p.AngleStiffness > 0.001f) p.AngleStiffness = Mathf.Clamp01(p.AngleStiffness * _liveStiff);
                    yield return new KeyValuePair<MmdClothPartId, MmdClothPart>((MmdClothPartId)part, p);
                }
            }
        }

        public float CurrentSimulationFrequency => _simFreq;
        public float CurrentColliderMul => _profileColMul * _liveCol;

        /// <param name="profile">The model's physics.ini, or null → convert everything from the .pmx.</param>
        public static MmdMagicaCloth Setup(GameObject host, Transform[] bone, int[] parent, PmxLoader pmx, float unitScale,
                                           MmdClothProfile profile = null)
        {
            if (pmx?.PhysicsBones == null || pmx.PhysicsBones.Count == 0) return null;
            var m = new MmdMagicaCloth();
            try { m.Build(host, bone, pmx, unitScale, profile); }   // `parent` is re-derived from the PMX bones themselves
            catch (System.Exception e) { Debug.LogWarning("[mmd] Magica Cloth setup failed: " + e.Message + "\n" + e.StackTrace); return null; }
            return m.Any ? m : null;
        }

        // Which part a bone belongs to, from its DYNAMIC rigid body's label (the attached bone is usually generically
        // named, but the rigid body is labelled Bang / Twintail / Dress / Tie / …). Falls back to the bone name.
        // A model whose bodies are named differently just lands everything in Hair — it still simulates; the per-part
        // feel is then whatever [hair] says (that is what physics.ini is for).
        public static MmdClothPartId GroupOf(string label)
        {
            string nm = label ?? "";
            if (nm.Contains("Bang") || nm.Contains("前髪")) return MmdClothPartId.Bang;
            if (nm.Contains("Dress") || nm.Contains("Skirt") || nm.Contains("スカート") || nm.Contains("裙")) return MmdClothPartId.Skirt;
            if (nm.Contains("Tie") || nm.Contains("ネクタイ") || nm.Contains("領帯") || nm.Contains("领带")) return MmdClothPartId.Tie;
            return MmdClothPartId.Hair;   // twintails / hairlines / breast / misc
        }

        private void Build(GameObject host, Transform[] bone, PmxLoader pmx, float unitScale, MmdClothProfile profile)
        {
            int bc = pmx.Bones.Count;
            ProfilePath = profile?.Path;
            _simFreq = profile != null ? profile.SimulationFrequency(_simFreq) : _simFreq;
            _profileColMul = profile != null ? profile.ColliderRadiusMul(1f) : 1f;

            // ---- body colliders from the model's KINEMATIC rigid bodies (exact authored shapes + their groups) ----
            var colBodies = new List<PmxLoader.RigidBody>();   // colBodies[i] is what _colliders[i] came from
            BuildRigidBodyColliders(pmx, bone, colBodies);
            List<ColliderComponent> allCols = null;
            if (colBodies.Count == 0)   // model has no rigid-body data → hand-placed fallback, no group filtering
            {
                allCols = new List<ColliderComponent>();
                BuildFallbackColliders(pmx, bone, allCols, unitScale);
            }

            // ---- chains, each with ITS OWN authored firmness; identical-behaviour chains share a cloth ----
            var groups = MmdClothChains.Build(pmx.Bones, pmx.RigidBodies, pmx.BoneJointLimit, pmx.BoneJointSpring, colBodies);
            if (groups.Count == 0) return;

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
            foreach (var grp in groups)
            {
                var roots = new List<Transform>(grp.RootBones.Count);
                foreach (int r in grp.RootBones) if (r >= 0 && r < bc && bone[r] != null) roots.Add(bone[r]);
                if (roots.Count == 0) continue;

                // SHAPE RETENTION, calibrated against the pybullet ground truth (compare.py) — see MmdClothChains.Derive:
                //  - spring>0 (fringe/tufts): angle RESTORATION ∝ the authored rotation spring (9/10 PASS at 0.9×norm).
                //  - spring==0 (twintails/tie/skirt): MMD says "hold this shape" by LOCKING the joint limits, so those
                //    chains take the full restoration + a loose angle-LIMIT guard. Magica's angleLimit stiffness is NOT
                //    Bullet's ERP: at 1.0 it hard-glues the chain to the animated pose (rigid + violent whipping in a
                //    real dance); at 0.2 it never wins against gravity. Restoration is a FORCE — it yields under dance
                //    load and holds at rest — with a root-weighted curve so a 30-bone twintail does not sag.
                // CONNECTION: skirt = AutomaticMesh. MMD's panels are independent strands (Line matches Bullet's
                // dynamics better — flare/oscillation), but Magica's Edge collision on independent strands has only
                // VERTICAL edges, so a dance-speed leg slips BETWEEN panels (user-verified clipping). Anti-clip wins.
                var cp = MmdClothChains.Derive(grp.Stats);

                // ---- physics.ini (if the model has one) overrides whatever keys it names ----
                var id = grp.Part;
                if (profile != null) cp = profile.Apply(id, cp);
                bool sheet = cp.Connection == MmdClothConnection.Auto ? grp.Sheet : cp.Connection == MmdClothConnection.Mesh;
                var conn = sheet ? RenderSetupData.BoneConnectionMode.AutomaticMesh : RenderSetupData.BoneConnectionMode.Line;

                float radWorld = grp.Stats.RadMean * unitScale * cp.RadiusMul;   // particle radius is WORLD-space (× scaleRatio=1, not lossyScale)
                float chainLenWorld = Mathf.Max((grp.Stats.MaxY - grp.Stats.MinY) * unitScale, radWorld);   // ≈ how far the hem can travel

                List<ColliderComponent> partCols;
                if (allCols != null) partCols = allCols;
                else
                {
                    partCols = new List<ColliderComponent>(grp.ColliderIndices.Count);
                    foreach (int ci in grp.ColliderIndices) if (ci >= 0 && ci < _colliders.Count) partCols.Add(_colliders[ci]);
                }
                // Inertia reference = the body bone the chains ride on (head/hip/…). The SDO dance is in the bones, so a
                // cloth under the static _mmdRoot sees no body motion; parenting to that bone makes a spin rotate the
                // reference → inertia carries the chain.
                Transform refT = (grp.AnchorBone >= 0 && grp.AnchorBone < bc && bone[grp.AnchorBone] != null) ? bone[grp.AnchorBone] : host.transform;
                SdoLog.Note("mmd", $"  cloth[{id}:{grp.Label}] chains={grp.ChainCount} bones={grp.BoneCount} cols={partCols.Count} {(sheet ? "MESH" : "line")} " +
                                   $"anchor={(grp.AnchorBone >= 0 && grp.AnchorBone < bc ? pmx.Bones[grp.AnchorBone].NameJp : "root")} " +
                                   $"massR/T={grp.Stats.MassRoot:F1}/{grp.Stats.MassTip:F2} spring={grp.Stats.SpringMean:F1} locked={grp.Stats.LockedFrac:F2} " +
                                   $"-> angle={(cp.UseAngleRestoration ? cp.AngleStiffness.ToString("F2") : "off")} limit={cp.AngleLimitDeg:F1}° gFall={cp.GravityFalloff:F2} " +
                                   $"damp={cp.DampingRoot:F3}→{cp.DampingTip:F3} depthI={cp.DepthInertia:F2} radW={radWorld:F2} g={gravity * cp.GravityMul:F0}(upm={unitsPerMeter:F1})" +
                                   (profile != null && profile.Has(id) ? "  [physics.ini]" : ""));
                _resolved.Add(new KeyValuePair<MmdClothPartId, MmdClothPart>(id, cp));
                _resolvedBones.Add(grp.BoneCount);
                BuildCloth(refT, "Mmd" + id + "Cloth_" + grp.Label, roots, partCols, cp, gravity, radWorld, conn, sheet,
                           unitsPerMeter, chainLenWorld);
            }

            // Fast dance = fast bones; raise the global solver rate (default 90) to its hard cap (150) so a limb moves
            // less per substep → fewer chances to tunnel through the cloth. Collider collision has no per-cloth
            // iteration knob, so substeps are the only anti-tunnelling lever. Global; guarded (no-op outside play mode).
            try { MagicaManager.SetSimulationFrequency(Mathf.RoundToInt(_simFreq)); } catch { /* older API / edit mode */ }
            if (Mathf.Abs(_profileColMul - 1f) > 1e-4f) SetColliderRadius(1f);   // apply the profile's collider scale now
        }

        // Convert every KINEMATIC (mode 0) rigid body on a NON-physics body bone into a Magica collider that follows
        // that bone. The body it came from is recorded in the same order (colBodies[i] ↔ _colliders[i]) so
        // MmdClothChains can work out, per chain, which colliders the author lets it touch. Fingers (指) are skipped
        // (small + many). Offsets/sizes are raw PMX units — the collider inherits _mmdRoot's unitScale (lossyScale).
        private void BuildRigidBodyColliders(PmxLoader pmx, Transform[] bone, List<PmxLoader.RigidBody> colBodies)
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
                colBodies.Add(rb);
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

        // Push one resolved MmdClothPart (converted, then physics.ini-overridden) onto a Magica Cloth component.
        private void BuildCloth(Transform parentT, string name, List<Transform> roots, List<ColliderComponent> cols,
                                MmdClothPart p, float gravity, float particleRadius,
                                RenderSetupData.BoneConnectionMode connectionMode, bool sheet,
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
            float grav = gravity * p.GravityMul;
            sd.gravity = grav;                                     // gravityDirection defaults to (0,-1,0) = world down
            sd.gravityFalloff = Mathf.Clamp01(p.GravityFalloff);   // 0 = hang straight down; 1 (pinned bang) = hold rest shape
            // authored linear-damping gradient → root→tip air-resistance curve (root light, tip heavier so the tip settles)
            if (p.DampingTip > 1e-4f) sd.damping.SetValue(p.DampingTip, p.DampingRoot / p.DampingTip, 1f);
            else sd.damping.SetValue(Mathf.Clamp01(p.DampingRoot));
            // Particle thickness = the body's own collision radius (world-scaled), THIN near the body so it doesn't puff
            // over the adjacent hip/shoulder collider, FULL toward the tip where it must catch limbs.
            if (particleRadius > 1e-4f) sd.radius.SetValue(particleRadius, p.RadiusRootScale, 1f);
            sd.distanceConstraint.stiffness.SetValue(1f);   // 0/0-position joints = inextensible strand
            // SHAPE = force-based angle RESTORATION (yields under dance load, holds at rest) + a loose hard LIMIT guard.
            sd.angleRestorationConstraint.useAngleRestoration = p.UseAngleRestoration;
            if (p.UseAngleRestoration)
            {
                // pendulum chains: root-weighted curve — full strength where the gravity torque concentrates, less at the
                // tip where the whip should live. Sprung parts (the fringe) stay flat.
                if (p.RootWeighted) sd.angleRestorationConstraint.stiffness.SetValue(p.AngleStiffness, 1f, p.AngleStiffTipScale);
                else sd.angleRestorationConstraint.stiffness.SetValue(p.AngleStiffness);
            }
            if (p.AngleLimitDeg > 0.01f)
            {
                sd.angleLimitConstraint.useAngleLimit = true;
                sd.angleLimitConstraint.limitAngle.SetValue(p.AngleLimitDeg);
                sd.angleLimitConstraint.stiffness = p.AngleLimitStiffness;
            }
            // FOLLOW the anchor bone with a lag (= swing). worldInertia = how much body motion reaches the cloth (1 = all,
            // like MMD); depthInertia carries the heavy root / lets the light tip lag (whip).
            sd.inertiaConstraint.worldInertia = Mathf.Clamp01(p.WorldInertia);
            sd.inertiaConstraint.depthInertia = Mathf.Clamp01(p.DepthInertia);
            sd.inertiaConstraint.movementInertiaSmoothing = Mathf.Clamp01(p.InertiaSmoothing);
            // MMD imposes no world speed caps → DISABLE movement + rotation limits (let the cloth lag on fast walk/spin).
            // Particle speed stays as an anti-explosion safety, but at REAL scale: m/s × unitsPerMeter, not Magica's SI
            // default (10 u/s = 0.31 m/s here = hair physically unable to keep up with a dancing body).
            // Requires the MC2 MaxParticleSpeedLimit clamp raised (local patch); clamps back to 10 until then.
            sd.inertiaConstraint.movementSpeedLimit = new CheckSliderSerializeData(false, 10f);
            sd.inertiaConstraint.rotationSpeedLimit = new CheckSliderSerializeData(false, 1440f);
            sd.inertiaConstraint.particleSpeedLimit = new CheckSliderSerializeData(true, p.ParticleSpeedLimitMps * unitsPerMeter);
            // A panel RING (skirt) is leashed to its animated drape so it can't wrap up onto the body on a spin and stay;
            // strands (hair/tie) swing free (no leash). The hem may travel up to its own chain length — the physical
            // maximum for a panel pinned at the waist. (The stock 0..5 u clamp is patched to 100.)
            if (sheet)
            {
                sd.motionConstraint.useMaxDistance = true;
                sd.motionConstraint.maxDistance.SetValue(Mathf.Min(100f, chainLenWorld * p.MaxDistanceMul), p.MaxDistanceRootScale, 1f);
            }
            // EDGE collision (segments) so a fast limb can't slip THROUGH between particles; low friction so a rising leg
            // drags the panel a little then it slides back down (MMD cloth-side friction is 0; body friction not fed raw).
            sd.colliderCollisionConstraint.mode = ColliderCollisionConstraint.Mode.Edge;
            sd.colliderCollisionConstraint.friction = p.Friction;
            sd.colliderCollisionConstraint.colliderList.AddRange(cols);
            cloth.BuildAndRun();
            _cloths.Add(cloth); _baseGrav.Add(grav); _baseStiff.Add(p.AngleStiffness);
        }

        public void SetEnabled(bool on) { foreach (var c in _cloths) if (c != null) c.enabled = on; }

        /// <summary>Live tune (matches the SDO debug panel). <paramref name="stiffMul"/> scales each part's base
        /// stiffness (clamped to 1), <paramref name="gravMul"/> scales gravity.</summary>
        public void Tune(float gravMul, float stiffMul)
        {
            _liveGrav = gravMul; _liveStiff = stiffMul;   // remembered so Save writes what you are actually looking at
            for (int i = 0; i < _cloths.Count; i++)
            {
                var c = _cloths[i]; if (c == null) continue;
                c.SerializeData.gravity = _baseGrav[i] * gravMul;
                if (_baseStiff[i] > 0.001f)   // keep the free-hanging tie free (don't turn restoration back on)
                    c.SerializeData.angleRestorationConstraint.stiffness.SetValue(Mathf.Clamp01(_baseStiff[i] * stiffMul));
                c.SetParameterChange();
            }
        }

        /// <summary><paramref name="mul"/> is the debug panel's knob; the profile's own colliderRadiusMul multiplies on
        /// top, so a saved profile keeps its collider scale while the panel still starts from 1×.</summary>
        public void SetColliderRadius(float mul)
        {
            _liveCol = mul;
            float m = mul * _profileColMul;
            for (int i = 0; i < _colliders.Count; i++)
            {
                var s = _colBaseSize[i];
                if (_colliders[i] is MagicaSphereCollider sp) sp.SetSize(s.x * m);
                else if (_colliders[i] is MagicaCapsuleCollider cap) cap.SetSize(s.x * m, s.y * m, s.z);   // scale radius, keep length
            }
            foreach (var c in _cloths) if (c != null) c.SetParameterChange();
        }

        private static Vector3 MmdBonePos(PmxLoader pmx, string nameJp) { foreach (var b in pmx.Bones) if (b.NameJp == nameJp) return b.Position; return Vector3.zero; }
        private static int Find(PmxLoader pmx, string nameJp) { for (int i = 0; i < pmx.Bones.Count; i++) if (pmx.Bones[i].NameJp == nameJp) return i; return -1; }
    }
}
