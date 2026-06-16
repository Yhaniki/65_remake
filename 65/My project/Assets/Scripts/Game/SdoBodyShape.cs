using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Faithful port of the SDO body-shape (體型: 胖/標準/瘦) system. The original sets a per-bone NON-UNIFORM
    /// scale on the skeleton (decompiled <c>AvatarHelper_ScaleBones_004a0330</c>, gameplay/025_note:5236) driven by a
    /// single body "weight" <c>B</c> (1.0 = standard). Each affected bone keeps its LENGTH axis (local X = scale
    /// component 0, always 1.0) and scales only its CROSS-SECTION (the two perpendicular axes), so the dancer gets
    /// fatter (B&gt;1) or thinner (B&lt;1) WITHOUT changing height or limb length.
    ///
    /// Three groups (exact bone lists recovered from the EXE string tables at VA 0x585584 / 0x5855a0):
    ///   • BIG group  (rate ×1.9128205): lower torso + limb roots — Pelvis, Spine, Neck, L/R Thigh, L/R Calf, L/R UpperArm.
    ///   • SMALL group (rate ×1.0 = B)  : upper chest + distal limbs — Spine1, L/R Clavicle, L/R Forearm, L/R Hand.
    ///   • HEAD       (rate ×0.2725275): Bip01_Head — scales the Y axis only.
    /// All other bones (fingers, toes, feet, ponytail, root) are left unscaled.
    ///
    /// The body INDEX→weight table (decompiled Avatar_BuildCharacter, avatar/026_avatar:464-506): index 0..4 maps to a
    /// "weight" byte that is divided by a gender baseline (female 90, male 110) to give B. index 1 = standard (B=1.0).
    /// </summary>
    public static class SdoBodyShape
    {
        // bones whose cross-section scales fastest with body weight (lower torso + proximal limb segments)
        public static readonly string[] BigGroup =
        {
            "Bip01_Pelvis", "Bip01_Spine", "Bip01_Neck",
            "Bip01_L_Thigh", "Bip01_R_Thigh", "Bip01_L_Calf", "Bip01_R_Calf",
            "Bip01_L_UpperArm", "Bip01_R_UpperArm",
        };

        // bones that scale 1:1 with body weight (upper chest + distal limb segments)
        public static readonly string[] SmallGroup =
        {
            "Bip01_Spine1",
            "Bip01_L_Clavicle", "Bip01_L_Forearm", "Bip01_L_Hand",
            "Bip01_R_Clavicle", "Bip01_R_Forearm", "Bip01_R_Hand",
        };

        public const string HeadBone = "Bip01_Head";
        public const float BigRate = 1.9128205f;     // pelvis-table multiplier
        public const float HeadRate = 0.2725275f;    // head Y-axis multiplier

        /// <summary>
        /// The per-bone LOCAL scale for body weight <paramref name="weight"/> (B): X = bone length axis (kept at 1),
        /// Y/Z = cross-section. Compose into the bone's local matrix as <c>local * Matrix4x4.Scale(result)</c> so the
        /// scale is applied in bone-local axes (exactly as the original engine, which keeps scale component 0).
        /// Returns <see cref="Vector3.one"/> (no-op) for unaffected bones and for the standard weight B = 1.
        /// </summary>
        public static Vector3 ScaleFor(string bone, float weight)
        {
            float d = weight - 1f;   // the engine works on (B - 1)
            for (int i = 0; i < BigGroup.Length; i++)
                if (BigGroup[i] == bone) { float s = d * BigRate + 1f; return new Vector3(1f, s, s); }
            for (int i = 0; i < SmallGroup.Length; i++)
                if (SmallGroup[i] == bone) { float s = d + 1f; return new Vector3(1f, s, s); }   // d + 1 == B
            if (bone == HeadBone) { float s = d * HeadRate + 1f; return new Vector3(1f, s, 1f); }  // head: Y only
            return Vector3.one;
        }

        /// <summary>
        /// Body weight B from the in-game body index (0..4) and gender, faithful to Avatar_BuildCharacter:
        /// female weights {80,90,100,110,120}/90, male weights {100,110,120,130,140}/110.
        /// index 1 = standard (B = 1.0); index 0 = thinnest; index 4 = fattest.
        /// </summary>
        public static float WeightFromIndex(int index, bool male)
        {
            index = Mathf.Clamp(index, 0, 4);
            int[] female = { 80, 90, 100, 110, 120 };
            int[] maleW = { 100, 110, 120, 130, 140 };
            return male ? maleW[index] / 110f : female[index] / 90f;
        }
    }
}
