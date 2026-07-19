using UnityEngine;

namespace Sdo.Settings
{
    /// <summary>Loads + applies the persisted display settings before the first scene renders.</summary>
    public static class SettingsBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot()
        {
            ProfileManager.Boot();   // 先解析/建立 DATA/PROFILE（active 使用者+資料夾）—— settings.json 也放這層，故要先跑
            DisplaySettingsManager.Load();   // 讀 DATA/PROFILE/settings.json（舊 persistentDataPath 的會一次性遷移進來）
            DisplaySettingsManager.ApplyDisplay();
            RoomConfig.Load();       // 開房間面板預設 + OPTION 設定：DATA/PROFILE/ 下的共用 config.ini（可能覆蓋 OPTION 值）
            DisplaySettingsManager.ApplyDisplay();   // config.ini 的 [Option] 若覆蓋了視窗大小/顯示模式/vsync → 再套用一次
        }
    }
}
