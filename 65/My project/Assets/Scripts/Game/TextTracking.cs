using System.Collections.Generic;
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

        /// <summary>純幾何:給定每個字縫的自然墨水間距(em)、想收緊的量、收緊後必須留下的最小間距,
        /// 回傳實際可用的收緊量。本函式只收緊不撐開,所以結果永遠 ≥ 0;字縫不足時退回 0(自然字距)。</summary>
        public static float ClampTrackEm(IList<float> naturalGapsEm, float desiredTrackEm, float minGapEm)
        {
            if (desiredTrackEm <= 0f) return 0f;
            if (naturalGapsEm == null) return desiredTrackEm;
            float allowed = desiredTrackEm;
            for (int i = 0; i < naturalGapsEm.Count; i++)
                allowed = Mathf.Min(allowed, naturalGapsEm[i] - minGapEm);
            return Mathf.Max(0f, allowed);
        }

        /// <summary>量 <paramref name="text"/> 在 <paramref name="font"/> 下每個字縫的墨水間距,回傳
        /// 收緊後仍留有 <paramref name="minGapEm"/> 的最大收緊量。字型/字串量不到就原樣回傳
        /// <paramref name="desiredTrackEm"/>(量不到的字縫不設限,例如尚未進 atlas 的字)。</summary>
        public static float SafeTrackEm(TMP_FontAsset font, string text, float desiredTrackEm, float minGapEm)
        {
            if (desiredTrackEm <= 0f) return 0f;
            if (font == null || string.IsNullOrEmpty(text) || text.Length < 2) return desiredTrackEm;
            // 動態字型:字要先進 atlas 才查得到 metrics(已存在的字會直接跳過,成本很低)。
            try { font.TryAddCharacters(text); } catch { /* static font asset / 失敗 → 下面查不到就當作不設限 */ }

            float allowed = desiredTrackEm;
            for (int i = 0; i + 1 < text.Length; i++)
            {
                if (!TryMetrics(font, text[i], out _, out float rsb)) continue;
                if (!TryMetrics(font, text[i + 1], out float lsb, out _)) continue;
                allowed = Mathf.Min(allowed, rsb + lsb - minGapEm);
            }
            return Mathf.Max(0f, allowed);
        }

        /// <summary>同上,但給 legacy <see cref="TextMesh"/> 的逐字佈局用(<see cref="Label3D"/>)。優先用
        /// <see cref="MeasureFont"/> 的真 metrics 量,這樣跟 TMP 那邊收一樣多;沒掛字型時退回 legacy
        /// <see cref="Font"/> 自己的點陣 metrics —— 那份含光柵化留白又被 GDI 塗粗過,只夠拿來要求「別重疊」
        /// (minGap 當 0),量出來會比實際保守。</summary>
        public static float SafeTrackEm(Font font, string text, int fontSize, FontStyle style, float desiredTrackEm, float minGapEm)
        {
            if (desiredTrackEm <= 0f) return 0f;
            if (MeasureFont != null) return SafeTrackEm(MeasureFont, text, desiredTrackEm, minGapEm);
            if (font == null || string.IsNullOrEmpty(text) || text.Length < 2 || fontSize <= 0) return desiredTrackEm;
            font.RequestCharactersInTexture(text, fontSize, style);   // 沒進 atlas 就量不到 metrics

            float allowed = desiredTrackEm;
            for (int i = 0; i + 1 < text.Length; i++)
            {
                if (!font.GetCharacterInfo(text[i], out CharacterInfo a, fontSize, style)) continue;
                if (!font.GetCharacterInfo(text[i + 1], out CharacterInfo b, fontSize, style)) continue;
                float gap = ((a.advance - a.bearing - a.glyphWidth) + b.bearing) / (float)fontSize;
                allowed = Mathf.Min(allowed, gap);   // 這份 metrics 已經很胖了,再扣 minGap 會把中文也歸零
            }
            return Mathf.Max(0f, allowed);
        }

        /// <summary>把安全收緊量套到一個 TMP 標籤上(TMP 的 characterSpacing 單位 = fontSize 的 1/100,
        /// 負值 = 收緊)。設完 <c>text</c> 之後呼叫 —— 收緊量取決於字串內容。</summary>
        public static void ApplyTightening(TMP_Text label, float desiredTrackEm, float minGapEm)
        {
            if (label == null) return;
            label.characterSpacing = -SafeTrackEm(label.font, label.text, desiredTrackEm, minGapEm) * 100f;
        }

        /// <summary>一個字的左右側距(sidebearing,em):左 = bearingX,右 = advance − bearingX − width。
        /// 主字型查不到就往 fallback 找(各自用自己的 pointSize 正規化成 em)。</summary>
        private static bool TryMetrics(TMP_FontAsset font, char ch, out float lsbEm, out float rsbEm)
        {
            lsbEm = rsbEm = 0f;
            if (font == null) return false;
            var lookup = font.characterLookupTable;
            if (lookup != null && lookup.TryGetValue(ch, out var c) && c != null && c.glyph != null)
            {
                float pt = font.faceInfo.pointSize;
                if (pt <= 0f) return false;
                var m = c.glyph.metrics;
                lsbEm = m.horizontalBearingX / pt;
                rsbEm = (m.horizontalAdvance - m.horizontalBearingX - m.width) / pt;
                return true;
            }
            var fallbacks = font.fallbackFontAssetTable;
            if (fallbacks != null)
                foreach (var f in fallbacks)
                    if (f != null && f != font && TryMetrics(f, ch, out lsbEm, out rsbEm)) return true;
            return false;
        }
    }
}
