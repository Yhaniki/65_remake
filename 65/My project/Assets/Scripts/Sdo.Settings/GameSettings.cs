using System;

namespace Sdo.Settings
{
    [Serializable]
    public class DisplaySettings
    {
        public int width = 1024;                // windowed size (4:3, matches the 800×600 design aspect)
        public int height = 768;
        public string displayMode = "Borderless"; // 預設全螢幕視窗化。Windowed | Fullscreen | Borderless
        public bool vsync = true;
        public float uiScale = 1f;              // 1.0 / 1.25 / 1.5
    }

    [Serializable]
    public class VolumeSettings
    {
        public float bgm = 0.8f;
        public float gameMusic = 0.9f;
        public float sfx = 1f;
    }

    /// <summary>Serializable user settings persisted to persistentDataPath/settings.json.
    /// 註：開房間面板的預設(速度/note/組隊/掉落/模式)不放這，改放執行檔同層的 config.ini，見 <see cref="RoomConfig"/>。</summary>
    [Serializable]
    public class GameSettings
    {
        public int schemaVersion = 1;
        public DisplaySettings display = new DisplaySettings();
        public VolumeSettings audio = new VolumeSettings();
        public string language = "zh-TW";
        public string updatedAt = "";
    }
}
