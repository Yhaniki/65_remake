using System.Collections.Generic;

namespace Sdo.Game
{
    /// <summary>
    /// The animated "glide wing" (<c>_G</c>) rig — a wing's OWN skeleton + skinned mesh + looping <c>.mot</c> that
    /// flaps on the avatar's back INDEPENDENTLY of the body motion. This is the online-only feature the offline exe
    /// lacks (it sprintf's the <c>_chibang_g</c> filenames but never loads them = dead code). Reverse-engineered from
    /// the CN online client <c>sdo.bin</c> (FUN_0083f540, the character builder):
    ///   • it loops the equip slots; for the WING slot (slot 8) — when the room flag <c>+0xa84</c> is set and the
    ///     item isn't in a small exclusion list — it loads <c>&lt;id&gt;_&lt;gender&gt;_chibang_g.hrc</c> (a separate
    ///     skeleton), attaches <c>_g.msh</c> to slot 8 (FUN_0083cc20), then loads <c>&lt;id&gt;_&lt;gender&gt;_chibang.mot</c>
    ///     and plays it LOOPED (FUN_00411180 ..,1,..). The exhaustive switch names 265 distinct wing MODEL ids — the
    ///     authoritative "has an animated rig" set (extracted verbatim; see docs / online_flying_wings.tsv).
    /// The <c>+0xa84</c> gate itself is set (FUN ~292065) when the equipped wing id is in that same membership list, so
    /// the client-side rule for "which wings animate in the room" IS this id set (the packet only carries a present/idle
    /// flag). 花雨飛翼 = model 23920 (男) / 23921 (女), both members.
    ///
    /// Verified from the shipped data (<c>023920_MAN_CHIBANG.MOT</c>): 10 bones, 50 frames; the ROOT bone is static at
    /// the back attach point (male y≈50.5 / female y≈44.65, model space) with NO translation, and 6 feather bones flap
    /// via rotation (≈47-51 keys each). So the rig is authored in BODY MODEL SPACE — parenting it at the body origin
    /// (identity) sits it on the back and it flaps in place. This is SEPARATE from the BODY float (FLYSTAY, see
    /// <see cref="SpecialMotionItems"/>): a wing can animate without the body flying, and vice-versa. (The old
    /// "wing ships a _G mesh ⇒ it flies" heuristic was wrong for BODY float — but the _G rig here is gated on this
    /// decompiled id set, not on mesh presence, so non-animated wings that merely ship a _G mesh are excluded.)
    /// </summary>
    public static class AvatarWingRig
    {
        // Wing MODEL ids (the 6-digit mesh filename prefix) that carry an animated _G rig — VERBATIM from the online
        // sdo.bin FUN_0083f540 wing switch: every "<id>_<gender>_chibang_g.hrc" branch, deduped to 265 distinct model
        // ids. Keyed on the MODEL id (the value the remake parses from the mesh path), matching SpecialMotionItems.
        private static readonly HashSet<int> AnimatedRigModelIds = new HashSet<int>
        {
            8448, 8449, 8484, 15100, 15120, 15451, 17285, 17286, 17287, 17288, 17369, 17370,
            17371, 17372, 17373, 17374, 21023, 21024, 21089, 21090, 21091, 21723, 21724, 21729,
            21730, 22444, 22445, 22906, 22907, 23087, 23088, 23099, 23100, 23211, 23212, 23213,
            23214, 23316, 23317, 23691, 23692, 23834, 23835, 23836, 23837, 23849, 23850, 23920,
            23921, 23954, 23978, 23979, 24028, 24029, 24137, 24138, 24222, 24224, 24422, 24423,
            24610, 24611, 24738, 24739, 24747, 24748, 24913, 24914, 24915, 24916, 24917, 24918,
            25079, 25080, 25081, 25082, 25241, 25242, 25247, 25248, 25249, 25250, 25345, 25346,
            25347, 25351, 25352, 25372, 25373, 25534, 25535, 25570, 25571, 25904, 25905, 25922,
            25923, 25925, 26181, 26182, 26183, 26184, 26185, 26186, 26187, 26188, 26189, 26190,
            26192, 26193, 26194, 26195, 26196, 26387, 26388, 26389, 26390, 26391, 26392, 26654,
            26655, 26656, 26657, 26839, 26840, 26843, 27022, 27023, 27024, 27025, 27026, 27027,
            27028, 27029, 27030, 27195, 27196, 27228, 27229, 27378, 27379, 27381, 27382, 27383,
            27385, 27475, 27476, 27621, 27622, 27623, 27624, 27683, 27684, 27685, 27686, 27687,
            27688, 27689, 27690, 27691, 27692, 27693, 27737, 27738, 27904, 27905, 27906, 27907,
            27908, 27909, 28194, 28195, 28200, 28201, 28319, 28320, 28321, 28324, 28325, 28326,
            28329, 28330, 28331, 28334, 28335, 28336, 28337, 28338, 28339, 28340, 28341, 28342,
            28343, 28344, 28347, 28348, 28467, 28468, 28469, 28470, 28471, 28472, 28473, 28474,
            28925, 28927, 28928, 28929, 28931, 29152, 29153, 29155, 29624, 29626, 29627, 29629,
            29958, 29959, 29960, 29962, 29963, 29964, 30238, 30239, 30240, 30242, 30811, 30812,
            30814, 31147, 31148, 31150, 31320, 31637, 31823, 32001, 32193, 32394, 32597, 32783,
            32977, 33218, 33398, 33569, 33925, 34097, 34274, 34296, 34482, 34667, 34830, 35011,
            35189, 35384, 35632, 35719, 36072, 36273, 36471, 36656, 36925, 37116, 37315, 37526,
            37736,
        };

        /// <summary>True if <paramref name="modelId"/> (mesh prefix) is a wing with an animated <c>_G</c> rig.</summary>
        public static bool HasAnimatedRig(int modelId) => AnimatedRigModelIds.Contains(modelId);

        // Wings rendered as a STATIC wing (the base mesh, skinned to the BODY) instead of the animated _G rig. The base
        // mesh already skins to the body skeleton (so it bobs with the idle/walk) and its material points at ONE fixed
        // glow frame (甜心飛翼 023691/023692_*_CHIBANG.MSH → 023691_woman_chibang5.dds) — exactly what the 儲物間 / 男女選擇
        // previews show (they render the base mesh, not the rig). So for these wings we DON'T build the _G rig: it makes
        // the room match the wardrobe (same still texture, 維持全亮), follow the body, and stop the glow cycling — all at
        // once (使用者:「甜心飛翼 貼圖維持全亮,跟儲物間一樣」). Add more model ids to render them static too.
        private static readonly HashSet<int> StaticWingModelIds = new HashSet<int> { 23691, 23692 };

        /// <summary>True if this wing MODEL id should be drawn as a STATIC body-skinned wing (base mesh) rather than the
        /// animated <c>_G</c> rig — so it looks exactly like the 儲物間 / 男女選擇 preview (fixed glow frame, follows body).</summary>
        public static bool RenderAsStatic(int modelId) => StaticWingModelIds.Contains(modelId);

        /// <summary>The animated-wing model ids (read-only view, for tests / diagnostics).</summary>
        public static IEnumerable<int> AnimatedWingModelIds => AnimatedRigModelIds;

        /// <summary>The resolved <c>_G</c> rig asset paths (Datas-relative) for an equipped wing.</summary>
        public struct Paths
        {
            public string HrcRel;   // AVATAR/&lt;stem&gt;_G.HRC  — the wing's own skeleton
            public string MshRel;   // AVATAR/&lt;stem&gt;_G.MSH  — the wing mesh skinned to that skeleton
            public string MotRel;   // MOTION/&lt;stem&gt;.MOT    — the flap clip (looped)
            public int ModelId;     // the 6-digit model id (mesh prefix)
            public bool Male;       // gender parsed from the filename (informational; paths already encode it)
        }

        /// <summary>
        /// Resolve the animated <c>_G</c> rig paths for an equipped wing MSH path
        /// (e.g. <c>AVATAR/023920_MAN_CHIBANG.MSH</c> → <c>_G.HRC</c> / <c>_G.MSH</c> + <c>MOTION/023920_MAN_CHIBANG.MOT</c>).
        /// Returns false unless the path is a CHIBANG wing whose model id is in <see cref="AnimatedRigModelIds"/>.
        /// Pure (path strings only) — the caller checks the files actually exist before suppressing the static wing.
        /// </summary>
        public static bool TryResolve(string equippedMeshPath, out Paths paths)
        {
            paths = default;
            if (string.IsNullOrEmpty(equippedMeshPath)) return false;
            string norm = equippedMeshPath.Replace('\\', '/');
            int slash = norm.LastIndexOf('/');
            string file = slash >= 0 ? norm.Substring(slash + 1) : norm;
            int dot = file.LastIndexOf('.');
            string stem = dot >= 0 ? file.Substring(0, dot) : file;   // "023920_MAN_CHIBANG"
            string up = stem.ToUpperInvariant();
            if (up.IndexOf("CHIBANG", System.StringComparison.Ordinal) < 0) return false;
            if (up.EndsWith("_G")) { stem = stem.Substring(0, stem.Length - 2); up = up.Substring(0, up.Length - 2); }  // tolerate an already-_G stem
            var id = SpecialMotionItems.ModelIdFromMeshPath(file);
            if (!id.HasValue || !AnimatedRigModelIds.Contains(id.Value)) return false;
            paths = new Paths
            {
                HrcRel = "AVATAR/" + stem + "_G.HRC",
                MshRel = "AVATAR/" + stem + "_G.MSH",
                MotRel = "MOTION/" + stem + ".MOT",
                ModelId = id.Value,
                Male = up.IndexOf("WOMAN", System.StringComparison.Ordinal) < 0,   // check WOMAN first: "MAN" ⊂ "WOMAN"
            };
            return true;
        }

        /// <summary>True if any equipped mesh path is a wing carrying an animated <c>_G</c> rig.</summary>
        public static bool WearsAnimatedWing(IEnumerable<string> equippedParts)
        {
            if (equippedParts == null) return false;
            foreach (var p in equippedParts) if (TryResolve(p, out _)) return true;
            return false;
        }
    }
}
