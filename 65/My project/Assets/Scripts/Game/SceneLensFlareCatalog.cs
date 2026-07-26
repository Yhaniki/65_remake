using System;

namespace Sdo.Game
{
    /// <summary>
    /// 哪些場景有太陽鏡頭光斑。全遊戲只有 SCN0004 海灘 —— 官方在 Scene_LoadBackground 的 case 4 裡
    /// (StageScene_InitPlacementAndNode_004b4280) new 出一個 LensFlare 節點，其他 case 都沒有。
    /// 寫成表而不是在 ScreenGameplay 裡硬編字串，和其他場景目錄一致。
    /// </summary>
    public static class SceneLensFlareCatalog
    {
        private static readonly string[] Folders = { "SCN0004" };

        public static bool Has(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            foreach (var f in Folders)
                if (string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
