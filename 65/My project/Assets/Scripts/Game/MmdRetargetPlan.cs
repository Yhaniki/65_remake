using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>每根 MMD 骨頭在 retarget 時是「怎麼被驅動的」。</summary>
    public enum MmdDriveMode
    {
        /// <summary>沒有對應的 SDO 骨 —— 跟著父骨走(保持 rest)。中間骨(グルーブ/腰)、物理骨都是這種。</summary>
        Unmapped = 0,
        /// <summary>指向:把這根骨轉到「它的 SDO 對應骨指向子骨」的那個方向。免疫 T-pose/A-pose 差異。</summary>
        Aim,
        /// <summary>世界差量:直接抄 SDO 骨的絕對朝向。頭要用這個才會保持抬頭。</summary>
        WorldDelta,
        /// <summary>有對應但沒有可指的子骨(手掌/指尖)——跟著父骨,比世界差量穩。</summary>
        FollowParent,
    }

    public struct MmdBoneDrive
    {
        public int Hrc;              // 驅動這根骨的 HRC 骨 index,-1 = 沒有
        public MmdDriveMode Mode;
        public int AimChildHrc;      // Aim 專用:瞄準的 HRC 子骨
        public Vector3 AimRestDir;   // Aim 專用:MMD rest 的 骨→子骨 方向(已正規化)
    }

    /// <summary>
    /// 決定每根 MMD 骨頭要用哪種方式被 SDO 動作驅動 —— <see cref="MmdAvatar"/> 每幀套用的那張表,抽出來成純函式,
    /// 因為「哪根骨用哪個模式」正是腳會滑、會抖的地方,需要能被測試釘住。
    ///
    /// 兩條規則是踩過坑才長出來的,別動:
    ///
    /// ① <b>センター 只吃平移,不吃旋轉</b>(除非骨盆沒對應)。SDO 的 Bip01 長在骨盆上(零長度 root,所以 BuildAim
    ///    會把它判成退化),它自轉不會移動任何東西;MMD 的 センター→グルーブ→腰→下半身 之間有真實骨長,同一個
    ///    自轉會把整個下半身連同雙腳掃出一段動作裡根本沒有的弧。W_005663(初音)實測:下半身 每幀移動 1.52,
    ///    而 SDO 骨盆是 0.48;支撐腳的水平滑動是 SDO 的 2.7 倍。拿掉之後 下半身 回到 0.48、滑動剩 1.4 倍。
    ///    不會失去轉身,因為 下半身/上半身 都是 Aim,世界朝向是絕對的、跟父骨無關。
    ///
    /// ② <b>足首 用 WorldDelta,不是 Aim 也不是 FollowParent</b>。腳掌的正確判準是「這根骨從自己的 rest 轉了多少」——
    ///    兩具骨架的 rest 都是站直、腳底貼地,所以照抄相對旋轉,腳底就會做出動作要它做的事。三種模式在 W_005663
    ///    上量同一個東西(MMD 腳掌相對 rest 的旋轉 vs SDO 腳掌相對 bind 的旋轉,差幾度):
    ///
    ///      FollowParent(原本) 平均 16.5°、最多 46.1°  ← 腳掌硬跟小腿轉,腳底永遠擺不平
    ///      Aim(瞄 つま先)      平均  9.5°、最多  9.9°  ← 剩一個常數偏移:兩邊 ankle→toe 向量本來就差 8°
    ///      WorldDelta          平均  0.0°、最多  0.1°
    ///
    ///    手腕/手指不能比照辦理 —— 那裡 rest 姿勢的語義真的不同(SDO 是 T-pose、MMD 是 A-pose),世界差量會把手轉爛,
    ///    所以它們留在 FollowParent。腳沒有這個問題:站姿就是站姿。
    /// </summary>
    public static class MmdRetargetPlan
    {
        private static readonly string[] AnkleBones = { "Bip01_L_Foot", "Bip01_R_Foot" };


        /// <param name="mmdNames">PMX 骨頭的日文名,依 PMX 順序。</param>
        /// <param name="mmdPos">PMX 骨頭的 rest 位置(模型空間),與 <paramref name="mmdNames"/> 同索引。</param>
        /// <param name="hrcNames">HRC 骨名。</param>
        /// <param name="hrcParent">HRC 每根骨的父 index(-1 = 根)。</param>
        /// <param name="hrcBindPos">HRC bind pose 的世界位置。</param>
        public static MmdBoneDrive[] Build(IList<string> mmdNames, IList<Vector3> mmdPos,
                                           IList<string> hrcNames, IList<int> hrcParent, IList<Vector3> hrcBindPos)
        {
            int bc = mmdNames == null ? 0 : mmdNames.Count;
            var drive = new MmdBoneDrive[bc];
            for (int i = 0; i < bc; i++) { drive[i].Hrc = -1; drive[i].AimChildHrc = -1; }
            if (bc == 0 || hrcNames == null || hrcNames.Count == 0) return drive;

            var hrcIndex = new Dictionary<string, int>();
            for (int i = 0; i < hrcNames.Count; i++) if (!hrcIndex.ContainsKey(hrcNames[i])) hrcIndex[hrcNames[i]] = i;

            var bip01ToMmd = new Dictionary<string, int>();
            int rootBone = -1;
            for (int i = 0; i < bc; i++)
            {
                if (MmdBoneMap.TryGetBip01(mmdNames[i], out string bip01))
                {
                    if (!bip01ToMmd.ContainsKey(bip01)) bip01ToMmd[bip01] = i;
                    if (hrcIndex.TryGetValue(bip01, out int h)) drive[i].Hrc = h;
                }
                if (mmdNames[i] == MmdBoneMap.RootMmdBone && rootBone < 0) rootBone = i;
            }

            // ---- Aim:只沿同一條語意骨鏈前進,不能拿「第一個 mapped sibling」猜 ----
            // FEMALE.HRC 的 Spine children 依檔案順序是 R_Thigh/L_Thigh/Spine1,Neck 則是
            // R_Clavicle/L_Clavicle/Head；用第一個會讓上半身瞄右腿、頸部瞄右肩。
            for (int i = 0; i < bc; i++)
            {
                int h = drive[i].Hrc;
                if (h < 0 || h >= hrcNames.Count || !MmdBoneMap.TryGetAimChild(hrcNames[h], out string childName)) continue;
                if (!hrcIndex.TryGetValue(childName, out int hrcChild) || hrcChild < 0 || hrcChild >= hrcParent.Count) continue;
                if (hrcParent[hrcChild] != h || !bip01ToMmd.TryGetValue(childName, out int mmdChild)) continue;
                if (hrcChild >= hrcBindPos.Count || h >= hrcBindPos.Count) continue;
                Vector3 rd = mmdPos[mmdChild] - mmdPos[i];               // MMD rest 方向
                Vector3 hd = hrcBindPos[hrcChild] - hrcBindPos[h];       // SDO bind 方向
                if (rd.sqrMagnitude < 1e-6f || hd.sqrMagnitude < 1e-6f) continue;   // 退化(Bip01→Pelvis 重疊)→ 不 aim
                drive[i].Mode = MmdDriveMode.Aim;
                drive[i].AimChildHrc = hrcChild;
                drive[i].AimRestDir = rd.normalized;
            }

            // ---- 絕對朝向:頭一定要;センター 只有在骨盆沒被驅動時才要(規則①) ----
            if (bip01ToMmd.TryGetValue("Bip01_Head", out int headMmd) && drive[headMmd].Hrc >= 0)
            {
                drive[headMmd].Mode = MmdDriveMode.WorldDelta;
                drive[headMmd].AimChildHrc = -1;
                drive[headMmd].AimRestDir = Vector3.zero;
            }
            // 腳掌也是絕對的(規則②) —— 這裡要覆寫,不是「沒別的辦法才用」。
            foreach (string ankle in AnkleBones)
                if (bip01ToMmd.TryGetValue(ankle, out int ankleMmd) && drive[ankleMmd].Hrc >= 0)
                    drive[ankleMmd].Mode = MmdDriveMode.WorldDelta;
            bool pelvisDriven = bip01ToMmd.TryGetValue("Bip01_Pelvis", out int pelvisMmd) && drive[pelvisMmd].Hrc >= 0;
            if (rootBone >= 0 && !pelvisDriven && drive[rootBone].Mode != MmdDriveMode.Aim)
                drive[rootBone].Mode = MmdDriveMode.WorldDelta;

            // ---- 剩下「有對應但沒得瞄」的(手掌/指尖/腳尖):跟父骨 ----
            for (int i = 0; i < bc; i++)
                if (drive[i].Hrc >= 0 && drive[i].Mode == MmdDriveMode.Unmapped)
                    drive[i].Mode = MmdDriveMode.FollowParent;

            return drive;
        }
    }
}
