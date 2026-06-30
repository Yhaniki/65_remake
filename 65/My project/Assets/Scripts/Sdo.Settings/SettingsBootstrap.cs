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
            RoomConfig.Load();   // 開房間面板預設：執行檔同層的 config.ini
        }
    }
}
