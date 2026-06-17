using System;

namespace Sdo.Settings
{
    [Serializable]
    public class DisplaySettings
    {
        public int width = 1280;
        public int height = 720;
        public string displayMode = "Windowed"; // Windowed | Fullscreen | Borderless
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

    /// <summary>Serializable user settings persisted to persistentDataPath/settings.json.</summary>
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
