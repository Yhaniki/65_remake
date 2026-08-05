using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Sdo.Settings
{
    /// <summary>
    /// <c>PlayerPrefs</c> 的替代品，寫在 <c>DATA/PROFILE/prefs.ini</c>。
    ///
    /// 換掉 PlayerPrefs 的理由只有一個：**它在 Windows 上寫的是登錄檔**
    /// （<c>HKCU\Software\&lt;company&gt;\&lt;product&gt;</c>），那在遊戲資料夾外面。整包搬到別台機器會掉、
    /// 反安裝清不掉、玩家也完全看不到。規則是「build 產生的東西不准落在 build 資料夾之外」，
    /// 見 <c>docs/architecture/data-packaging.md</c> §1.1。
    ///
    /// 檔案格式跟專案其它 ini 一致：<c>key=value</c>、<c>#</c> 或 <c>;</c> 開頭是註解、UTF-8 **不帶 BOM**、
    /// **LF 換行**。（BOM + CRLF 讓設定默默失效已經踩過一次。）
    ///
    /// API 刻意跟 PlayerPrefs 一模一樣，呼叫端只要改型別名。差別有兩點：
    /// <list type="bullet">
    /// <item>浮點數一律用 <see cref="CultureInfo.InvariantCulture"/> 讀寫 —— 不然在逗號當小數點的
    /// 地區設定下，存進去的 <c>1.25</c> 讀回來會變成 <c>125</c>。</item>
    /// <item><see cref="Save"/> 才會真的落地。跟 PlayerPrefs 一樣：拖滑桿不該每幀寫檔。</item>
    /// </list>
    /// </summary>
    public static class LocalPrefs
    {
        /// <summary>檔名（<see cref="SdoDataRoot.ProfileDir"/> 底下）。</summary>
        public const string FileName = "prefs.ini";

        private static readonly object Gate = new object();
        private static Dictionary<string, string> _values;
        private static bool _dirty;
        private static string _pathOverride;

        /// <summary>prefs.ini 的絕對路徑。</summary>
        public static string FilePath
        {
            get
            {
                if (_pathOverride != null) return _pathOverride;
                try { return Path.Combine(SdoDataRoot.ProfileDir, FileName); }
                catch { return FileName; }
            }
        }

        /// <summary>把讀寫導到別的檔案並清掉已載入的內容 —— 測試與工具用。傳 null 還原成預設位置。</summary>
        public static void OverridePath(string path)
        {
            lock (Gate)
            {
                _pathOverride = path;
                _values = null;
                _dirty = false;
            }
        }

        // ---------------- 讀 ----------------

        public static bool HasKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (Gate) { return Load().ContainsKey(key); }
        }

        public static string GetString(string key, string defaultValue = "")
        {
            string raw = Raw(key);
            return raw ?? defaultValue;
        }

        public static int GetInt(string key, int defaultValue = 0)
        {
            string raw = Raw(key);
            int v;
            return raw != null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)
                ? v : defaultValue;
        }

        public static float GetFloat(string key, float defaultValue = 0f)
        {
            string raw = Raw(key);
            float v;
            return raw != null && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                ? v : defaultValue;
        }

        public static bool GetBool(string key, bool defaultValue = false)
        {
            string raw = Raw(key);
            if (raw == null) return defaultValue;
            raw = raw.Trim();
            if (raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (raw == "0" || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return defaultValue;
        }

        private static string Raw(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (Gate)
            {
                string v;
                return Load().TryGetValue(key, out v) ? v : null;
            }
        }

        // ---------------- 寫 ----------------

        public static void SetString(string key, string value) { Set(key, value ?? ""); }

        public static void SetInt(string key, int value) { Set(key, value.ToString(CultureInfo.InvariantCulture)); }

        public static void SetFloat(string key, float value) { Set(key, value.ToString("R", CultureInfo.InvariantCulture)); }

        public static void SetBool(string key, bool value) { Set(key, value ? "1" : "0"); }

        private static void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            // 換行會把一筆拆成兩筆、'=' 會讓值的前半被當成鍵 —— 直接擋掉，別讓檔案結構壞掉。
            if (key.IndexOf('=') >= 0 || key.IndexOf('\n') >= 0 || key.IndexOf('\r') >= 0) return;
            value = value.Replace("\r", "").Replace("\n", " ");

            lock (Gate)
            {
                var map = Load();
                string old;
                if (map.TryGetValue(key, out old) && old == value) return;   // 沒變就別標髒
                map[key] = value;
                _dirty = true;
            }
        }

        public static void DeleteKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (Gate)
            {
                if (Load().Remove(key)) _dirty = true;
            }
        }

        public static void DeleteAll()
        {
            lock (Gate)
            {
                Load().Clear();
                _dirty = true;
            }
        }

        /// <summary>真的寫檔（沒有變動就什麼都不做）。寫不進去就安靜放棄 —— 設定存不下來不該弄爆遊戲。</summary>
        public static void Save()
        {
            lock (Gate)
            {
                if (_values == null || !_dirty) return;

                var sb = new StringBuilder();
                sb.Append("# SDO local prefs —— 由遊戲自動寫入。手改也可以，格式是 key=value。\n");
                var keys = new List<string>(_values.Keys);
                keys.Sort(StringComparer.Ordinal);   // 排序：diff 才看得懂，也不會每次存檔順序都不一樣
                foreach (var k in keys) sb.Append(k).Append('=').Append(_values[k]).Append('\n');

                try
                {
                    var path = FilePath;
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    // UTF-8 無 BOM + LF：專案其它 ini 都是這個組合，BOM/CRLF 會讓某些讀取端默默失效。
                    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                    _dirty = false;
                }
                catch { /* 唯讀目錄 → 這次存不下來，下次 Save 再試 */ }
            }
        }

        /// <summary>丟掉記憶體裡那份，下次讀取時重新載入（測試用）。未存檔的變動會消失。</summary>
        public static void Reload()
        {
            lock (Gate) { _values = null; _dirty = false; }
        }

        // ---------------- 載入 ----------------

        /// <summary>呼叫端必須先持有鎖。</summary>
        private static Dictionary<string, string> Load()
        {
            if (_values != null) return _values;

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var path = FilePath;
                if (File.Exists(path))
                {
                    foreach (var line in File.ReadAllLines(path))
                    {
                        var s = line.Trim();
                        if (s.Length == 0 || s[0] == '#' || s[0] == ';') continue;
                        int eq = s.IndexOf('=');
                        if (eq <= 0) continue;
                        map[s.Substring(0, eq).Trim()] = s.Substring(eq + 1).Trim();
                    }
                }
            }
            catch { /* 讀不到 → 當成空的，用預設值跑 */ }

            _values = map;
            _dirty = false;
            return _values;
        }
    }
}
