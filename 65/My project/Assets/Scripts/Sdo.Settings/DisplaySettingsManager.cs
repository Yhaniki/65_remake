using System;
using System.IO;
using UnityEngine;

namespace Sdo.Settings
{
    /// <summary>
    /// <see cref="GameSettings"/> 的**執行期工作副本**（畫面/音量/按鍵/遊戲頁），以及套用顯示設定
    /// （Screen.SetResolution）。**它自己不再有檔案**：值落地在 <see cref="RoomConfig"/> 的 config.ini
    /// （<c>[Option]</c>）與 <see cref="KeyMap"/> 的 keymaps.ini（鍵位），<see cref="Load"/>／<see cref="Save"/>
    /// 只是把那兩份組進來／寫回去。舊的 settings.json 由 <see cref="RoomConfig.Load"/> 一次性併入後刪除
    /// （<see cref="ReadLegacyJson"/> / <see cref="DeleteLegacyJson"/>）。
    /// 夾值（<see cref="Sanitize"/>）是純函式可單元測試；只有 <see cref="ApplyDisplay"/> 碰 Unity 的 Screen/QualitySettings。
    /// </summary>
    public static class DisplaySettingsManager
    {
        public static GameSettings Settings { get; private set; } = new GameSettings();
        public static event Action SettingsChanged;

        public const string LegacyFileName = "settings.json";

        // 舊 settings.json 的位置＝存檔層 DATA/PROFILE/（更早的 persistentDataPath 版本，上一版開機時就已經搬到這裡了）。
        // 只用於一次性搬遷進 config.ini。
        private static string LegacyProfilePath => Path.Combine(ProfileManager.Root, LegacyFileName);

        /// <summary>把 config.ini 的 <c>[Option]</c> + keymaps.ini 的鍵位組成執行期的 <see cref="Settings"/>。
        /// **必須在 <see cref="RoomConfig.Load"/> 與 <see cref="KeyMap.Load"/> 之後**呼叫（見 <see cref="SettingsBootstrap"/>）。</summary>
        public static void Load()
        {
            var s = new GameSettings();
            if (RoomConfig.hasOption) RoomConfig.ApplyOptionTo(s);   // 沒有 [Option]（不該發生，Load 會補）→ 就用內建預設
            KeyMap.ApplyTo(s.keys);
            Settings = Sanitize(s);
        }

        /// <summary>把目前的 <see cref="Settings"/> 寫回落地檔：<c>[Option]</c> 進 config.ini、鍵位進 keymaps.ini。</summary>
        public static void Save()
        {
            Settings = Sanitize(Settings);
            Settings.updatedAt = DateTime.UtcNow.ToString("o");
            RoomConfig.CaptureOptionFrom(Settings);
            RoomConfig.Save();
            KeyMap.CaptureFrom(Settings.keys);
            KeyMap.Save();
            SettingsChanged?.Invoke();
        }

        /// <summary>讀舊的 DATA/PROFILE/settings.json；沒有/壞掉 → null。只給 <see cref="RoomConfig.Load"/> 做一次性搬遷用。</summary>
        public static GameSettings ReadLegacyJson()
        {
            try
            {
                var path = LegacyProfilePath;
                if (!File.Exists(path)) return null;
                var s = JsonUtility.FromJson<GameSettings>(File.ReadAllText(path));
                return s == null ? null : Sanitize(s);
            }
            catch (Exception e) { Debug.LogWarning($"[Settings] legacy read failed: {e.Message}"); return null; }
        }

        /// <summary>刪掉舊的 settings.json（內容已併進 config.ini 的 [Option] 才呼叫）。</summary>
        public static void DeleteLegacyJson()
        {
            try
            {
                var path = LegacyProfilePath;
                if (!File.Exists(path)) return;
                File.Delete(path);
                Debug.Log($"[Settings] merged into config.ini, removed {path}");
            }
            catch (Exception e) { Debug.LogWarning($"[Settings] legacy delete failed: {e.Message}"); }
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
            if (string.IsNullOrEmpty(s.display.displayMode)) s.display.displayMode = "Windowed";

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
