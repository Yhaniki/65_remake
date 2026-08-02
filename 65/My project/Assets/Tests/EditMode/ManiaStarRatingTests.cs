using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    public class ManiaStarRatingTests
    {
        // Level = round(star × 7) clamped 1..999.
        [Test]
        public void LevelFromStar_Examples_And_Clamps()
        {
            Assert.AreEqual(16, ManiaStarRating.LevelFromStar(2.35));   // 16.45 → 16
            Assert.AreEqual(35, ManiaStarRating.LevelFromStar(5.0));
            Assert.AreEqual(56, ManiaStarRating.LevelFromStar(8.0));
            Assert.AreEqual(4, ManiaStarRating.LevelFromStar(0.5));     // 3.5 → 4 (rounds away from zero)
            Assert.AreEqual(1, ManiaStarRating.LevelFromStar(0.0));     // clamp min
            Assert.AreEqual(350, ManiaStarRating.LevelFromStar(50.0));  // 天花板 999 → 350 是真的值，不再被壓成 99
            Assert.AreEqual(999, ManiaStarRating.LevelFromStar(200.0)); // clamp max (1400)
        }

        // 99 以前是天花板，星數 ≥ 14.15 的譜全部擠在同一個數字上；現在它只是個普通等級。
        [Test]
        public void LevelFromStar_Above_99_Is_Not_Flattened()
        {
            Assert.AreEqual(99, ManiaStarRating.LevelFromStar(14.2));    // 99.4 → 99
            Assert.AreEqual(100, ManiaStarRating.LevelFromStar(14.3));   // 100.1 → 100，會超過舊上限
            Assert.AreEqual(140, ManiaStarRating.LevelFromStar(20.0));
        }

        [Test]
        public void Empty_Chart_Is_Zero_Star()
        {
            Assert.AreEqual(0.0, ManiaStarRating.Calculate(new OsuBeatmap { Keys = 4 }), 1e-9);
        }

        [Test]
        public void Denser_Chart_Rates_Higher()
        {
            var sparse = Stream(20, 400);   // 20 notes, 400ms apart
            var dense = Stream(20, 120);    // 20 notes, 120ms apart
            double s1 = ManiaStarRating.Calculate(sparse);
            double s2 = ManiaStarRating.Calculate(dense);
            Assert.Greater(s2, s1, "a faster stream should rate higher");
            Assert.GreaterOrEqual(ManiaStarRating.Level(dense), 1);
        }

        // 炸彈是要避開的（永遠不判定，踩到只扣血），所以完全不能進 strain —— 它進去會雙重灌水：自己加一份
        // strain，又縮短後面真音符的間隔讓衰減來不及發生。灑滿雷的慢譜曾經因此從 LV6 虛高到 LV44。
        [Test]
        public void Bombs_Do_Not_Change_The_Raw_Star_Rating()
        {
            Assert.AreEqual(ManiaStarRating.Calculate(Stream(40, 300)), ManiaStarRating.Calculate(Mined(40, 300)), 1e-9,
                "炸彈不判定，不能進 strain");
        }

        // ……但顯示的星數/等級**要**看得出來：同一張譜灑了雷就是比較難打（要閃），只是加得很小
        // （<= +4%，見 ChartDifficultyBonus.BombMax）。
        [Test]
        public void Bombs_Nudge_The_Displayed_Star_Rating_Up()
        {
            var plain = Stream(40, 300);
            var mined = Mined(40, 300);
            double a = ManiaStarRating.CalculateAdjusted(plain), b = ManiaStarRating.CalculateAdjusted(mined);
            Assert.Greater(b, a, "灑滿雷的譜顯示星數要略高");
            Assert.LessOrEqual(b, a * (1.0 + ChartDifficultyBonus.BombMax) + 1e-9, "但只能是「略」高");
            Assert.GreaterOrEqual(ManiaStarRating.Level(mined), ManiaStarRating.Level(plain));
        }

        // 只有炸彈的譜＝沒有一顆打得到的音符 → 0 星（跟空譜同義）。
        [Test]
        public void All_Bomb_Chart_Is_Zero_Star()
        {
            var bm = new OsuBeatmap { Keys = 4 };
            for (int i = 0; i < 40; i++)
                bm.HitObjects.Add(new OsuHitObject(i % 4, 500 + i * 120, null, isBomb: true));
            Assert.AreEqual(0.0, ManiaStarRating.Calculate(bm), 1e-9);
        }

        private static OsuBeatmap Stream(int count, int stepMs)
        {
            var bm = new OsuBeatmap { Keys = 4 };
            for (int i = 0; i < count; i++)
                bm.HitObjects.Add(new OsuHitObject(i % 4, 500 + i * stepMs));
            return bm;
        }

        // 同一條 stream，但每個真音符之間塞 3 顆炸彈（其餘三條 lane），密度是音符的三倍。
        private static OsuBeatmap Mined(int count, int stepMs)
        {
            var bm = Stream(count, stepMs);
            for (int i = 0; i < count; i++)
                for (int lane = 0; lane < 4; lane++)
                    if (lane != i % 4)
                        bm.HitObjects.Add(new OsuHitObject(lane, 500 + i * stepMs + stepMs / 2, null, isBomb: true));
            bm.HitObjects.Sort((a, b) => a.StartTimeMs.CompareTo(b.StartTimeMs));
            return bm;
        }
    }
}
