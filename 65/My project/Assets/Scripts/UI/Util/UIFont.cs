using TMPro;
using UnityEngine;
using Sdo.Localization;

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
                _cjk = BuildCjk();
                ClearBrokenKerning(_cjk);
                // 這份字型也是「量字縫」的依據:字距收緊要靠真 metrics 才算得準(legacy Font 的點陣 metrics 含
                // 光柵化留白又被 GDI 塗粗過,太胖)。遊戲內名牌/排名榜(legacy TextMesh)因此跟房間(TMP)收一樣多。
                Sdo.Game.TextTracking.MeasureFont = _cjk;
                return _cjk;
            }
        }

        /// <summary>The bundled Source Han Sans (思源黑體, SIL OFL). ONE shared instance —— 每建一份就是另一張
        /// atlas,而它同時是 <see cref="Cjk"/> 與 <see cref="Lihei"/> 的 fallback。四種語言實測 0 缺字。</summary>
        public static TMP_FontAsset Bundled
        {
            get
            {
                if (_bundledTried) return _bundled;
                _bundledTried = true;
                _bundled = BuildBundled();
                return _bundled;
            }
        }
        private static TMP_FontAsset _bundled; private static bool _bundledTried;

        /// <summary>Resolve the CJK face: OS SimSun → bundled Source Han Sans → any OS face that survives the probe.</summary>
        private static TMP_FontAsset BuildCjk()
        {
            var bundled = Bundled;
            // Primary: OS SimSun, the official client's hardcoded face.
            var simsun = BuildOs(new[] { "SimSun", "NSimSun", "宋体" });
            if (simsun != null)
            {
                simsun.name = "OS_SimSun";
                // Rare glyphs outside SimSun's GBK coverage fall through to the bundled face.
                if (bundled != null) AddFallback(simsun, bundled);
                return simsun;
            }
            // Fallback: bundled Source Han Sans (imported → TMP can rasterize a dynamic CJK atlas from it).
            if (bundled != null) return bundled;
            // Last resort: any installed OS face that survives the probe.
            var fa = BuildOs(new[] { "Microsoft JhengHei", "Microsoft YaHei", "PMingLiU", "SimHei", "Arial" });
            if (fa == null) return null;
            fa.name = "OS_CJK_Primary";
            // Japanese kana/glyph fallback.
            var jp = BuildOs(new[] { "Yu Gothic", "Meiryo", "MS Gothic" });
            if (jp != null) { jp.name = "OS_JP_Fallback"; AddFallback(fa, jp); }
            // Simplified Chinese fallback.
            var sc = BuildOs(new[] { "Microsoft YaHei", "SimSun" });
            if (sc != null) { sc.name = "OS_SC_Fallback"; AddFallback(fa, sc); }
            return fa;
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
                    var bundled = Bundled;
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

        /// <summary>
        /// OPTION 對話框「進階」那頁動態文字的字面 —— **依語言切**：繁中＝華康儷中黑（<see cref="Lihei"/>，
        /// OPTIONDLG 那張圖上烘死的中文就是它），其餘語言＝bundled 思源黑體（<see cref="Bundled"/>）。
        ///
        /// 🔴 為什麼不能一律用儷中黑:`DFLiHei.ttc` 的 cmap 宣告 **35,981** 個碼位,其中 **11,706 個的字形是空的**
        ///    —— 有登記、沒筆畫。簡中命中 144 字(显/语/简/弹/开/关/闭…)、**日文也命中 47 字**(変/弾/楽/戻/観…),
        ///    繁中 0。因為 cmap 有登記,TMP 認定「這個字我有」→ **fallback 永遠不會啟動**,直接畫出一片空白;
        ///    症狀是「字不見了」而不是豆腐字 □,所以更難發現(使用者回報:簡中「显示模式」變成「示模式」)。
        ///    `HasCharacter()` 對這種字回 true —— 要判斷畫不畫得出來只能看 glyph metrics,見 <see cref="CanRender"/>。
        /// </summary>
        public static TMP_FontAsset DialogFace
            => LocalizationManager.Current == Language.TraditionalChinese ? Lihei : (Bundled ?? Cjk);

        /// <summary>
        /// 這份字型(含它的 fallback)**真的畫得出**這串字嗎 —— 不是問「有沒有這個碼位」(那是
        /// <c>HasCharacter</c>,對空心字會騙人,見 <see cref="DialogFace"/>),而是看 glyph 的 metrics 有沒有寬高。
        /// 空白/控制字元跳過(它們本來就沒有墨水)。畫不出來時 <paramref name="missing"/> 回第一個出問題的字。
        /// </summary>
        public static bool CanRender(TMP_FontAsset fa, string text, out char missing)
        {
            missing = '\0';
            if (string.IsNullOrEmpty(text)) return true;
            if (fa == null) { missing = text[0]; return false; }
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c)) continue;
                if (!HasInk(fa, c, 0)) { missing = c; return false; }
            }
            return true;
        }

        /// <summary>一個字在這份字型(或它的 fallback)裡有沒有實際筆畫。**主字型宣告有、但字形是空的 → 直接回 false
        /// 而不往 fallback 找** —— 那正是 TMP 排版時會做的事(它認為主字型有,不會再問 fallback),這裡要模擬的是
        /// 「畫出來會長什麼樣」,不是「哪裡找得到這個字」。</summary>
        private static bool HasInk(TMP_FontAsset fa, char c, int depth)
        {
            if (fa == null || depth > 4) return false;              // depth:防 fallback 互指造成無限遞迴
            bool declared;
            try { declared = fa.HasCharacter(c, searchFallbacks: false, tryAddCharacter: true); }
            catch { declared = false; }
            if (declared)
            {
                var table = fa.characterLookupTable;
                if (table != null && table.TryGetValue(c, out var ch) && ch != null && ch.glyph != null)
                {
                    var m = ch.glyph.metrics;
                    return m.width > 0f && m.height > 0f;
                }
                return true;    // 加得進去但查不到 metrics → 當它畫得出來（不要誤判成缺字）
            }
            var fb = fa.fallbackFontAssetTable;
            if (fb != null)
                foreach (var f in fb)
                    if (f != null && f != fa && HasInk(f, c, depth + 1)) return true;
            return false;
        }

        // 俐方體 Cubic 11 — a SIL OFL pixel/bitmap font (bundled at Assets/Resources/Fonts/Cubic_11.ttf, redistributable
        // unlike the OS SimSun). Used for the boot loading screen's tech-minimalist pixel look. Cubic 11 covers 繁中 +
        // 假名 + Latin; any glyph it lacks (簡中 song titles, rare kanji) falls through to the CJK face, and if the pixel
        // font itself is missing (worktree not yet imported by Unity) the whole thing degrades to Cjk so boot never breaks.
        public const string CubicFontResource = "Fonts/Cubic_11";
        private static TMP_FontAsset _cubic; private static bool _cubicTried;
        public static TMP_FontAsset Cubic
        {
            get
            {
                if (_cubicTried) return _cubic;
                _cubicTried = true;
                var f = Resources.Load<Font>(CubicFontResource);
                if (f != null)
                {
                    TMP_FontAsset fa = null;
                    try { fa = TMP_FontAsset.CreateFontAsset(f); } catch { }
                    if (fa != null)
                    {
                        fa.name = "Cubic11";
                        var cjk = Cjk;                          // glyphs Cubic 11 lacks fall through to SimSun/Source Han
                        if (cjk != null) AddFallback(fa, cjk);
                        _cubic = fa;
                        return _cubic;
                    }
                }
                _cubic = Cjk;   // pixel font unavailable → keep the boot screen readable with the CJK face
                return _cubic;
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

        /// <summary>
        /// 關掉這個標籤的 kerning。必須做在**標籤**上而不是字型上:字型的 kerning 表是執行期
        /// <c>TryAddCharacters</c> 時才被填的,在建字型當下清空,之後載入新字元又會被填回來(實測清完仍是 62 對)。
        /// 關掉 <c>kern</c> feature 則是 TMP 排版時查不查那張表的開關,一勞永逸。
        /// 詳細原因見 <see cref="ClearBrokenKerning"/>。
        /// </summary>
        public static void DisableKerning(TMP_Text label)
        {
            if (label == null) return;
            var feats = label.fontFeatures;
            if (feats != null) feats.Clear();
        }

        /// <summary>
        /// 丟掉 TMP 從 OS 字型讀進來的 kerning(字距對)調整表。
        ///
        /// 為什麼:官方字體 SimSun 的西文是「半形等寬」(upem 只有 256、每個拉丁字母一律 advance 0.5em),
        /// 本來就沒有、也不需要 kerning。但 TMP 的動態字型會把字型檔裡的 GPOS/kern 讀出來套用,而讀到的值
        /// 完全不成比例 —— 實機診斷印出來是 <c>xAdvance = -35.15625</c> font units,除以 pointSize 90 = **-0.39 em**,
        /// 在 14px 字級下就是 **-5.47px**:一個字母的前進量只剩 2.5px(正常 7.9px),於是 "AT"、"AV" 這種
        /// 組合直接疊在一起變成一個字。
        ///
        /// 這也是「同一份程式碼在兩個 branch 長不一樣」的真正原因:kerning 表是**執行期**才被填的,
        /// 載入的字元夠多才會觸發(song-loader 開機要掃 176 頁歌單、warmup 幾百個字,atlas 用到 8 張),
        /// 字元少的那邊表是空的、看起來就正常。清掉之後兩邊的排版逐像素一致。
        /// </summary>
        private static void ClearBrokenKerning(TMP_FontAsset fa, int depth = 0)
        {
            if (fa == null || depth > 4) return;               // depth:防 fallback 互指造成無限遞迴
            var recs = fa.fontFeatureTable != null ? fa.fontFeatureTable.glyphPairAdjustmentRecords : null;
            if (recs != null) recs.Clear();
            var fb = fa.fallbackFontAssetTable;
            if (fb != null)
                foreach (var f in fb)
                    if (f != null && f != fa) ClearBrokenKerning(f, depth + 1);
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
