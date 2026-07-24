using System;
using System.IO;

namespace Sdo.Game
{
    /// <summary>
    /// 「歌曲外掛包」的動作外掛（overlay）機制。一個 SDO 歌包（如 <c>… [NX] Patch …/patch Datas</c>）把它自帶的
    /// 編舞（<c>.dps</c>）和它引用的動作片段（<c>.mot</c>）用**跟遊戲資料根一模一樣的樹狀結構**擺在一起：
    /// <code>
    ///   &lt;tree&gt;/DANCE/&lt;id&gt;.DPS      ← 這首歌的編舞
    ///   &lt;tree&gt;/MOTION/*.MOT          ← 編舞裡點名的一般舞步（wdanceNNNN.mot）
    ///   &lt;tree&gt;/AUMOTION/*.MOT        ← 官方編舞用的特殊片段（W_00xxxx.MOT），常有 base 根裡沒有的
    /// </code>
    /// 所以「這首歌的 <c>.dps</c> 是從哪棵樹讀出來的，它的 <c>.mot</c> 就在那棵樹裡」。當那棵樹**不是**遊戲資料根
    /// （<c>SdoExtracted.Root</c>）時，就把它當這首歌的 overlay：查動作片段時 overlay 先贏、找不到才退回 base 根
    /// （見 <c>ScreenGameplay.ResolveMot</c>）。這樣一個包只要自帶 DANCE + MOTION/AUMOTION 就是自足的插件，
    /// 不必再從 <see cref="Sdo.Osu.ExternalSong"/> 一路把路徑穿過歌單／GameSession —— dps 路徑本身就編碼了那棵樹。
    ///
    /// 純字串/路徑運算，不碰磁碟（<see cref="Path.GetFullPath"/>/<see cref="DirectoryInfo.Name"/> 只正規化路徑、
    /// 不要求檔案存在），因此可單元測試（見 MotionOverlayTests）。
    /// </summary>
    public static class MotionOverlay
    {
        /// <summary>
        /// 一個已解析的 <c>.dps</c> 完整路徑所隱含的 overlay 動作樹，沒有就回 <c>""</c>。回 <c>""</c> 的情形：
        /// dps 不在某個 <c>DANCE/</c> 資料夾裡（例如外部 osu/SM 歌臨時生成、直接躺在歌資料夾的 <c>.dps</c>）、
        /// 或它那棵樹**就是** base 資料根（官方歌 → 用 base 動作，overlay 只會重複探同一批檔）。
        /// </summary>
        /// <param name="resolvedDpsPath">已解析成可讀取的 <c>.dps</c> 路徑（絕對，允許夾帶 <c>..</c>）。</param>
        /// <param name="baseRoot">遊戲資料根（<c>SdoExtracted.Root</c>）。</param>
        public static string RootForDps(string resolvedDpsPath, string baseRoot)
        {
            if (string.IsNullOrEmpty(resolvedDpsPath)) return "";
            try
            {
                string danceDir = Path.GetDirectoryName(resolvedDpsPath);            // <tree>/DANCE
                if (string.IsNullOrEmpty(danceDir)) return "";
                // 只有真的擺在 DANCE/ 下的 dps 才有「隔壁就是 MOTION/」的約定。DirectoryInfo.Name 取最後一段，
                // 不解 ".."，所以 "…/patch music/../patch Datas/DANCE" 也能正確認出是 "DANCE"。
                if (!string.Equals(new DirectoryInfo(danceDir).Name, "DANCE", StringComparison.OrdinalIgnoreCase))
                    return "";
                string tree = Path.GetDirectoryName(danceDir);                       // <tree>
                if (string.IsNullOrEmpty(tree)) return "";
                if (SamePath(tree, baseRoot)) return "";                             // base 的 dps → base 動作，不算 overlay
                return Path.GetFullPath(tree);                                       // 正規化（收掉 ".."）後回傳
            }
            catch { return ""; }
        }

        /// <summary>兩個路徑指向同一個資料夾嗎（正規化 + 去尾分隔符 + 不分大小寫）。任一無法正規化就當「不同」。</summary>
        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string Norm(string p)
            => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
