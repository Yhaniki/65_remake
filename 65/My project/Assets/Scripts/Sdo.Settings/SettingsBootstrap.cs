using UnityEngine;

namespace Sdo.Settings
{
    /// <summary>Loads + applies the persisted display settings before the first scene renders.</summary>
    public static class SettingsBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot()
        {
            // 開機第一行就把實際解析到的 data root 印出來（含 SDO_DATA_ROOT / data_root.txt 覆寫的結果）——
            // 「資產/存檔到底讀哪一棵樹」以前只能靠猜。
            Debug.Log($"[DataRoot] {SdoDataRoot.Root}  (profiles: {SdoDataRoot.ProfileDir})");

            // 順序有相依：config.ini 是總表（[Profile] activeId + [Room] + [Option]），先讀它；keymaps.ini 缺檔時要靠
            // config.ini 的舊 opt_keys 種一份；GameSettings 工作副本由前兩者組成；ProfileManager 的 active 使用者
            // 讀的是 config.ini 的 activeId。
            RoomConfig.Load();               // DATA/PROFILE/config.ini（並一次性併入舊 settings.json / active.txt / 舊位置 config.ini）
            KeyMap.Load();                   // DATA/PROFILE/keymaps.ini（4 鍵鍵位 + 遊玩功能鍵）
            DisplaySettingsManager.Load();   // [Option] + 鍵位 → 執行期 GameSettings
            ProfileManager.Boot();           // 解析/建立 DATA/PROFILE（active 使用者+資料夾）
            DisplaySettingsManager.ApplyDisplay();
        }
    }
}
