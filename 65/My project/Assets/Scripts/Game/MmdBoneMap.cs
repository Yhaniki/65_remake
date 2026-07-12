using System.Collections.Generic;

namespace Sdo.Game
{
    /// <summary>
    /// MMD bone name (PMX Japanese) → SDO Biped bone name (HRC "Bip01_*") correspondence — a verbatim port of the
    /// BONE_MAP in <c>tools/bms_sdo/mot_pipeline/vmd_to_mot_blender.py</c>, which was validated end-to-end converting
    /// VMD dances onto the SDO skeleton. That pipeline maps MMD motion → SDO; here we use the SAME table in the display
    /// direction: for each MMD bone we drive it from the HRC bone it corresponds to (world-space rotation-delta
    /// retarget in <see cref="MmdAvatar"/>). MMD bones with no entry (全ての親 / グルーブ / twist / skirt-physics /
    /// leg-IK bones) simply aren't driven — they hold their rest pose, which is correct for a first display pass.
    ///
    /// Notes carried over from the pipeline: FEMALE.HRC has no Bip01_Spine2, so 上半身2 maps to Bip01_Spine1; the MMD
    /// 上半身 (upper body) maps to Bip01_Spine.
    /// </summary>
    public static class MmdBoneMap
    {
        /// <summary>MMD Japanese bone name → HRC Bip01 bone name.</summary>
        public static readonly Dictionary<string, string> ToBip01 = new Dictionary<string, string>
        {
            { "センター", "Bip01" },            // root translation
            { "下半身",   "Bip01_Pelvis" },     // MMD lower body drives the pelvis (+ legs)
            { "上半身",   "Bip01_Spine" },
            { "上半身2",  "Bip01_Spine1" },     // FEMALE.HRC has no Spine2
            { "首",       "Bip01_Neck" },
            { "頭",       "Bip01_Head" },

            { "左肩",     "Bip01_L_Clavicle" },
            { "左腕",     "Bip01_L_UpperArm" },
            { "左ひじ",   "Bip01_L_Forearm" },
            { "左手首",   "Bip01_L_Hand" },
            { "右肩",     "Bip01_R_Clavicle" },
            { "右腕",     "Bip01_R_UpperArm" },
            { "右ひじ",   "Bip01_R_Forearm" },
            { "右手首",   "Bip01_R_Hand" },

            { "左足",     "Bip01_L_Thigh" },
            { "左ひざ",   "Bip01_L_Calf" },
            { "左足首",   "Bip01_L_Foot" },
            { "右足",     "Bip01_R_Thigh" },
            { "右ひざ",   "Bip01_R_Calf" },
            { "右足首",   "Bip01_R_Foot" },

            { "左親指０", "Bip01_L_Finger0" },  { "左親指１", "Bip01_L_Finger01" }, { "左親指２", "Bip01_L_Finger02" },
            { "左人指１", "Bip01_L_Finger1" },  { "左人指２", "Bip01_L_Finger11" }, { "左人指３", "Bip01_L_Finger12" },
            { "左中指１", "Bip01_L_Finger2" },  { "左中指２", "Bip01_L_Finger21" }, { "左中指３", "Bip01_L_Finger22" },
            { "左薬指１", "Bip01_L_Finger3" },  { "左薬指２", "Bip01_L_Finger31" }, { "左薬指３", "Bip01_L_Finger32" },
            { "左小指１", "Bip01_L_Finger4" },  { "左小指２", "Bip01_L_Finger41" }, { "左小指３", "Bip01_L_Finger42" },
            { "右親指０", "Bip01_R_Finger0" },  { "右親指１", "Bip01_R_Finger01" }, { "右親指２", "Bip01_R_Finger02" },
            { "右人指１", "Bip01_R_Finger1" },  { "右人指２", "Bip01_R_Finger11" }, { "右人指３", "Bip01_R_Finger12" },
            { "右中指１", "Bip01_R_Finger2" },  { "右中指２", "Bip01_R_Finger21" }, { "右中指３", "Bip01_R_Finger22" },
            { "右薬指１", "Bip01_R_Finger3" },  { "右薬指２", "Bip01_R_Finger31" }, { "右薬指３", "Bip01_R_Finger32" },
            { "右小指１", "Bip01_R_Finger4" },  { "右小指２", "Bip01_R_Finger41" }, { "右小指３", "Bip01_R_Finger42" },
        };

        /// <summary>The MMD bone whose world POSITION carries the whole body's root translation (センター → Bip01).</summary>
        public const string RootMmdBone = "センター";

        public static bool TryGetBip01(string mmdName, out string bip01) => ToBip01.TryGetValue(mmdName ?? "", out bip01);
    }
}
