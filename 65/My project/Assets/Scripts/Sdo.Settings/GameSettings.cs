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
    /// OPTION「遊戲」頁（官方 OPTIONDLG 的 OptionGameWindow）的可調偏好，屬本機裝置層級，隨 <see cref="GameSettings"/>
    /// 存進 settings.json。有實際作用的：<see cref="fullscreenFill"/>（畫面填滿/黑邊，由 AspectController 套用）、
    /// <see cref="effectCharacter"/>／<see cref="effectScene"/>（人物/場景特效開關）、<see cref="cameraAuto"/>（自動導播/
    /// 固定鏡頭）、<see cref="panelOpacity"/>（note 面板不透明度＝ScreenGameplay.boardAlpha）。其餘（<see cref="bloom"/>／
    /// <see cref="notesPanelLeft"/>／<see cref="callCardInGame"/>）目前只保存狀態、忠實呈現官方選項，尚未接功能。
    /// </summary>
    [Serializable]
    public class GameplaySettings
    {
        public bool fullscreenFill = false;  // 遊戲畫面：true=全屏(填滿) / false=視窗化(左右黑邊 pillarbox)。預設窗口
        public bool bloom = true;            // 全屏泛光效果（預設開；暫未接功能）
        public bool notesPanelLeft = true;   // notes 面板位置：true=左(預設) / false=中（暫未接功能）
        public bool effectCharacter = true;  // 遊戲特效「人物特效」：每 100 combo 的 100/200/300 COMBO.EFT
        public bool effectScene = true;      // 遊戲特效「場景特效」：場景常駐背景 EFT（魔法陣/雪/極光/發光…）
        public bool cameraAuto = true;       // 遊戲視角：true=默認(自動導播) / false=固定(鏡頭 1)
        public bool callCardInGame = true;   // 呼叫卡遊戲中顯示（預設開；暫未接功能）
        public float panelOpacity = 1.4f;    // 面板透明度：note 面板 alpha 倍率(=boardAlpha)，範圍 0..1.6（1.4=官方）

        public const float MaxPanelOpacity = 1.6f;   // 官方滑桿最高 1.6X
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
        public GameplaySettings gameplay = new GameplaySettings();   // OPTION「遊戲」頁偏好
        public string language = "zh-TW";
        public string updatedAt = "";
    }
}
