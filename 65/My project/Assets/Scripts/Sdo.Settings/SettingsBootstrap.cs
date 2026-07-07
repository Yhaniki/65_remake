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
            RoomConfig.Load();       // 開房間面板預設：改成 active 使用者資料夾下的 config.ini
        }
    }
}
