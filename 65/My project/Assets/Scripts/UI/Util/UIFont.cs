using TMPro;
using UnityEngine;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Provides a CJK-capable TMP font. PRIMARY = the bundled Source Han Sans (思源黑體, SIL OFL) at
    /// Assets/Resources/Fonts — an IMPORTED font file has source data TMP can rasterize into a DYNAMIC atlas, so
    /// 繁中/漢字/假名/Latin all render (cross-platform). FALLBACK = a dynamic font from an installed OS font
    /// (best-effort; TMP often cannot rasterize OS dynamic fonts, which is why those alone showed 方塊字).
    /// </summary>
    public static class UIFont
    {
        private static TMP_FontAsset _cjk;
        private static bool _tried;
        // full OTF (繁中+簡中+日+拉丁) under Assets/Resources/ — SAME file the in-game HUD uses (Sdo.Game.TextStyles),
        // so the room (TMP) and gameplay (legacy TextMesh) share one typeface. Single source = TextStyles.
        public const string BundledFontResource = Sdo.Game.TextStyles.BundledFontResource;

        public static TMP_FontAsset Cjk
        {
            get
            {
                if (_tried) return _cjk;
                _tried = true;
                // Primary: bundled Source Han Sans (imported → TMP can rasterize a dynamic CJK atlas from it).
                var bundled = Resources.Load<Font>(BundledFontResource);
                if (bundled != null)
                {
                    try { _cjk = TMP_FontAsset.CreateFontAsset(bundled); } catch { _cjk = null; }
                    if (_cjk != null) { _cjk.name = "SourceHanSansTC"; return _cjk; }
                }
                // Fallback: build from an installed OS font (繁中 face; covers SC + most kanji + Latin).
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
