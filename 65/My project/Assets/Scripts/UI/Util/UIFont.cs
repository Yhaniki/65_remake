using TMPro;
using UnityEngine;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Provides a CJK-capable TMP font for v1 WITHOUT shipping a font file: it builds a dynamic
    /// TMP_FontAsset from an installed OS font (Microsoft JhengHei etc.) so 繁中/簡中/日 render
    /// immediately on Windows. Swap in a bundled Noto Sans CJK asset later for cross-platform builds.
    /// </summary>
    public static class UIFont
    {
        private static TMP_FontAsset _cjk;
        private static bool _tried;

        public static TMP_FontAsset Cjk
        {
            get
            {
                if (_tried) return _cjk;
                _tried = true;
                // Primary: Traditional Chinese face (also covers SC + most kanji + Latin).
                _cjk = Build(new[] { "Microsoft JhengHei", "Microsoft YaHei", "PMingLiU", "SimHei", "Arial" });
                if (_cjk == null) return null;
                _cjk.name = "OS_CJK_Primary";
                // Japanese kana/glyph fallback.
                var jp = Build(new[] { "Yu Gothic", "Meiryo", "MS Gothic" });
                if (jp != null) { jp.name = "OS_JP_Fallback"; _cjk.fallbackFontAssetTable.Add(jp); }
                // Simplified Chinese fallback.
                var sc = Build(new[] { "Microsoft YaHei", "SimSun" });
                if (sc != null) { sc.name = "OS_SC_Fallback"; _cjk.fallbackFontAssetTable.Add(sc); }
                return _cjk;
            }
        }

        private static TMP_FontAsset Build(string[] names)
        {
            try
            {
                var os = Font.CreateDynamicFontFromOSFont(names, 36);
                if (os == null) return null;
                return TMP_FontAsset.CreateFontAsset(os);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Pre-add a set of glyphs to the dynamic atlas to avoid first-show stutter.</summary>
        public static void Warmup(string characters)
        {
            if (string.IsNullOrEmpty(characters)) return;
            var f = Cjk;
            if (f == null) return;
            try { f.TryAddCharacters(characters); } catch { /* best effort */ }
        }
    }
}
