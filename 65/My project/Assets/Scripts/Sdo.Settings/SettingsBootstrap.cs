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

            ProfileManager.Boot();   // 先解析/建立 DATA/PROFILE（active 使用者+資料夾）—— settings.json 也放這層，故要先跑
            DisplaySettingsManager.Load();   // 讀 DATA/PROFILE/settings.json（舊 persistentDataPath 的會一次性遷移進來）
            DisplaySettingsManager.ApplyDisplay();
            RoomConfig.Load();       // 開房間面板預設 + OPTION 設定：執行檔同層的全域 config.ini（不跟著使用者；可能覆蓋 OPTION 值）
            DisplaySettingsManager.ApplyDisplay();   // config.ini 的 [Option] 若覆蓋了視窗大小/顯示模式/vsync → 再套用一次
        }
    }
}
