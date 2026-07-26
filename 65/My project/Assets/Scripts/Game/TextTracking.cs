using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 字距收緊(tracking)的安全上限 —— 收緊到「字母不會互相黏住」為止。
    ///
    /// 為什麼需要:官方字體 SimSun 的西文是「半形等寬」,每個拉丁字母的前進寬度都固定 0.5 em
    /// (128/256,upem 只有 256),但墨水幾乎填滿整個字身:大寫 T 右側只留 9/256 em、A 左側只留
    /// 2/256 em,所以 "TA" 兩個字母之間天生只有 0.043 em 的空隙("AT" 0.047、"AV"/"VA" 0.035、
    /// "mA" 0.012)。全形中日文則兩側各留 ~0.04 em,相鄰空隙 ≥0.08 em。
    /// 於是同一個「固定收緊 0.05 em」,中文歌名看不出來,含 T/A 的英文歌名卻直接把兩個字母的墨水
    /// 疊在一起(SOLID STATE SQUAD 的 "TA" 連成一塊)。
    ///
    /// 對策:收緊量改成逐字串計算 —— 量出這串字每個字縫的真實墨水間距,收緊後至少要留下
    /// <c>minGapEm</c>。純中文歌名維持原本的收緊量,含 TA/AT 的英文歌名自動退回接近自然字距。
    /// 這只調整字元間距,字形本身永遠不縮放(真字距,不變形)。
    ///
    /// 但「退回自然字距」還不夠 —— TA 的 0.043em 在歌單的 14px 字級下只有 0.60 個像素,一開始就不夠。
    /// 逐像素量到的實況(SimSun / 14px / faux bold):收緊 0.05em → 兩個字母併成一塊;完全不收緊 → 只剩
    /// 1 個半亮的像素縫(谷底亮度 0.17,粉底深字下人眼看就是黏著)。所以窄到一定程度的字縫必須**撐開**:
    /// 自然字縫 &lt; <see cref="TextStyles.NarrowInkGapPx"/> 顯示像素的,撐到 <see cref="TextStyles.SafeInkGapPx"/>。
    /// 門檻用顯示像素而不是 em —— 黏不黏是像素現象,同一個 em 字縫在 14px 和 48px 下差很多。門檻壓在 0.75px
    /// 讓中文一個都不觸發(最窄的中文相鄰組合仍有 0.88px),中文的收緊量原封不動。
    /// 因此本類別回傳的「收緊量」可以是負的 —— 負值 = 撐開。
    ///
    /// 兩條字型路徑都走這裡:TMP(選歌歌名、房間頭頂名字 <c>OutlinedLabel</c>)用 <see cref="TMP_FontAsset"/> 版,
    /// 遊戲內名牌/排名榜的 legacy <see cref="TextMesh"/> 逐字佈局(<see cref="Label3D"/>)用 <see cref="Font"/> 版。
    /// 兩邊都是「整串取最窄的字縫」而不是每個字縫各自收 —— 房間(TMP)和遊戲內(TextMesh)顯示同一個名字,
    /// 收緊策略一致,兩邊的寬度才會一致。
    /// </summary>
    public static class TextTracking
    {
        /// <summary>量字縫用的字型,由 UI 端在建好 CJK 字型時掛上來(<c>UIFont.Cjk</c>)。
        ///
        /// 為什麼 legacy <see cref="Font"/> 不能自己量:它回報的是「圖集裡那格點陣的大小」——含 1px 光柵化留白,
        /// 而且 SimSun 沒有粗體字面、Bold 是 GDI 現場塗粗的,塗完 glyph 比字身還寬(48px 下 'A' 的 glyphWidth 27 &gt;
        /// advance 24)。照它算,連中文的字縫都會變成負的 → 收緊量一律歸零,「字靠緊一點」整個失效。
        /// TMP 的 font asset 走 FreeType,拿到的是字型檔裡的真 metrics(和用 fontTools 直接讀 simsun.ttc 對得上),
        /// 所以兩條路徑都用它量 —— 順便讓房間(TMP)和遊戲內(TextMesh)的同一個名字收一樣多。</summary>
        public static TMP_FontAsset MeasureFont;

        /// <summary>撐開量的上限(em)—— 防呆用。字型裡偶爾有墨水完全填滿字身的字(自然字縫 ≈ 0),
        /// 沒有上限的話整串會被那一個字縫撐得離譜。</summary>
        public const float MaxExpandEm = 0.08f;

        /// <summary>一個字縫「應該留下多少墨水間距」(em)。字縫本來就夠寬 → 只要守住收緊底線 <paramref name="minGapEm"/>;
        /// 窄到 <see cref="TextStyles.NarrowInkGapPx"/> 顯示像素以下 → 那是字型給不起,改成撐開到
        /// <see cref="TextStyles.SafeInkGapPx"/> 個像素。<paramref name="displayPx"/> ≤ 0(不知道畫多大)時退回純 em 判斷。</summary>
        private static float TargetGapEm(float naturalGapEm, float minGapEm, float displayPx)
        {
            if (displayPx <= 0f) return minGapEm;
            if (naturalGapEm * displayPx >= TextStyles.NarrowInkGapPx) return minGapEm;
            return TextStyles.SafeInkGapPx / displayPx;
        }

        /// <summary>純幾何:給定每個字縫的自然墨水間距(em)、想收緊的量、收緊後必須留下的最小間距,以及這串字
        /// 實際畫多大(顯示像素),回傳該套用的字距調整量。正值 = 收緊,**負值 = 撐開**(字縫天生就不夠時)。
        /// 整串共用一個值,所以取最需要空間的那個字縫決定 —— 房間(TMP)和遊戲內(TextMesh)才會一致。</summary>
        public static float ClampTrackEm(IList<float> naturalGapsEm, float desiredTrackEm, float minGapEm, float displayPx)
        {
            float ask = Mathf.Max(0f, desiredTrackEm);
            if (naturalGapsEm == null || naturalGapsEm.Count == 0) return ask;
            float allowed = ask;
            for (int i = 0; i < naturalGapsEm.Count; i++)
            {
                float gap = naturalGapsEm[i];
                allowed = Mathf.Min(allowed, gap - TargetGapEm(gap, minGapEm, displayPx));
            }
            return Mathf.Clamp(allowed, -MaxExpandEm, ask);
        }

        /// <summary>量 <paramref name="text"/> 在 <paramref name="font"/> 下每個字縫的墨水間距,回傳該套用的字距
        /// 調整量(正 = 收緊,負 = 撐開)。<paramref name="displayPx"/> 是這串字實際畫多大 —— 判斷「窄到會黏」要用
        /// 顯示像素。字型/字串量不到就原樣回傳 <paramref name="desiredTrackEm"/>(尚未進 atlas 的字不設限)。</summary>
        public static float SafeTrackEm(TMP_FontAsset font, string text, float desiredTrackEm, float minGapEm, float displayPx)
        {
            float ask = Mathf.Max(0f, desiredTrackEm);
            if (font == null || string.IsNullOrEmpty(text) || text.Length < 2) return ask;
            // 動態字型:字要先進 atlas 才查得到 metrics(已存在的字會直接跳過,成本很低)。
            try { font.TryAddCharacters(text); } catch { /* static font asset / 失敗 → 下面查不到就當作不設限 */ }

            float allowed = ask;
            for (int i = 0; i + 1 < text.Length; i++)
            {
                // 含空白的「字縫」是詞間距,不是墨水縫(空格 glyph 沒有墨水)—— 讓它參與只會讓整串被
                // 一個假字縫綁住:"SOLID STATE SQUAD" 的撐開量本來就是被 "E "(0.44px)決定的。
                if (char.IsWhiteSpace(text[i]) || char.IsWhiteSpace(text[i + 1])) continue;
                if (!TryMetrics(font, text[i], out _, out float rsb)) continue;
                if (!TryMetrics(font, text[i + 1], out float lsb, out _)) continue;
                float gap = rsb + lsb;
                allowed = Mathf.Min(allowed, gap - TargetGapEm(gap, minGapEm, displayPx));
            }
            return Mathf.Clamp(allowed, -MaxExpandEm, ask);
        }

        /// <summary>同上,但給 legacy <see cref="TextMesh"/> 的逐字佈局用(<see cref="Label3D"/>)。優先用
        /// <see cref="MeasureFont"/> 的真 metrics 量,這樣跟 TMP 那邊收一樣多;沒掛字型時退回 legacy
        /// <see cref="Font"/> 自己的點陣 metrics —— 那份含光柵化留白又被 GDI 塗粗過,只夠拿來要求「別重疊」
        /// (minGap 當 0),量出來會比實際保守。</summary>
        public static float SafeTrackEm(Font font, string text, int fontSize, FontStyle style, float desiredTrackEm,
                                        float minGapEm, float displayPx)
        {
            float ask = Mathf.Max(0f, desiredTrackEm);
            if (MeasureFont != null) return SafeTrackEm(MeasureFont, text, desiredTrackEm, minGapEm, displayPx);
            if (font == null || string.IsNullOrEmpty(text) || text.Length < 2 || fontSize <= 0) return ask;
            font.RequestCharactersInTexture(text, fontSize, style);   // 沒進 atlas 就量不到 metrics

            float allowed = ask;
            for (int i = 0; i + 1 < text.Length; i++)
            {
                if (!font.GetCharacterInfo(text[i], out CharacterInfo a, fontSize, style)) continue;
                if (!font.GetCharacterInfo(text[i + 1], out CharacterInfo b, fontSize, style)) continue;
                float gap = ((a.advance - a.bearing - a.glyphWidth) + b.bearing) / (float)fontSize;
                allowed = Mathf.Min(allowed, gap);   // 這份 metrics 已經很胖了,再扣 minGap 會把中文也歸零
            }
            return Mathf.Clamp(allowed, -MaxExpandEm, ask);
        }

        /// <summary>把安全字距套到一個 TMP 標籤上(TMP 的 characterSpacing 單位 = fontSize 的 1/100,
        /// 負值 = 收緊)。設完 <c>text</c> 之後呼叫 —— 調整量取決於字串內容。
        /// 用 <c>label.fontSize</c> 當顯示像素:那是這串字在設計解析度下畫多大,也是最保守的估計
        /// (畫面被放大時字縫只會更寬,不會更黏)。</summary>
        public static void ApplySafeTracking(TMP_Text label, float desiredTrackEm, float minGapEm)
        {
            if (label == null) return;
            label.characterSpacing = -SafeTrackEm(label.font, label.text, desiredTrackEm, minGapEm, label.fontSize) * 100f;
        }

        /// <summary>設定文字並套上光學字距:characterSpacing 只負責收緊(中文歌名照舊收 0.05em),
        /// 「撐開」交給逐字對的 <c>&lt;cspace&gt;</c>(見 <see cref="OpticalText"/>)—— 兩者分工才不會像整串撐開那樣
        /// 「把夠寬的縫撐得更寬、最窄的那對還是黏著」。傳未加工的原字串,函式自己組 rich text 指派給標籤。</summary>
        public static void ApplyOpticalTracking(TMP_Text label, string text, float desiredTrackEm, float minGapEm,
                                                float targetGapPx)
        {
            if (label == null) return;
            text = text ?? "";
            // displayPx 傳 0 = 純 em 判斷 = 只收緊不撐開(撐開由下面的逐字對負責,兩邊都做會疊成兩倍)。
            float track = SafeTrackEm(label.font, text, desiredTrackEm, minGapEm, 0f);
            label.characterSpacing = -track * 100f;
            label.text = OpticalText(label.font, text, label.fontSize, targetGapPx, track);
        }

        /// <summary>advance 小於這個(em)就當半形西文。SimSun 的拉丁字母一律 0.5em、全形中日文 1.0em,
        /// 中間沒有東西,所以用 advance 分辨比查 Unicode 範圍直接。</summary>
        private const float HalfWidthMaxEm = 0.75f;

        /// <summary>把一串字排成「每個字縫都看得見」的 TMP rich text —— 逐字對各自補足,不是整串一起撐。
        ///
        /// 為什麼不能整串一起撐:整串只有一個 characterSpacing,撐開量由最窄的那個字縫決定,結果是
        /// **本來就夠寬的縫被撐得更寬,最窄的那對還是不夠**。實測(SOLID STATE SQUAD @14px):整串撐到
        /// 讓 UA 有 1.2px 時,SO 被撐到 2.08px 看起來很鬆,而 TA 只有 1.36px —— 使用者回報「其他字都變寬了,
        /// 只有 TA 還黏在一起」。更糟的是撐開量其實被 <c>"E "</c>(0.44em)這種**空白對**綁住,而空格沒有墨水,
        /// 撐它只是把詞間距推大,對 TA 一點幫助都沒有。
        ///
        /// 逐字對做就沒有這個問題:每對各自補到 <paramref name="targetGapPx"/>,夠寬的一點都不加,
        /// 所以總寬度不會浪費(實測反而比整串撐開更窄)。只動半形西文之間的縫 —— 全形中日文靠字形辨識,
        /// 縫本來就不是它的可讀性來源,撐它會把中文歌名整個拆散。含空白的對也跳過(那是詞間距,不是墨水縫)。
        ///
        /// <paramref name="alreadyTrackedEm"/> 是已經套在 characterSpacing 上的收緊量(正 = 收緊),
        /// 兩者會疊加,所以補的量要把它加回來。回傳值直接指派給 <c>label.text</c>。</summary>
        public static string OpticalText(TMP_FontAsset font, string text, float displayPx, float targetGapPx,
                                         float alreadyTrackedEm = 0f)
        {
            if (font == null || string.IsNullOrEmpty(text) || text.Length < 2) return text;
            if (displayPx <= 0f || targetGapPx <= 0f) return text;
            // 歌名自己帶 '<' 的話,插進去的標籤會讓 TMP 把整段當標記解析 → 整串原樣送出,不做光學字距。
            if (text.IndexOf('<') >= 0) return text;
            try { font.TryAddCharacters(text); } catch { /* 查不到 metrics 的字下面會被跳過 */ }

            float targetEm = targetGapPx / displayPx;
            var sb = new StringBuilder(text.Length * 3);
            float applied = 0f;
            bool any = false;
            for (int i = 0; i < text.Length; i++)
            {
                float extra = (i + 1 < text.Length) ? ExtraGapEm(font, text[i], text[i + 1], targetEm, alreadyTrackedEm) : 0f;
                if (Mathf.Abs(extra - applied) > 1e-4f)
                {
                    sb.Append("<cspace=").Append(extra.ToString("0.####", CultureInfo.InvariantCulture)).Append("em>");
                    applied = extra;
                    any = true;
                }
                sb.Append(text[i]);
            }
            if (!any) return text;
            sb.Append("</cspace>");
            return sb.ToString();
        }

        /// <summary>這一對字之間還缺多少 em 才看得見(0 = 不用補)。空白對、全形字對、量不到的字都不補。</summary>
        private static float ExtraGapEm(TMP_FontAsset font, char a, char b, float targetEm, float alreadyTrackedEm)
        {
            if (char.IsWhiteSpace(a) || char.IsWhiteSpace(b)) return 0f;
            if (!TryMetrics(font, a, out _, out float rsb, out float advA)) return 0f;
            if (!TryMetrics(font, b, out float lsb, out _, out float advB)) return 0f;
            if (advA >= HalfWidthMaxEm || advB >= HalfWidthMaxEm) return 0f;
            return Mathf.Max(0f, targetEm - (rsb + lsb - alreadyTrackedEm));
        }

        /// <summary>一個字的左右側距(sidebearing,em):左 = bearingX,右 = advance − bearingX − width。
        /// 主字型查不到就往 fallback 找(各自用自己的 pointSize 正規化成 em)。</summary>
        private static bool TryMetrics(TMP_FontAsset font, char ch, out float lsbEm, out float rsbEm)
            => TryMetrics(font, ch, out lsbEm, out rsbEm, out _);

        private static bool TryMetrics(TMP_FontAsset font, char ch, out float lsbEm, out float rsbEm, out float advanceEm)
        {
            lsbEm = rsbEm = advanceEm = 0f;
            if (font == null) return false;
            var lookup = font.characterLookupTable;
            if (lookup != null && lookup.TryGetValue(ch, out var c) && c != null && c.glyph != null)
            {
                float pt = font.faceInfo.pointSize;
                if (pt <= 0f) return false;
                var m = c.glyph.metrics;
                lsbEm = m.horizontalBearingX / pt;
                rsbEm = (m.horizontalAdvance - m.horizontalBearingX - m.width) / pt;
                advanceEm = m.horizontalAdvance / pt;
                return true;
            }
            var fallbacks = font.fallbackFontAssetTable;
            if (fallbacks != null)
                foreach (var f in fallbacks)
                    if (f != null && f != font && TryMetrics(f, ch, out lsbEm, out rsbEm, out advanceEm)) return true;
            return false;
        }
    }
}
