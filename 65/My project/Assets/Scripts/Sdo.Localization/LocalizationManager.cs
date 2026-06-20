using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sdo.Localization
{
    /// <summary>
    /// Lightweight runtime string-table localization (繁中 / 簡中 / 英 / 日).
    /// Tables live at StreamingAssets/Localization/&lt;code&gt;.json. English is always loaded as the
    /// fallback. Missing keys return "[key]" + a warning so they are visible during development.
    /// </summary>
    public static class LocalizationManager
    {
        public static Language Current { get; private set; } = Language.TraditionalChinese;
        public static event Action LanguageChanged;

        private static StringTable _table;     // active language
        private static StringTable _fallback;  // English
        private static CultureInfo _culture = CultureInfo.GetCultureInfo("en-US");
        private static bool _inited;

        public static CultureInfo Culture => _culture;
        public static bool IsInitialized => _inited;

        public static void Init(Language lang)
        {
            _fallback = LoadTable(Language.English);
            _inited = true;
            SetLanguage(lang);
        }

        public static void EnsureInit()
        {
            if (!_inited) Init(Current);
        }

        public static void SetLanguage(Language lang)
        {
            Current = lang;
            var t = LoadTable(lang);
            _table = t ?? _fallback;
            var cultureName = _table?.Culture ?? LanguageInfo.Culture(lang);
            try { _culture = CultureInfo.GetCultureInfo(cultureName); }
            catch { _culture = CultureInfo.InvariantCulture; }
            LanguageChanged?.Invoke();
        }

        /// <summary>Test seam: inject tables directly without touching StreamingAssets.</summary>
        public static void LoadFromTables(Language current, StringTable currentTable, StringTable fallbackTable)
        {
            Current = current;
            _table = currentTable;
            _fallback = fallbackTable;
            _inited = true;
            try { _culture = CultureInfo.GetCultureInfo(currentTable?.Culture ?? "en-US"); }
            catch { _culture = CultureInfo.InvariantCulture; }
            LanguageChanged?.Invoke();
        }

        public static string Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (_table != null && _table.TryGet(key, out var v) && v != null) return v;
            if (_fallback != null && _fallback.TryGet(key, out var fv) && fv != null) return fv;
            Debug.LogWarning($"[Loc] missing key: {key}");
            return "[" + key + "]";
        }

        public static string Get(string key, params object[] args)
        {
            var fmt = Get(key);
            if (args == null || args.Length == 0) return fmt;
            try { return string.Format(_culture, fmt, args); }
            catch { return fmt; }
        }

        private static StringTable LoadTable(Language lang)
        {
            try
            {
                var path = Path.Combine(Application.streamingAssetsPath, "Localization", LanguageInfo.Code(lang) + ".json");
                if (!File.Exists(path))
                {
                    Debug.LogWarning($"[Loc] table missing: {path}");
                    return null;
                }
                return StringTable.Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception e)
            {
                Debug.LogError($"[Loc] load failed for {lang}: {e.Message}");
                return null;
            }
        }
    }
}
