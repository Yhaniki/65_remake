using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sdo.Settings
{
    /// <summary>
    /// 開房間右側面板的可選清單與預設值，存成「執行檔同一層」的 <c>config.ini</c>（不是 AppData）。
    /// 純文字、好手改：第一次跑會自動寫一份附註解的範本；之後讀檔覆蓋預設。解析/夾值是純函式可單元測試
    /// （<see cref="ParseInto"/> / <see cref="Sanitize"/> 不碰檔案）。
    /// </summary>
    public static class RoomConfig
    {
        // ---- 當下生效的值（欄位＝INI 的 key）----
        public static float[] speedSteps = { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 4.0f, 5.0f, 6.0f, 8.0f };
        public static float defaultSpeed = 2.5f;     // 預設速度（會對齊到 speedSteps 最近檔位）
        public static int defaultNoteType = -1;      // note 種類(hit-effect)：-1=隨機
        public static int defaultTeam = 3;           // 組隊：0=A,1=B,2=C,3=自由
        public static int defaultDropDirection = 0;  // 掉落方式：0=向上,1=向下
        public static int defaultGameMode = 0;       // 模式：0=自由模式,1=普通模式,2=情侶模式

        public const string FileName = "config.ini";

        /// <summary>config.ini 的完整路徑：執行檔同一層（Editor 下 = 專案根「My project/」）。</summary>
        public static string FilePath
        {
            get
            {
                // 建置版 Application.dataPath = "<exe 同層>/<Product>_Data" → 其上一層就是 exe 所在資料夾。
                // Editor 下 dataPath = ".../My project/Assets" → 上一層 = "My project"（開發放這方便）。
                string dir;
                try { dir = Directory.GetParent(Application.dataPath).FullName; }
                catch { dir = Application.dataPath; }
                return Path.Combine(dir, FileName);
            }
        }

        /// <summary>讀 config.ini（不存在就用內建預設並寫一份範本）。開機時呼叫一次。</summary>
        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    ParseInto(File.ReadAllText(FilePath));
                    Sanitize();
                }
                else
                {
                    Sanitize();
                    Save();   // 第一次：留一份可編輯的範本在 exe 旁
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RoomConfig] load failed, using defaults: {e.Message}");
                Sanitize();
            }
        }

        /// <summary>把目前的值寫回 config.ini（附中文註解）。</summary>
        public static void Save()
        {
            try
            {
                File.WriteAllText(FilePath, Serialize(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogError($"[RoomConfig] save failed: {e.Message}");
            }
        }

        /// <summary>把一份 INI 文字解析進靜態欄位（純函式：不碰檔案）。未出現的 key 保留原值。</summary>
        public static void ParseInto(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "speedSteps": speedSteps = ParseFloatList(val); break;
                    case "defaultSpeed": defaultSpeed = ParseFloat(val, defaultSpeed); break;
                    case "defaultNoteType": defaultNoteType = ParseInt(val, defaultNoteType); break;
                    case "defaultTeam": defaultTeam = ParseInt(val, defaultTeam); break;
                    case "defaultDropDirection": defaultDropDirection = ParseInt(val, defaultDropDirection); break;
                    case "defaultGameMode": defaultGameMode = ParseInt(val, defaultGameMode); break;
                }
            }
        }

        /// <summary>夾正非法值（空/壞的 speedSteps 回退內建；其餘夾範圍）。純函式。</summary>
        public static void Sanitize()
        {
            if (speedSteps == null || speedSteps.Length == 0)
                speedSteps = new[] { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 4.0f, 5.0f, 6.0f, 8.0f };
            if (defaultSpeed <= 0f) defaultSpeed = 2.5f;
            if (defaultNoteType < -1) defaultNoteType = -1;
            defaultTeam = Mathf.Clamp(defaultTeam, 0, 3);
            defaultDropDirection = Mathf.Clamp(defaultDropDirection, 0, 1);
            defaultGameMode = Mathf.Clamp(defaultGameMode, 0, 2);
        }

        /// <summary>輸出帶註解的 INI 文字（純函式）。</summary>
        public static string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append("# 開房間右側面板預設設定 — 放在執行檔同一層，純文字可手改。\n");
            sb.Append("# 改完存檔，下次開遊戲生效。\n");
            sb.Append("[Room]\n");
            sb.Append("# 速度可選清單（逗號分隔，要加/減檔位直接改）\n");
            sb.Append("speedSteps=").Append(FloatListToString(speedSteps)).Append('\n');
            sb.Append("# 預設速度（會對齊到上面最接近的檔位）\n");
            sb.Append("defaultSpeed=").Append(defaultSpeed.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("# 預設 note 種類(hit-effect)：-1=隨機，>=0=指定第幾種\n");
            sb.Append("defaultNoteType=").Append(defaultNoteType).Append('\n');
            sb.Append("# 預設組隊：0=A 1=B 2=C 3=自由\n");
            sb.Append("defaultTeam=").Append(defaultTeam).Append('\n');
            sb.Append("# 預設掉落方式：0=向上 1=向下\n");
            sb.Append("defaultDropDirection=").Append(defaultDropDirection).Append('\n');
            sb.Append("# 預設模式：0=自由模式 1=普通模式 2=情侶模式\n");
            sb.Append("defaultGameMode=").Append(defaultGameMode).Append('\n');
            return sb.ToString();
        }

        // ---- small parse helpers ----
        private static float[] ParseFloatList(string s)
        {
            var parts = s.Split(',');
            var list = new System.Collections.Generic.List<float>(parts.Length);
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length == 0) continue;
                if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) list.Add(f);
            }
            return list.ToArray();
        }

        private static string FloatListToString(float[] a)
        {
            if (a == null || a.Length == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < a.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(a[i].ToString("0.0##", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static float ParseFloat(string s, float fallback)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;

        private static int ParseInt(string s, int fallback)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    }
}
