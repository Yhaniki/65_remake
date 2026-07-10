using TMPro;
using UnityEngine;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Provides a CJK-capable TMP font. PRIMARY = OS-installed SimSun (宋体) — the face the OFFICIAL client
    /// hardcodes for every string (FontD3DWin.cpp builds all text via GDI CreateFontA "SimSun"; same string in the
    /// TW online client). We load it from the player's Windows at runtime like the official exe did — simsun.ttc
    /// ships with all Windows editions but is NOT redistributable, so it must never be bundled. TMP sometimes fails
    /// to rasterize an OS face (方塊字), so the asset is probed with real CJK glyphs before being accepted.
    /// FALLBACK = the bundled Source Han Sans (思源黑體, SIL OFL) at Assets/Resources/Fonts (cross-platform safe).
    /// </summary>
    public static class UIFont
    {
        private static TMP_FontAsset _cjk;
        private static bool _tried;
        // full OTF (繁中+簡中+日+拉丁) under Assets/Resources/ — SAME file the in-game HUD uses (Sdo.Game.TextStyles),
        // so the room (TMP) and gameplay (legacy TextMesh) share one typeface. Single source = TextStyles.
        public const string BundledFontResource = Sdo.Game.TextStyles.BundledFontResource;

        // Probe glyphs proving an OS-font asset actually rasterizes (簡/繁/假名/Latin) instead of tofu.
        private const string ProbeChars = "宋体體測试テスAa1";

        public static TMP_FontAsset Cjk
        {
            get
            {
                if (_tried) return _cjk;
                _tried = true;
                var bundled = BuildBundled();
                // Primary: OS SimSun, the official client's hardcoded face.
                var simsun = BuildOs(new[] { "SimSun", "NSimSun", "宋体" });
                if (simsun != null)
                {
                    simsun.name = "OS_SimSun";
                    // Rare glyphs outside SimSun's GBK coverage fall through to the bundled face.
                    if (bundled != null) AddFallback(simsun, bundled);
                    _cjk = simsun;
                    return _cjk;
                }
                // Fallback: bundled Source Han Sans (imported → TMP can rasterize a dynamic CJK atlas from it).
                if (bundled != null) { _cjk = bundled; return _cjk; }
                // Last resort: any installed OS face that survives the probe.
                _cjk = BuildOs(new[] { "Microsoft JhengHei", "Microsoft YaHei", "PMingLiU", "SimHei", "Arial" });
                if (_cjk == null) return null;
                _cjk.name = "OS_CJK_Primary";
                // Japanese kana/glyph fallback.
                var jp = BuildOs(new[] { "Yu Gothic", "Meiryo", "MS Gothic" });
                if (jp != null) { jp.name = "OS_JP_Fallback"; AddFallback(_cjk, jp); }
                // Simplified Chinese fallback.
                var sc = BuildOs(new[] { "Microsoft YaHei", "SimSun" });
                if (sc != null) { sc.name = "OS_SC_Fallback"; AddFallback(_cjk, sc); }
                return _cjk;
            }
        }

        // 華康儷中黑 (DFLiHei) — the face the user hand-baked the OPTION dialog text in. BUNDLED with the game
        // (Assets/Resources/Fonts/DFLiHei.ttc) so it renders on ANY machine, not just one with the font installed.
        // Used ONLY for the OPTION dialog's dynamic text (the 進階 tab). Load order: bundled resource → OS-installed
        // face → Cjk (SimSun/Source Han Sans) so the dialog never renders tofu even if the ttc is missing/unreadable.
        public const string LiheiFontResource = "Fonts/DFLiHei";
        private static TMP_FontAsset _lihei; private static bool _liheiTried;
        public static TMP_FontAsset Lihei
        {
            get
            {
                if (_liheiTried) return _lihei;
                _liheiTried = true;
                var face = BuildResource(LiheiFontResource)
                        ?? BuildOs(new[] { "DFLiHei-Md", "DFPLiHei-Md", "華康儷中黑", "華康儷中黑(P)", "DFLiHei", "DFPLiHei" });
                if (face != null)
                {
                    face.name = "DFLiHei";
                    var bundled = BuildBundled();
                    if (bundled != null) AddFallback(face, bundled);   // rare glyphs fall through to Source Han Sans
                    _lihei = face;
                }
                else
                {
                    _lihei = Cjk;   // face unavailable → reuse the SimSun/bundled face so text still shows
                }
                return _lihei;
            }
        }

        /// <summary>Build a TMP asset from a Font imported under Resources (bundled with the game). Null if absent or
        /// it fails to rasterize CJK (→ caller falls back to the OS face).</summary>
        private static TMP_FontAsset BuildResource(string path)
        {
            var f = Resources.Load<Font>(path);
            if (f == null) return null;
            try { var fa = TMP_FontAsset.CreateFontAsset(f); if (fa != null && Probe(fa)) return fa; } catch { }
            return null;
        }

        // fallbackFontAssetTable is NULL on runtime-created assets (TMP only allocates it for serialized ones);
        // .Add() without this guard NREs and aborts whatever screen is mid-build.
        private static void AddFallback(TMP_FontAsset primary, TMP_FontAsset fallback)
        {
            if (primary.fallbackFontAssetTable == null)
                primary.fallbackFontAssetTable = new System.Collections.Generic.List<TMP_FontAsset>();
            primary.fallbackFontAssetTable.Add(fallback);
        }

        private static TMP_FontAsset BuildBundled()
        {
            var bundled = Resources.Load<Font>(BundledFontResource);
            if (bundled == null) return null;
            TMP_FontAsset fa = null;
            try { fa = TMP_FontAsset.CreateFontAsset(bundled); } catch { }
            if (fa != null) fa.name = "SourceHanSansTC";
            return fa;
        }

        /// <summary>First face (in order) that both builds a TMP asset AND rasterizes the probe glyphs.</summary>
        private static TMP_FontAsset BuildOs(string[] names)
        {
            foreach (var name in names)
            {
                // Family-name path first (resolves through the OS font table, null if absent) …
                TMP_FontAsset fa = null;
                try { fa = TMP_FontAsset.CreateFontAsset(name, "Regular"); } catch { }
                if (fa != null && Probe(fa)) return fa;
                // … then the legacy dynamic-Font path.
                fa = Build(new[] { name });
                if (fa != null && Probe(fa)) return fa;
            }
            return null;
        }

        private static bool Probe(TMP_FontAsset fa)
        {
            try { return fa.TryAddCharacters(ProbeChars); } catch { return false; }
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
