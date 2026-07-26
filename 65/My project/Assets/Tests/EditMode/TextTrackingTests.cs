using System.Collections.Generic;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using Sdo.Game;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 歌名字距的安全範圍。歌單原本固定收緊 0.05 em,但官方字體 SimSun 的西文是半形等寬且幾乎沒有側距 ——
    /// "TA" 天生只有 0.043 em(14px 字級下 0.60 個像素)—— 於是 "SOLID STATE SQUAD" 的 T 和 A 被收成一塊。
    /// 光是「不收緊」還不夠:0.60px 的縫渲染出來只有 1 個半亮像素,人眼看仍是黏著。所以這裡鎖住兩件事:
    ///   1. 字縫夠寬(中文)→ 維持原本的收緊量,一點都不能變鬆。
    ///   2. 字縫天生過窄(窄西文對)→ 必須**撐開**到看得見的縫,回傳負值。
    /// 門檻用顯示像素:0.75px 以下算過窄,撐到 1.2px。中文最窄的相鄰組合是 0.88px,一個都不會被誤傷。
    /// </summary>
    public class TextTrackingTests
    {
        private const float SongPx = 14f;    // 歌單字級 —— 判斷「窄到會黏」要用畫出來的大小

        private static List<float> Gaps(params float[] g) => new List<float>(g);

        private TMP_FontAsset _savedMeasureFont;

        [SetUp] public void SaveMeasureFont() => _savedMeasureFont = TextTracking.MeasureFont;
        [TearDown] public void RestoreMeasureFont() => TextTracking.MeasureFont = _savedMeasureFont;

        [Test]
        public void WideGaps_KeepTheFullTightening()
        {
            // 全形中日文相鄰空隙 ≥0.08 em → 收 0.05 還留得下 0.03。
            Assert.AreEqual(0.05f, TextTracking.ClampTrackEm(Gaps(0.082f, 0.094f), 0.05f, 0.03f, SongPx), 1e-5f);
        }

        [Test]
        public void WorstCaseChinese_StillTightens_NeverExpands()
        {
            // 量到的中文最壞相鄰組合(達/語)= 0.0625 em = 0.88px @14 —— 仍在 0.75px 門檻之上,
            // 所以走的是收緊那條路(收到只剩 minGap),絕不能因為改了防黏規則就變成撐開。
            float track = TextTracking.ClampTrackEm(Gaps(0.0625f), 0.05f, 0.03f, SongPx);
            Assert.AreEqual(0.0325f, track, 1e-5f);
            Assert.Greater(track, 0f, "中文不該被撐開");
        }

        [Test]
        public void NarrowGap_IsExpandedNotJustLeftAlone()
        {
            // "TA" = 0.043 em = 0.60px @14 → 低於 0.75px 門檻 → 撐開到 1.2px(=0.0857em),回傳負值。
            float track = TextTracking.ClampTrackEm(Gaps(0.043f), 0.05f, 0.03f, SongPx);
            Assert.AreEqual(0.043f - 1.2f / SongPx, track, 1e-5f);
            Assert.Less(track, 0f, "天生不夠的字縫必須撐開,不是只有『不收緊』");
        }

        [Test]
        public void NeediestGapInTheStringWins()
        {
            // 一串字只有一個 characterSpacing → 取最需要空間的字縫決定(這裡是 0.043 的那個)。
            Assert.AreEqual(0.043f - 1.2f / SongPx,
                            TextTracking.ClampTrackEm(Gaps(0.09f, 0.043f, 0.12f), 0.05f, 0.03f, SongPx), 1e-5f);
        }

        [Test]
        public void VeryNarrowGap_ExpandsFurther_ButNeverBeyondTheCap()
        {
            // "mA" = 0.012 em(0.17px @14)→ 撐開量更大,但不得超過 MaxExpandEm。
            float track = TextTracking.ClampTrackEm(Gaps(0.012f), 0.05f, 0.03f, SongPx);
            Assert.AreEqual(0.012f - 1.2f / SongPx, track, 1e-5f);
            Assert.GreaterOrEqual(track, -TextTracking.MaxExpandEm);

            // 墨水完全填滿字身(字縫 0)→ 撐開量被上限擋住,不會把整串撐得離譜。
            Assert.AreEqual(-TextTracking.MaxExpandEm, TextTracking.ClampTrackEm(Gaps(0f), 0.05f, 0.03f, SongPx), 1e-5f);
        }

        [Test]
        public void BigFontSize_NeedsNoExpansion_TheSameGapIsPlentyOfPixels()
        {
            // 同一個 0.043 em 字縫:14px 下只有 0.60px(要撐開),48px 下有 2.06px(夠寬,照收緊)。
            // 這就是門檻必須用顯示像素、不能用 em 的原因。
            Assert.Less(TextTracking.ClampTrackEm(Gaps(0.043f), 0.05f, 0.03f, 14f), 0f);
            Assert.AreEqual(0.013f, TextTracking.ClampTrackEm(Gaps(0.043f), 0.05f, 0.03f, 48f), 1e-5f);
        }

        [Test]
        public void NoTighteningAsked_StillPreventsGlyphsFromTouching()
        {
            // 沒要求收緊 ≠ 可以放著黏 —— 字縫過窄照樣撐開。
            Assert.AreEqual(0.043f - 1.2f / SongPx, TextTracking.ClampTrackEm(Gaps(0.043f), 0f, 0.03f, SongPx), 1e-5f);
            // 字縫夠寬又沒要求收緊 → 什麼都不做。
            Assert.AreEqual(0f, TextTracking.ClampTrackEm(Gaps(0.2f), 0f, 0.03f, SongPx), 1e-5f);
            Assert.AreEqual(0f, TextTracking.ClampTrackEm(null, 0f, 0.03f, SongPx), 1e-5f);
        }

        [Test]
        public void NothingMeasured_KeepsTheAskedAmount()
        {
            // 字縫量不到(字型沒載入/字不在 atlas)→ 不設限,維持原本要的收緊量。
            Assert.AreEqual(0.05f, TextTracking.ClampTrackEm(null, 0.05f, 0.03f, SongPx), 1e-5f);
            Assert.AreEqual(0.05f, TextTracking.ClampTrackEm(Gaps(), 0.05f, 0.03f, SongPx), 1e-5f);
        }

        [Test]
        public void UnknownDisplaySize_FallsBackToTheEmOnlyFloor()
        {
            // 不知道畫多大 → 沒有像素可判斷,退回純 em 的收緊底線(只收不撐)。
            Assert.AreEqual(0.013f, TextTracking.ClampTrackEm(Gaps(0.043f), 0.05f, 0.03f, 0f), 1e-5f);
        }

        [Test]
        public void SongTitleTighteningIsWithinTheSaneRange()
        {
            Assert.Greater(TextStyles.MinInkGapEm, 0f);
            Assert.Less(TextStyles.MinInkGapEm, TextStyles.SongTitleTrackEm + 0.05f);
            // 門檻必須落在「中文最窄 0.88px」之下、「西文 AT 0.66px」之上,否則不是誤傷中文就是漏掉 TA/AT。
            Assert.Greater(TextStyles.NarrowInkGapPx, 0.66f, "門檻太低 → 西文 AT 不會被撐開");
            Assert.Less(TextStyles.NarrowInkGapPx, 0.88f, "門檻太高 → 中文會被誤判成過窄而變鬆");
            Assert.Greater(TextStyles.SafeInkGapPx, TextStyles.NarrowInkGapPx, "撐開的目標要比門檻寬,否則撐了等於沒撐");
            // 逐字對的目標要比整串的下限大 —— 整串會被最窄的那對綁住,逐字對才補得起真正黏的那對。
            Assert.Greater(TextStyles.SongTitleOpticalGapPx, TextStyles.SafeInkGapPx);
            Assert.Less(TextStyles.SongTitleOpticalGapPx, 4f, "補太多會讓西文歌名散開");
        }

        // ---- 真實字型(需要機器上有 SimSun / 後備思源黑體)----

        [Test]
        public void RealFont_CapitalTA_IsExpanded_WhileChineseStillTightens()
        {
            var font = UIFont.Cjk;
            if (font == null) Assert.Ignore("沒有可用的 CJK 字型,跳過");

            float latin = TextTracking.SafeTrackEm(font, "SOLID STATE SQUAD", TextStyles.SongTitleTrackEm, TextStyles.MinInkGapEm, SongPx);
            float cjk = TextTracking.SafeTrackEm(font, "戀愛達人的祕密", TextStyles.SongTitleTrackEm, TextStyles.MinInkGapEm, SongPx);

            Assert.Less(latin, 0f, "SimSun 的 TA/UA 在 14px 下天生不夠 → 必須撐開");
            Assert.AreEqual(TextStyles.SongTitleTrackEm, cjk, 1e-5f, "中文的字縫夠寬 → 維持原本的收緊量");
        }

        [Test]
        public void RealFont_EveryGapEndsUpWithEnoughInk()
        {
            var font = UIFont.Cjk;
            if (font == null) Assert.Ignore("沒有可用的 CJK 字型,跳過");

            // 這串字每個大寫組合都很窄(TA / AT / AV / mw),套用後不得讓任何一對字的墨水碰到。
            foreach (var s in new[] { "SOLID STATE SQUAD", "The Great Artist", "AT AT AV mw" })
            {
                float track = TextTracking.SafeTrackEm(font, s, TextStyles.SongTitleTrackEm, TextStyles.MinInkGapEm, SongPx);
                Assert.GreaterOrEqual(track, -TextTracking.MaxExpandEm, s);
                Assert.LessOrEqual(track, TextStyles.SongTitleTrackEm + 1e-5f, s);

                float narrowest = NarrowestGapEm(font, s);
                float resulting = (narrowest - track) * SongPx;      // 套用後這個字縫還剩幾個像素
                Assert.Greater(resulting, 0.9f, s + " 最窄的字縫套用後仍不到 1 個像素 → 看起來還是黏的");
            }
        }

        // ---- 逐字對光學字距(歌單實際走的路)----

        [Test]
        public void Optical_ChineseIsUntouched()
        {
            var font = UIFont.Cjk;
            if (font == null) Assert.Ignore("沒有可用的 CJK 字型,跳過");

            // 全形中文靠字形辨識,縫不是它的可讀性來源 —— 撐它會把中文歌名整個拆散。一個標籤都不該插。
            const string s = "戀愛達人的祕密";
            Assert.AreEqual(s, TextTracking.OpticalText(font, s, SongPx, TextStyles.SongTitleOpticalGapPx));
        }

        [Test]
        public void Optical_NarrowLatinPairGetsExactlyWhatItLacks()
        {
            var font = UIFont.Cjk;
            if (font == null) Assert.Ignore("沒有可用的 CJK 字型,跳過");

            float gap = NarrowestGapEm(font, "TA");
            string s = TextTracking.OpticalText(font, "TA", SongPx, TextStyles.SongTitleOpticalGapPx);
            float want = TextStyles.SongTitleOpticalGapPx / SongPx - gap;
            Assert.AreEqual(want, FirstCspaceEm(s), 1e-3f, "補的量要剛好把這一對補到目標,不多不少");
        }

        [Test]
        public void Optical_WhitespacePairsAreNotPushedApart()
        {
            var font = UIFont.Cjk;
            if (font == null) Assert.Ignore("沒有可用的 CJK 字型,跳過");

            // 空格沒有墨水,"A␣" / "␣A" 不是墨水縫而是詞間距 —— 撐它只會讓字散開。
            // (整串撐開的舊做法正是被 "E␣"(0.44px)這個假字縫綁住的。)
            Assert.AreEqual("A A", TextTracking.OpticalText(font, "A A", SongPx, TextStyles.SongTitleOpticalGapPx));
        }

        [Test]
        public void Optical_WideEnoughPairIsLeftAlone()
        {
            var font = UIFont.Cjk;
            if (font == null) Assert.Ignore("沒有可用的 CJK 字型,跳過");

            // "LI" 天生 1.75px @14 → 已經比目標寬,一點都不該補(整串撐開會連它一起撐,這正是要避免的)。
            Assert.AreEqual(0f, FirstCspaceEm(TextTracking.OpticalText(font, "LI", SongPx, 1.4f)), 1e-4f);
        }

        [Test]
        public void Optical_TextWithMarkupCharIsPassedThroughUntouched()
        {
            var font = UIFont.Cjk;
            if (font == null) Assert.Ignore("沒有可用的 CJK 字型,跳過");

            // 歌名自己帶 '<' 的話,插標籤會讓 TMP 把整段當標記解析 → 寧可不做光學字距。
            const string s = "A<B TA";
            Assert.AreEqual(s, TextTracking.OpticalText(font, s, SongPx, TextStyles.SongTitleOpticalGapPx));
        }

        [Test]
        public void Optical_EveryLatinPairReachesTheTarget_AndFitsTheColumn()
        {
            var font = UIFont.Cjk;
            if (font == null) Assert.Ignore("沒有可用的 CJK 字型,跳過");

            const string s = "SOLID STATE SQUAD";
            float target = TextStyles.SongTitleOpticalGapPx / SongPx;
            float extraTotal = 0f;
            for (int i = 0; i + 1 < s.Length; i++)
            {
                if (char.IsWhiteSpace(s[i]) || char.IsWhiteSpace(s[i + 1])) continue;
                float gap = NarrowestGapEm(font, s.Substring(i, 2));
                float resulting = Mathf.Max(gap, target);
                extraTotal += Mathf.Max(0f, target - gap);
                Assert.GreaterOrEqual(resulting * SongPx, TextStyles.SongTitleOpticalGapPx - 1e-3f,
                                      s.Substring(i, 2) + " 補完後仍不到目標");
            }
            // 歌名欄寬 252px,半形西文一個字 7px:補完也必須遠遠塞得下,否則會撞到右邊的時間欄。
            float width = s.Length * (SongPx * 0.5f) + extraTotal * SongPx;
            Assert.Less(width, 252f, "光學字距補完後的歌名寬度超出欄位");
        }

        /// <summary>抓出 rich text 裡第一個 <c>&lt;cspace=…em&gt;</c> 的值(沒有標籤 = 0)。</summary>
        private static float FirstCspaceEm(string s)
        {
            int i = s.IndexOf("<cspace=");
            if (i < 0) return 0f;
            int j = s.IndexOf("em>", i);
            if (j < 0) return 0f;
            return float.Parse(s.Substring(i + 8, j - i - 8), System.Globalization.CultureInfo.InvariantCulture);
        }

        // ---- legacy TextMesh 那條路(遊戲內頭頂名字 / 排名榜,Label3D 逐字佈局)----

        [Test]
        public void LegacyFont_MeasuresWithTheRealMetrics_SoBothPathsTrackTheSame()
        {
            var font = TextStyles.CjkFont();
            var tmp = UIFont.Cjk;
            if (font == null || tmp == null) Assert.Ignore("沒有可用的 CJK 字型,跳過");
            TextTracking.MeasureFont = tmp;

            foreach (var s in new[] { "SOLID STATE SQUAD", "戀愛達人的祕密" })
            {
                float legacy = TextTracking.SafeTrackEm(font, s, 48, FontStyle.Bold, TextStyles.HeadNameTrackEm, TextStyles.MinInkGapEm, 22f);
                float viaTmp = TextTracking.SafeTrackEm(tmp, s, TextStyles.HeadNameTrackEm, TextStyles.MinInkGapEm, 22f);
                Assert.AreEqual(viaTmp, legacy, 1e-5f, s + ":房間(TMP)和遊戲內(TextMesh)必須套一樣多");
            }
        }

        [Test]
        public void LegacyFont_WithoutMeasureFont_FallsBackToItsOwnBitmapMetrics()
        {
            var font = TextStyles.CjkFont();
            if (font == null) Assert.Ignore("沒有可用的 CJK 字型,跳過");
            TextTracking.MeasureFont = null;   // 沒掛量測字型 → 只能用 legacy 自己那份(含光柵留白 + GDI 塗粗,偏胖)

            float latin = TextTracking.SafeTrackEm(font, "SOLID STATE SQUAD", 48, FontStyle.Bold, TextStyles.HeadNameTrackEm, TextStyles.MinInkGapEm, 22f);
            float cjk = TextTracking.SafeTrackEm(font, "戀愛達人的祕密", 48, FontStyle.Bold, TextStyles.HeadNameTrackEm, TextStyles.MinInkGapEm, 22f);

            Assert.LessOrEqual(latin, cjk, "含 TA 的西文名字必須收得比中文名字少");
            Assert.LessOrEqual(cjk, TextStyles.HeadNameTrackEm + 1e-5f, "收緊量不會超過要求的量");
        }

        [Test]
        public void HeadNameLabel_LatinGetsRoom_ChineseStillTightens()
        {
            var font = TextStyles.CjkFont();
            if (font == null || UIFont.Cjk == null) Assert.Ignore("沒有可用的 CJK 字型,跳過");
            TextTracking.MeasureFont = UIFont.Cjk;

            // 名牌收得比歌名更狠(0.1em):西文的字縫根本沒有 0.1em 可收 → 貼著自然 advance 甚至再撐開;
            // 中文的字縫夠寬 → 照收。
            float latinSlack = AdvanceSlack("SOLID STATE SQUAD", font);
            float cjkSlack = AdvanceSlack("戀愛達人的祕密", font);

            Assert.Less(latinSlack, 0.02f, "西文名字幾乎不能再收（字縫不夠）→ 位置應該貼著自然 advance");
            Assert.Greater(cjkSlack, 0.02f, "中文名字的字縫夠寬 → 還是要看得出收緊");
        }

        /// <summary>把一個 <see cref="Label3D"/> 排出來,回傳「每個字縫實際收掉了幾個 em」(0 = 自然字距,負 = 撐開)。</summary>
        private static float AdvanceSlack(string text, Font font)
        {
            var label = TextStyles.NewLabel("tt", TextStyles.Style.HeadName, 0, 22f, TextAnchor.MiddleLeft);
            try
            {
                label.Text = text;
                var a = Cell(label, 0); var b = Cell(label, 1);
                if (a == null || b == null) Assert.Ignore("Label3D 沒有建出逐字 cell,跳過");
                if (!font.GetCharacterInfo(text[0], out CharacterInfo ia, a.fontSize, FontStyle.Bold))
                    Assert.Ignore("量不到 legacy 字型 metrics,跳過");
                float worldPerPx = 0.1f * a.characterSize;
                float natural = ia.advance * worldPerPx;
                float actual = b.transform.localPosition.x - a.transform.localPosition.x;
                return (natural - actual) / (a.fontSize * worldPerPx);   // 收掉的量,換算成 em
            }
            finally { Object.DestroyImmediate(label.root); }
        }

        private static TextMesh Cell(Label3D label, int k)
        {
            var tr = label.root.transform.Find("c" + k);
            return tr != null ? tr.GetComponent<TextMesh>() : null;
        }

        // ---- 房間頭頂名字(TMP OutlinedLabel)----

        [Test]
        public void OutlinedLabel_ReDerivesTheTrackingPerString()
        {
            var rootGo = new GameObject("tt_root", typeof(RectTransform));
            try
            {
                var label = OutlinedLabel.Create(rootGo.transform, "n", 0f, 0f, 160f, 20f, 14f,
                    Color.white, Color.black, 1.4f, true, trackEm: TextStyles.HeadNameTrackEm);
                if (label.Face == null || label.Face.font == null) Assert.Ignore("沒有可用的 CJK 字型,跳過");

                label.SetText("戀愛達人的祕密");
                float cjk = -label.Face.characterSpacing / 100f;
                label.SetText("SOLID STATE SQUAD");
                float latin = -label.Face.characterSpacing / 100f;

                Assert.Less(latin, cjk, "含 TA 的西文名字必須比中文名字鬆");

                // 描邊層是臉層的偏移複本 → 字距不同步的話,黑邊會從字上飄開。
                foreach (var t in rootGo.GetComponentsInChildren<TextMeshProUGUI>(true))
                    Assert.AreEqual(label.Face.characterSpacing, t.characterSpacing, 1e-4f, "描邊層的字距沒跟著臉層");
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        /// <summary>獨立重算一次字串裡最窄的墨水字縫(em),當作上面那條斷言的對照組。</summary>
        private static float NarrowestGapEm(TMP_FontAsset font, string s)
        {
            font.TryAddCharacters(s);
            float min = float.MaxValue;
            for (int i = 0; i + 1 < s.Length; i++)
            {
                if (!font.characterLookupTable.TryGetValue(s[i], out var a) || a?.glyph == null) continue;
                if (!font.characterLookupTable.TryGetValue(s[i + 1], out var b) || b?.glyph == null) continue;
                float pt = font.faceInfo.pointSize;
                var ma = a.glyph.metrics; var mb = b.glyph.metrics;
                float gap = ((ma.horizontalAdvance - ma.horizontalBearingX - ma.width) + mb.horizontalBearingX) / pt;
                if (gap < min) min = gap;
            }
            return min == float.MaxValue ? float.MaxValue : min;
        }
    }
}
