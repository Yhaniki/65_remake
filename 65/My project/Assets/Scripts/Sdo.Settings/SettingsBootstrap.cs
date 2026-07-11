using UnityEngine;

namespace Sdo.Settings
{
    /// <summary>Loads + applies the persisted display settings before the first scene renders.</summary>
    public static class SettingsBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot()
        {
            DisplaySettingsManager.Load();
            DisplaySettingsManager.ApplyDisplay();
            ProfileManager.Boot();   // 解析/建立 active 使用者(DATA/PROFILE)，載入其收藏 —— 必須在 RoomConfig 之前
            RoomConfig.Load();       // 開房間面板預設 + OPTION 設定：active 使用者資料夾下的 config.ini（可能覆蓋 OPTION 值）
            DisplaySettingsManager.ApplyDisplay();   // config.ini 的 [Option] 若覆蓋了視窗大小/顯示模式/vsync → 再套用一次
        }
    }
}
