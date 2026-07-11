using System;
using System.IO;
using UnityEngine;

namespace Sdo.Settings
{
    /// <summary>
    /// Loads/saves <see cref="GameSettings"/> (persistentDataPath/settings.json) and applies the
    /// display settings via Screen.SetResolution. Serialization/clamping is pure and unit-testable;
    /// only <see cref="ApplyDisplay"/> touches Unity's Screen/QualitySettings.
    /// </summary>
    public static class DisplaySettingsManager
    {
        public static GameSettings Settings { get; private set; } = new GameSettings();
        public static event Action SettingsChanged;

        // settings.json 放在 DATA/PROFILE 底下（跟 active.txt 同一層；全域設定，非 per-user，不進 <id> 子資料夾），
        // 讓所有存檔集中在專案 DATA 夾（可隨 exe 搬機），不再散落在 Unity 的 persistentDataPath。
        private static string FilePath => Path.Combine(ProfileManager.Root, "settings.json");
        // 舊位置（persistentDataPath/settings.json）：保留供一次性遷移。
        private static string LegacyFilePath => Path.Combine(Application.persistentDataPath, "settings.json");

        public static void Load()
        {
            try
            {
                // 新位置優先；沒有就從舊的 persistentDataPath 讀進來並一次性遷移（不刪舊檔）。
                string path = File.Exists(FilePath) ? FilePath
                            : (File.Exists(LegacyFilePath) ? LegacyFilePath : null);
                if (path != null)
                {
                    var json = File.ReadAllText(path);
                    var s = JsonUtility.FromJson<GameSettings>(json);
                    Settings = Sanitize(s ?? new GameSettings());
                    if (path == LegacyFilePath) Save();   // 遷移：把舊設定寫到 DATA/PROFILE/settings.json
                }
                else
                {
                    Settings = new GameSettings();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Settings] load failed, using defaults: {e.Message}");
                Settings = new GameSettings();
            }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(ProfileManager.Root);   // DATA/PROFILE 可能尚未建立（Boot 之前存檔時）
                Settings.updatedAt = DateTime.UtcNow.ToString("o");
                var json = JsonUtility.ToJson(Settings, true);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Settings] save failed: {e.Message}");
            }
            SettingsChanged?.Invoke();
        }

        /// <summary>Repair/clamp a (possibly partial or corrupt) settings object into a valid one. Pure.</summary>
        public static GameSettings Sanitize(GameSettings s)
        {
            s ??= new GameSettings();
            s.display ??= new DisplaySettings();
            s.audio ??= new VolumeSettings();
            s.keys ??= new KeyBindSettings();
            s.keys.lane4 = KeyBindSettings.SanitizeNames(s.keys.lane4, KeyBindSettings.DefaultPrimary);
            s.keys.lane4aux = KeyBindSettings.SanitizeNames(s.keys.lane4aux, KeyBindSettings.DefaultAux);

            var c = ResolutionPreset.Clamp(s.display.width, s.display.height);
            s.display.width = c.Width;
            s.display.height = c.Height;
            if (s.display.uiScale <= 0f) s.display.uiScale = 1f;
            s.display.uiScale = Mathf.Clamp(s.display.uiScale, 0.5f, 3f);
            if (string.IsNullOrEmpty(s.display.displayMode)) s.display.displayMode = "Borderless";

            s.audio.bgm = Mathf.Clamp01(s.audio.bgm);
            s.audio.gameMusic = Mathf.Clamp01(s.audio.gameMusic);
            s.audio.sfx = Mathf.Clamp01(s.audio.sfx);

            s.gameplay ??= new GameplaySettings();
            s.gameplay.panelOpacity = Mathf.Clamp(s.gameplay.panelOpacity, 0f, GameplaySettings.MaxPanelOpacity);

            if (string.IsNullOrEmpty(s.language)) s.language = "zh-TW";
            return s;
        }

        public static FullScreenMode ToMode(string m) => m switch
        {
            "Fullscreen" => FullScreenMode.ExclusiveFullScreen,
            "Borderless" => FullScreenMode.FullScreenWindow,
            _ => FullScreenMode.Windowed,
        };

        public static string FromMode(FullScreenMode m) => m switch
        {
            FullScreenMode.ExclusiveFullScreen => "Fullscreen",
            FullScreenMode.FullScreenWindow => "Borderless",
            FullScreenMode.MaximizedWindow => "Borderless",
            _ => "Windowed",
        };

        public static void ApplyDisplay()
        {
            var d = Settings.display;
            var mode = ToMode(d.displayMode);
            if (mode == FullScreenMode.FullScreenWindow || mode == FullScreenMode.ExclusiveFullScreen)
            {
                // Fullscreen / borderless: use the NATIVE desktop resolution so the 4:3 frame (AspectController)
                // stretches to fill the whole display. The stored width/height only applies to Windowed mode.
                var r = Screen.currentResolution;
                Screen.SetResolution(r.width, r.height, mode);
            }
            else
            {
                Screen.SetResolution(d.width, d.height, mode);
            }
            QualitySettings.vSyncCount = d.vsync ? 1 : 0;
            SettingsChanged?.Invoke();
        }
    }
}
