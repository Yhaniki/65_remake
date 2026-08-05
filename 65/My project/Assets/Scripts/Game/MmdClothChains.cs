using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Splits a PMX's dynamic rigid bodies into CHAINS (one per strand / skirt panel / tie), derives each chain's
    /// firmness from ITS OWN joints, and merges only the chains that behave identically into one Magica cloth.
    ///
    /// <b>Why this exists.</b> The converter used to accumulate one set of numbers per hard-coded part
    /// (<see cref="MmdClothPartId"/>) and average across everything in it. On a real model that average is meaningless:
    /// Ika Miku's "hair" part is 446 bones spanning 24 chains — ten 30-bone twintails plus 26 bones of short 3-bone
    /// tufts. The tufts are authored with a rotation SPRING of 5, the twintails with none (their joints are LOCKED,
    /// which is how MMD says "hold this shape"). Averaging gave springMean 0.291 for the whole part, which fell into
    /// the "sprung" branch and produced angle-restoration 0.052 for everything — 6% of the bones dragged the other 94%
    /// (every twintail) down to nearly zero shape retention, while the values the user actually tuned (0.9) only ever
    /// reached the fringe/skirt/tie. Per chain, each of those groups derives its own correct value.
    ///
    /// <b>What gets merged.</b> One Magica cloth per chain would be ~47 cloths on this model (each with its own
    /// solver team and collider list), and the skirt in particular MUST stay one cloth so its panels can be connected
    /// into a ring. So chains are bucketed by a BEHAVIOUR SIGNATURE — the part, the body bone they hang from, sheet vs
    /// strand, the exact set of colliders the author lets them touch, and the derived firmness numbers quantised to a
    /// bucket. Chains that would have been given the same Magica parameters anyway share a cloth; chains that would
    /// not, don't. Ika Miku lands on ~7 cloths.
    ///
    /// Pure logic (no Unity objects beyond Vector3) so the split, the signature and the derived tuning can be unit
    /// tested — see <c>MmdClothChainsTests</c>. <see cref="MmdMagicaCloth"/> turns the result into components.
    /// </summary>
    public static class MmdClothChains
    {
        /// <summary>The authored firmness of one chain (or of a merged bucket of identical chains), as read off the
        /// model's rigid bodies + joints. Everything here is in the PMX's own units / Bullet's own conventions;
        /// <see cref="Derive"/> is what turns it into Magica numbers.</summary>
        public struct ClothStats
        {
            public float SpringMean;    // joint rotation spring (Bullet setStiffness), mean over the chain's joints
            public float LimMeanRad;    // joint rotation-limit width, mean (rad) — 0 = the author locked that joint
            public float LockedFrac;    // fraction of joints whose limit is ~0 (MMD's "hold this shape")
            public float AngDampMean;   // authored ANGULAR damping (Bullet clamps to 1) — the author's main energy sink
            public float LinRoot, LinTip;   // authored LINEAR damping at the chain root / tips
            public float MassRoot, MassTip; // authored mass at the root / tips (root heavy = anchored, tip light = whips)
            public float RadMean;       // mean body radius = how thick the strand is
            public float MinY, MaxY;    // bone Y extent ≈ chain length (raw PMX units)
        }

        /// <summary>One Magica cloth to build: the chains that share a behaviour signature.</summary>
        public sealed class ClothGroup
        {
            public MmdClothPartId Part;
            public int AnchorBone = -1;                       // body bone the chains hang from (inertia reference)
            public bool Sheet;                                // box-shaped bodies ⇒ a panel sheet (skirt), not strands
            public readonly List<int> RootBones = new List<int>();      // one per chain
            public readonly List<int> ColliderIndices = new List<int>();// into the caller's kinematic-collider list
            public int BoneCount, ChainCount;
            public ClothStats Stats;
            public string Label;                              // diagnostics: first chain's body name (+N more)
        }

        // The 準標準ボーン a cloth chain can hang off. A strand's immediate parent is usually a nameless hair-root bone;
        // what matters for the inertia reference is the BODY part it rides on, and using that directly also lets chains
        // whose roots hang off different (rigidly-linked) hair roots share one cloth. Longest names first so 上半身2
        // wins over 上半身.
        private static readonly string[] BodyAnchors =
        {
            "上半身2", "上半身3", "上半身", "下半身", "首", "頭", "腰", "センター", "グルーブ", "全ての親",
            "左肩", "右肩", "左腕", "右腕", "左ひじ", "右ひじ", "左手首", "右手首",
            "左足", "右足", "左ひざ", "右ひざ", "左足首", "右足首",
        };

        /// <summary>Split + merge. <paramref name="colliderBodies"/> is the kinematic-body list the caller turned into
        /// Magica colliders, in the same order (a group's <see cref="ClothGroup.ColliderIndices"/> index into it);
        /// pass null/empty when the model has none and the caller falls back to hand-placed colliders.</summary>
        public static List<ClothGroup> Build(IList<PmxLoader.Bone> bones, IList<PmxLoader.RigidBody> bodies,
                                             IDictionary<int, float> jointLimit, IDictionary<int, float> jointSpring,
                                             IList<PmxLoader.RigidBody> colliderBodies)
        {
            var outGroups = new List<ClothGroup>();
            if (bones == null || bodies == null) return outGroups;

            // first dynamic body per bone (a bone can carry several; the first is the one the converter uses)
            var bodyOf = new Dictionary<int, PmxLoader.RigidBody>();
            foreach (var rb in bodies)
                if (rb != null && rb.Mode != 0 && rb.Bone >= 0 && rb.Bone < bones.Count && !bodyOf.ContainsKey(rb.Bone))
                    bodyOf[rb.Bone] = rb;
            if (bodyOf.Count == 0) return outGroups;

            var phys = new HashSet<int>(bodyOf.Keys);
            var children = new Dictionary<int, List<int>>();
            foreach (int b in phys)
            {
                int p = Parent(bones, b);
                if (!phys.Contains(p)) continue;
                if (!children.TryGetValue(p, out var lst)) children[p] = lst = new List<int>();
                lst.Add(b);
            }

            var order = new List<int>(phys); order.Sort();
            var buckets = new Dictionary<string, ClothGroup>();
            foreach (int root in order)
            {
                if (phys.Contains(Parent(bones, root))) continue;   // not a chain root

                // the chain = this root's whole physics subtree (a strand may fork)
                var chain = new List<int>();
                var tips = new List<int>();
                var stack = new Stack<int>(); stack.Push(root);
                while (stack.Count > 0)
                {
                    int b = stack.Pop();
                    chain.Add(b);
                    if (children.TryGetValue(b, out var cs)) { foreach (int c in cs) stack.Push(c); }
                    else tips.Add(b);
                }

                var st = Accumulate(bones, bodyOf, jointLimit, jointSpring, chain, root, tips,
                                    out MmdClothPartId part, out bool sheet);
                int anchor = BodyAnchorOf(bones, Parent(bones, root));
                var cols = SelectColliders(bodyOf, chain, colliderBodies);

                string sig = Signature(part, anchor, sheet, cols, st);
                if (!buckets.TryGetValue(sig, out var grp))
                {
                    buckets[sig] = grp = new ClothGroup
                    {
                        Part = part, AnchorBone = anchor, Sheet = sheet, Stats = st,
                        Label = bodyOf[root].Name ?? ("bone" + root),
                    };
                    grp.ColliderIndices.AddRange(cols);
                    outGroups.Add(grp);
                }
                else
                {
                    grp.Stats = Merge(grp.Stats, grp.BoneCount, st, chain.Count, grp.ChainCount);
                }
                grp.RootBones.Add(root);
                grp.BoneCount += chain.Count;
                grp.ChainCount++;
            }

            foreach (var g in outGroups)
                if (g.ChainCount > 1) g.Label += "+" + (g.ChainCount - 1);
            return outGroups;
        }

        private static int Parent(IList<PmxLoader.Bone> bones, int b)
            => b >= 0 && b < bones.Count ? bones[b].Parent : -1;

        /// <summary>The authored data of one chain, read off its bodies and joints.</summary>
        private static ClothStats Accumulate(IList<PmxLoader.Bone> bones, Dictionary<int, PmxLoader.RigidBody> bodyOf,
                                             IDictionary<int, float> jointLimit, IDictionary<int, float> jointSpring,
                                             List<int> chain, int root, List<int> tips,
                                             out MmdClothPartId part, out bool sheet)
        {
            var st = new ClothStats { MinY = float.PositiveInfinity, MaxY = float.NegativeInfinity };
            float limSum = 0f, sprSum = 0f, angSum = 0f, radSum = 0f;
            int limN = 0, sprN = 0, lockedN = 0, boxN = 0, bodyN = 0;
            var votes = new int[4];
            foreach (int b in chain)
            {
                if (bodyOf.TryGetValue(b, out var rb))
                {
                    votes[(int)MmdMagicaCloth.GroupOf(rb.Name)]++;
                    angSum += rb.AngularDamp; radSum += Mathf.Max(rb.Size.x, 1e-3f); bodyN++;
                    if (rb.Shape == 1) boxN++;
                }
                if (jointLimit != null && jointLimit.TryGetValue(b, out float lim))
                { limSum += lim; limN++; if (lim < 1e-4f) lockedN++; }
                if (jointSpring != null && jointSpring.TryGetValue(b, out float spr)) { sprSum += spr; sprN++; }
                if (b >= 0 && b < bones.Count)
                {
                    float y = bones[b].Position.y;
                    if (y < st.MinY) st.MinY = y;
                    if (y > st.MaxY) st.MaxY = y;
                }
            }
            st.SpringMean = sprN > 0 ? sprSum / sprN : 0f;
            st.LimMeanRad = limN > 0 ? limSum / limN : 0f;
            st.LockedFrac = limN > 0 ? (float)lockedN / limN : 0f;
            st.AngDampMean = bodyN > 0 ? angSum / bodyN : 0f;
            st.RadMean = bodyN > 0 ? radSum / bodyN : 0.2f;
            var rootRb = bodyOf.TryGetValue(root, out var rr) ? rr : null;
            st.LinRoot = rootRb != null ? rootRb.LinearDamp : 0.5f;
            st.MassRoot = rootRb != null ? rootRb.Mass : 1f;
            float tipLin = 0f, tipMass = 0f; int tipN = 0;
            foreach (int t in tips) if (bodyOf.TryGetValue(t, out var trb)) { tipLin += trb.LinearDamp; tipMass += trb.Mass; tipN++; }
            st.LinTip = tipN > 0 ? tipLin / tipN : st.LinRoot;
            st.MassTip = tipN > 0 ? tipMass / tipN : st.MassRoot;

            int best = 0;
            for (int i = 1; i < 4; i++) if (votes[i] > votes[best]) best = i;
            part = (MmdClothPartId)best;
            sheet = bodyN > 0 && boxN * 2 >= bodyN;
            if (float.IsInfinity(st.MinY)) { st.MinY = 0f; st.MaxY = 0f; }
            return st;
        }

        /// <summary>Bone-count-weighted merge of two identical-signature chains (chain-level quantities — root/tip
        /// damping and mass — average per CHAIN, the rest per BONE).</summary>
        private static ClothStats Merge(ClothStats a, int aBones, ClothStats b, int bBones, int aChains)
        {
            float wa = aBones, wb = bBones, w = Mathf.Max(wa + wb, 1e-6f);
            float ca = aChains, cb = 1f, c = ca + cb;
            return new ClothStats
            {
                SpringMean = (a.SpringMean * wa + b.SpringMean * wb) / w,
                LimMeanRad = (a.LimMeanRad * wa + b.LimMeanRad * wb) / w,
                LockedFrac = (a.LockedFrac * wa + b.LockedFrac * wb) / w,
                AngDampMean = (a.AngDampMean * wa + b.AngDampMean * wb) / w,
                RadMean = (a.RadMean * wa + b.RadMean * wb) / w,
                LinRoot = (a.LinRoot * ca + b.LinRoot * cb) / c,
                LinTip = (a.LinTip * ca + b.LinTip * cb) / c,
                MassRoot = (a.MassRoot * ca + b.MassRoot * cb) / c,
                MassTip = (a.MassTip * ca + b.MassTip * cb) / c,
                MinY = Mathf.Min(a.MinY, b.MinY),
                MaxY = Mathf.Max(a.MaxY, b.MaxY),
            };
        }

        // Two rigid bodies collide iff each has the OTHER's group bit set in its collision-enable mask.
        private static bool Collide(byte gA, ushort mA, byte gB, ushort mB)
            => ((mA >> gB) & 1) == 1 && ((mB >> gA) & 1) == 1;

        /// <summary>Which of the caller's colliders this chain's bodies are authored to touch.</summary>
        private static List<int> SelectColliders(Dictionary<int, PmxLoader.RigidBody> bodyOf, List<int> chain,
                                                 IList<PmxLoader.RigidBody> colliderBodies)
        {
            var outl = new List<int>();
            if (colliderBodies == null) return outl;
            for (int i = 0; i < colliderBodies.Count; i++)
            {
                var cb = colliderBodies[i];
                if (cb == null) continue;
                foreach (int b in chain)
                {
                    if (!bodyOf.TryGetValue(b, out var rb)) continue;
                    if (Collide(rb.Group, rb.Mask, cb.Group, cb.Mask)) { outl.Add(i); break; }
                }
            }
            return outl;
        }

        /// <summary>Walk up to the body bone this chain rides on (see <see cref="BodyAnchors"/>); the chain's own
        /// parent when none is found (unnamed rig, non-MMD-standard skeleton).</summary>
        public static int BodyAnchorOf(IList<PmxLoader.Bone> bones, int from)
        {
            int guard = 0;
            for (int b = from; b >= 0 && b < bones.Count && guard++ < 256; b = bones[b].Parent)
            {
                string n = bones[b].NameJp ?? "";
                for (int i = 0; i < BodyAnchors.Length; i++) if (n == BodyAnchors[i]) return b;
            }
            return from;
        }

        // Chains merge only when every knob they'd be given lands in the same bucket. Quantisation steps are ~a third
        // of the range each knob spans in practice: finer would split chains that feel identical, coarser would merge
        // the twintails back into the tufts (the bug this class exists to fix).
        private static string Signature(MmdClothPartId part, int anchor, bool sheet, List<int> cols, in ClothStats s)
        {
            var sb = new System.Text.StringBuilder(64);
            sb.Append((int)part).Append('|').Append(anchor).Append('|').Append(sheet ? 1 : 0).Append('|');
            for (int i = 0; i < cols.Count; i++) sb.Append(cols[i]).Append(',');
            sb.Append('|').Append(Q(s.SpringMean, 0.5f)).Append('|').Append(Q(s.LockedFrac, 0.25f))
              .Append('|').Append(Q(s.LimMeanRad, 0.09f)).Append('|').Append(Q(s.LinRoot, 0.15f))
              .Append('|').Append(Q(s.LinTip, 0.15f)).Append('|').Append(Q(MassGradient(s), 1f))
              .Append('|').Append(Q(s.AngDampMean, 0.5f));
            return sb.ToString();
        }

        private static int Q(float v, float step) => Mathf.RoundToInt(v / step);

        /// <summary>ln(root mass / tip mass) — MMD authors the root heavy (anchored) and the tip light (whips).</summary>
        public static float MassGradient(in ClothStats s)
            => Mathf.Log(Mathf.Max(s.MassRoot / Mathf.Max(s.MassTip, 1e-3f), 1f));

        // Bullet/MMD damping is PER-SECOND (velocity retains (1−d) after 1 s); Magica damping is applied PER SUBSTEP
        // (at our 150 Hz: v ×= 1 − m×0.6 each step). Match one second of decay: (1 − m×0.6)^150 = (1 − d)
        //   ⇒ m = (1 − (1−d)^(1/150)) / 0.6.   e.g. d=0.2 → m=0.0025, d=1.0(capped) → m=0.043 ≈ Magica's stock range.
        public static float BulletToMagicaDamping(float bulletDamp, float freq = 150f)
        {
            // MC2 applies `velocity *= 1 - damping * simulationPower.z`, and TimeManager sets
            // z = t (t = 90/freq) while t <= 1, t^0.3 above — derived here rather than hard-coded so raising the
            // solver rate does not silently change how hard every model's cloth is damped.
            float t = 90f / Mathf.Max(freq, 1f);
            float power = t > 1f ? Mathf.Pow(t, 0.3f) : t;
            float retainPerSec = Mathf.Clamp(1f - bulletDamp, 0.02f, 1f);
            return Mathf.Clamp((1f - Mathf.Pow(retainPerSec, 1f / Mathf.Max(freq, 1f))) / power, 0f, 0.2f);
        }

        /// <summary>How much a strand of Bullet rigid bodies actually bleeds energy, as ONE per-second number Magica
        /// can use. Bullet damps linear AND angular velocity; a swinging chain carries its energy in both, and the
        /// converter used to read only the linear one. Ika's twintails are authored linear 0.2 / ANGULAR 2.0 (Bullet
        /// clamps that to 1 = the angular velocity is gone within the second) — reading only the linear term made them
        /// the least damped thing on the model, which measured as "never settles" and a 3.5× over-strong spin fling
        /// against the reference sim. The rotation of a bone in a chain is not free, though (its neighbours constrain
        /// it), so an ORDINARY angular damping is worth roughly HALF a linear one. A value ABOVE 1 is different: Bullet
        /// clamps it, so it buys the author nothing inside the sim and is purely how MMD says "settle quickly" — that
        /// one is taken at FULL weight, because at half the twintails kept ringing (reference settles them in 1.9 s,
        /// the probe measured "never", and on screen that is springy bouncing). Weighting EVERY angular term fully
        /// instead would also drag the skirt/tie root damping 0.5 → 0.995 (they are authored angular 0.99…1.0), which
        /// is not what the twintail fix was after: the probe measured that as ~40% less droop and stream on both.
        /// Either way it only matters where it EXCEEDS what the linear term already removes, so the skirt/tie/fringe
        /// (authored linear 0.5…0.999) stay untouched.</summary>
        public static float EffectiveDamping(float linearDamp, float angularDamp, float angularWeight = 0.5f)
            => Mathf.Max(Mathf.Clamp01(linearDamp), Mathf.Clamp01(angularDamp) * (angularDamp > 1f ? 1f : angularWeight));

        /// <summary>The authored data → the Magica knobs. Unchanged formulas from the per-part converter; what changed
        /// is that they are now fed ONE chain's numbers instead of a whole part's average.</summary>
        public static MmdClothPart Derive(in ClothStats s)
        {
            float springNorm = Mathf.Clamp01(s.SpringMean / 5f);   // rotation-spring strength 0..1
            return new MmdClothPart
            {
                GravityMul = 1f,
                // gravityFalloff ← springNorm: a pinned part holds its shape against gravity (falloff→1 = gravity≈0
                // at rest pose); a pendulum keeps full gravity (falloff 0) so it hangs down.
                GravityFalloff = springNorm,
                UseAngleRestoration = true,
                // spring>0 (fringe/tufts): restoration ∝ the authored spring. spring==0 (twintails/tie/skirt): MMD says
                // "hold this shape" by LOCKING the joint limits instead, so those chains get the full user-tuned value.
                AngleStiffness = s.SpringMean > 0f ? Mathf.Clamp01(springNorm * 0.9f) : 0.9f,
                AngleStiffTipScale = 0.8f,                     // firmer tip = the "韌性" spring-back feel
                RootWeighted = s.SpringMean <= 0f,              // pendulum chains: strong root, free tip
                // ANGLE LIMIT = the authored joint range + a per-joint slack. The slack is what Bullet's soft
                // (ERP≈0.2) solve of a LOCKED limit leaves in practice, so it shrinks with how much of the chain the
                // author locked: a free-ish chain (skirt/tie) keeps the loose ~6° guard that only catches extreme
                // excursions, a locked one (twintails: 96% of joints at 0/0) gets ~1.5° — measured, not guessed: at 6°
                // the 30-bone twintail could accumulate 218° of bend and flung 3.7× as far as the reference sim.
                AngleLimitDeg = Mathf.Lerp(6f, 1.5f, Mathf.Clamp01(s.LockedFrac)) + Mathf.Rad2Deg * s.LimMeanRad,
                // Bullet solves locked limits with ERP≈0.2 per substep — a SOFT constraint that YIELDS under
                // dance-speed load and converges at rest. 1 (hard clamp) glues the hair to the head.
                AngleLimitStiffness = 0.2f,
                // DAMPING ← Bullet's LINEAR + ANGULAR damping (see EffectiveDamping), per-second → per-substep.
                DampingRoot = BulletToMagicaDamping(EffectiveDamping(s.LinRoot, s.AngDampMean)),
                DampingTip = BulletToMagicaDamping(EffectiveDamping(s.LinTip, s.AngDampMean)),
                RadiusMul = 1f,
                RadiusRootScale = 0.35f,   // thin near the body so it can't puff over the adjacent hip/shoulder collider
                // MMD/Bullet imparts the FULL body motion — at 0.67 the twintail under-swung 65%, zero spin fling.
                WorldInertia = 1f,
                // depthInertia has no Bullet counterpart — it exists because a PBD solver has no TENSION. In Bullet the
                // anchor drags the whole chain through its joints; in Magica the anchor's motion has to be handed to
                // the particles explicitly, and this is the knob that does it (SimulationManagerNormal lerps a particle
                // toward the anchor's step vector by depthInertia*(1-depth²) — full at the root, fading to the tip).
                // Measured against the reference sim, less of it = more lag, not more whip: 0.47 (the old mass-gradient
                // derivation) → walk stream 61°, 0 → 68°, against Bullet's 32°. Handing over the FULL motion is both
                // the closest analogue of Bullet's tension and what measured best.
                DepthInertia = 1f,
                InertiaSmoothing = 0.15f,   // shaves Magica's impulsive shift spike; 0.3 was too slow for a spin
                Connection = MmdClothConnection.Auto,
                MaxDistanceMul = 1f,        // hem may travel its own chain length from the animated drape
                MaxDistanceRootScale = 0.5f,
                Friction = 0.15f,           // low: a rising leg drags the panel a little, then it slides back down
                ParticleSpeedLimitMps = 8f, // anti-explosion only (scaled by unitsPerMeter at build)
            };
        }
    }
}
