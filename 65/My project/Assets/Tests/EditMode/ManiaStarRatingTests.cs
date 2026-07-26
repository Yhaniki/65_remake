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

        private static OsuBeatmap Stream(int count, int stepMs)
        {
            var bm = new OsuBeatmap { Keys = 4 };
            for (int i = 0; i < count; i++)
                bm.HitObjects.Add(new OsuHitObject(i % 4, 500 + i * stepMs));
            return bm;
        }
    }
}
