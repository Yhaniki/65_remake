using System;
using UnityEngine;

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
        public float bgm = 0.5f;
        public float gameMusic = 0.5f;
        public float sfx = 0.5f;
    }

    /// <summary>
    /// Gameplay key bindings for the 4-key DDR lanes (order 0=Left 1=Down 2=Up 3=Right — matches
    /// ScreenGameplay.DefaultLaneKeys). Each lane has a primary (主鍵位) and an auxiliary (輔助鍵位) key,
    /// mirroring the original OPTIONDLG keyboard tab. Keys are stored as <see cref="KeyCode"/> enum NAMES
    /// (JsonUtility-friendly + hand-editable in settings.json). ScreenGameplay consumes <see cref="ToLaneKeys"/>.
    /// </summary>
    [Serializable]
    public class KeyBindSettings
    {
        public string[] lane4 = { "A", "S", "W", "D" };                                   // 主鍵位 (primary): A=Left S=Down W=Up D=Right
        public string[] lane4aux = { "LeftArrow", "DownArrow", "UpArrow", "RightArrow" };  // 輔助鍵位 (auxiliary): 方向鍵 上下左右

        public static readonly KeyCode[] DefaultPrimary = { KeyCode.A, KeyCode.S, KeyCode.W, KeyCode.D };
        public static readonly KeyCode[] DefaultAux = { KeyCode.LeftArrow, KeyCode.DownArrow, KeyCode.UpArrow, KeyCode.RightArrow };

        /// <summary>Per-lane key sets {primary, aux} for the gameplay input loop. Pure; falls back to defaults.</summary>
        public KeyCode[][] ToLaneKeys()
        {
            var res = new KeyCode[4][];
            for (int i = 0; i < 4; i++)
            {
                var p = ParseKey(At(lane4, i), DefaultPrimary[i]);
                var a = ParseKey(At(lane4aux, i), DefaultAux[i]);
                res[i] = new[] { p, a };
            }
            return res;
        }

        /// <summary>Coerce a 4-length name array to valid KeyCode names, filling gaps/invalid entries from
        /// <paramref name="def"/>. Pure (unit-testable) — used by DisplaySettingsManager.Sanitize.</summary>
        public static string[] SanitizeNames(string[] a, KeyCode[] def)
        {
            var res = new string[4];
            for (int i = 0; i < 4; i++)
            {
                var v = At(a, i);
                res[i] = (!string.IsNullOrEmpty(v) && Enum.TryParse<KeyCode>(v, out _)) ? v : def[i].ToString();
            }
            return res;
        }

        public static KeyCode ParseKey(string s, KeyCode fallback)
            => (!string.IsNullOrEmpty(s) && Enum.TryParse<KeyCode>(s, out var k)) ? k : fallback;

        private static string At(string[] a, int i) => (a != null && i < a.Length) ? a[i] : null;
    }

    /// <summary>Serializable user settings persisted to persistentDataPath/settings.json（畫面/音量/按鍵/語言，
    /// 屬「本機裝置」層級，不隨 user 走）。註：開房間面板的預設(速度/note/組隊/掉落/模式)不放這，改放 per-user 的
    /// config.ini（DATA/PROFILE/&lt;id&gt;/），見 <see cref="RoomConfig"/> / <see cref="ProfileManager"/>。</summary>
    [Serializable]
    public class GameSettings
    {
        public int schemaVersion = 1;
        public DisplaySettings display = new DisplaySettings();
        public VolumeSettings audio = new VolumeSettings();
        public KeyBindSettings keys = new KeyBindSettings();
        public string language = "zh-TW";
        public string updatedAt = "";
    }
}
