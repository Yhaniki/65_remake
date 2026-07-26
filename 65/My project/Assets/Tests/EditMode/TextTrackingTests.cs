using System.Collections.Generic;
using NUnit.Framework;
using TMPro;
using Sdo.Game;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 歌名字距收緊的安全上限。歌單原本固定收緊 0.05 em,但官方字體 SimSun 的西文是半形等寬且幾乎沒有
    /// 側距 —— "TA" 天生只有 0.043 em 的空隙 —— 於是 "SOLID STATE SQUAD" 的 T 和 A 被收成一塊。
    /// 這裡鎖住:收緊量必須逐字串 clamp,窄字縫的字串自動退回接近自然字距,寬字縫(中文)不受影響。
    /// </summary>
    public class TextTrackingTests
    {
        private static List<float> Gaps(params float[] g) => new List<float>(g);

        [Test]
        public void WideGaps_KeepTheFullTightening()
        {
            // 全形中日文相鄰空隙 ≥0.08 em → 收 0.05 還留得下 0.03。
            Assert.AreEqual(0.05f, TextTracking.ClampTrackEm(Gaps(0.082f, 0.094f), 0.05f, 0.03f), 1e-5f);
        }

        [Test]
        public void NarrowGap_ClampsSoTheGlyphsStillHaveAir()
        {
            // "TA" = 0.043 em → 最多只能收 0.013,剩下的 0.03 就是 minGap。
            Assert.AreEqual(0.013f, TextTracking.ClampTrackEm(Gaps(0.043f), 0.05f, 0.03f), 1e-5f);
        }

        [Test]
        public void TightestGapInTheStringWins()
        {
            // 一串字只有一個 characterSpacing → 取最窄的字縫決定。
            Assert.AreEqual(0.013f, TextTracking.ClampTrackEm(Gaps(0.09f, 0.043f, 0.12f), 0.05f, 0.03f), 1e-5f);
        }

        [Test]
        public void GapNarrowerThanTheMinimum_FallsBackToNaturalSpacing()
        {
            // "mA" = 0.012 em,本來就比 minGap 窄 → 不收緊(也絕不撐開,結果不會是負的)。
            Assert.AreEqual(0f, TextTracking.ClampTrackEm(Gaps(0.012f), 0.05f, 0.03f), 1e-5f);
        }

        [Test]
        public void NoTighteningAsked_StaysZero()
        {
            Assert.AreEqual(0f, TextTracking.ClampTrackEm(Gaps(0.2f), 0f, 0.03f), 1e-5f);
            Assert.AreEqual(0f, TextTracking.ClampTrackEm(null, 0f, 0.03f), 1e-5f);
        }

        [Test]
        public void NothingMeasured_KeepsTheAskedAmount()
        {
            // 字縫量不到(字型沒載入/字不在 atlas)→ 不設限,維持原本要的收緊量。
            Assert.AreEqual(0.05f, TextTracking.ClampTrackEm(null, 0.05f, 0.03f), 1e-5f);
            Assert.AreEqual(0.05f, TextTracking.ClampTrackEm(Gaps(), 0.05f, 0.03f), 1e-5f);
        }

        [Test]
        public void SongTitleTighteningIsWithinTheSaneRange()
        {
            Assert.Greater(TextStyles.MinInkGapEm, 0f);
            Assert.Less(TextStyles.MinInkGapEm, TextStyles.SongTitleTrackEm + 0.05f);
        }

        // ---- 真實字型(需要機器上有 SimSun / 後備思源黑體)----

        [Test]
        public void RealFont_CapitalTA_TightensLessThanChinese()
        {
            var font = UIFont.Cjk;
            if (font == null) Assert.Ignore("沒有可用的 CJK 字型,跳過");

            float latin = TextTracking.SafeTrackEm(font, "SOLID STATE SQUAD", TextStyles.SongTitleTrackEm, TextStyles.MinInkGapEm);
            float cjk = TextTracking.SafeTrackEm(font, "戀愛達人的祕密", TextStyles.SongTitleTrackEm, TextStyles.MinInkGapEm);

            Assert.GreaterOrEqual(latin, 0f, "收緊量不會是負的(只收緊不撐開)");
            Assert.LessOrEqual(latin, cjk, "含 TA 的英文歌名必須收得比中文歌名少");
            Assert.AreEqual(TextStyles.SongTitleTrackEm, cjk, 1e-5f, "全形中文的字縫夠寬 → 維持原本的收緊量");
        }

        [Test]
        public void RealFont_TighteningNeverEatsTheInkGap()
        {
            var font = UIFont.Cjk;
            if (font == null) Assert.Ignore("沒有可用的 CJK 字型,跳過");

            // 這串字每個大寫組合都很窄(TA / AT / AV / mw),收緊後不得讓任何一對字的墨水碰到。
            foreach (var s in new[] { "SOLID STATE SQUAD", "The Great Artist", "AT AT AV mw" })
            {
                float track = TextTracking.SafeTrackEm(font, s, TextStyles.SongTitleTrackEm, TextStyles.MinInkGapEm);
                Assert.GreaterOrEqual(track, 0f, s);
                Assert.LessOrEqual(track, TextStyles.SongTitleTrackEm + 1e-5f, s);
                float room = System.Math.Max(0f, NarrowestGapEm(font, s) - TextStyles.MinInkGapEm);
                Assert.LessOrEqual(track, room + 1e-5f, s + " 收得比最窄的字縫還多 → 字會黏在一起");
            }
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
