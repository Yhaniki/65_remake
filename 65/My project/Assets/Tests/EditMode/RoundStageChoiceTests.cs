using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Net;
using Sdo.UI.Catalog;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    /// <summary>
    /// 「這一局跑哪個場景」的解析(<see cref="RoundStageChoice.Pick"/>)。
    ///
    /// 為什麼值得一條測試:這一步的結果會**離開**房間設定 —— 它只餵 gameplay 的 scenePath,
    /// 房間 win2 那張場景縮圖照樣讀設定。弄錯不會報錯,只會讓「選了隨機場景」變成
    /// 「進遊戲那一刻縮圖跳成抽到的那張,而且下一局起永遠是同一個場景」。
    /// </summary>
    public class RoundStageChoiceTests
    {
        private const int Max = NetLimits.MaxSceneId;

        /// <summary>固定回傳序列的假 RNG,順便記下被要求的區間(要驗上界是含 Max 的)。</summary>
        private sealed class FakeRng
        {
            private readonly Queue<int> _values;
            public int LastMin = -1, LastMaxExclusive = -1;
            public int Calls;
            public FakeRng(params int[] values) { _values = new Queue<int>(values); }
            public int Range(int min, int maxExclusive)
            {
                Calls++;
                LastMin = min; LastMaxExclusive = maxExclusive;
                return _values.Count > 0 ? _values.Dequeue() : min;
            }
        }

        [Test]
        public void Fixed_Scene_Is_Used_As_Is_And_Never_Rolls()
        {
            var rng = new FakeRng(7);
            Assert.AreEqual(19, RoundStageChoice.Pick(false, 19, Max, rng.Range));
            Assert.AreEqual(0, rng.Calls, "指定場景不該去抽");
        }

        [Test]
        public void Random_Ignores_The_Placeholder_Id()
        {
            // 🔴 隨機時 settingSceneId 裡放的是上一次抽的佔位值。拿它當結果 = 只抽一次,
            // 之後每一局都是同一個場景(正是這條路徑要避免的)。
            var rng = new FakeRng(3);
            Assert.AreEqual(3, RoundStageChoice.Pick(true, 19, Max, rng.Range));
        }

        [Test]
        public void Random_Can_Roll_The_Highest_Scene()
        {
            var rng = new FakeRng(Max);
            Assert.AreEqual(Max, RoundStageChoice.Pick(true, 0, Max, rng.Range));
            Assert.AreEqual(0, rng.LastMin);
            Assert.AreEqual(Max + 1, rng.LastMaxExclusive, "上界是含 Max 的 → 要用 Max+1 當 exclusive");
        }

        [Test]
        public void Random_Rerolls_Every_Round()
        {
            var rng = new FakeRng(2, 11, 30);
            Assert.AreEqual(2, RoundStageChoice.Pick(true, 9, Max, rng.Range));
            Assert.AreEqual(11, RoundStageChoice.Pick(true, 9, Max, rng.Range));
            Assert.AreEqual(30, RoundStageChoice.Pick(true, 9, Max, rng.Range));
        }

        [Test]
        public void Out_Of_Range_Values_Are_Clamped()
        {
            var rng = new FakeRng(99);
            Assert.AreEqual(Max, RoundStageChoice.Pick(true, 0, Max, rng.Range), "壞掉的 RNG 也不能送出越界的 sceneId");
            Assert.AreEqual(0, RoundStageChoice.Pick(false, -5, Max, null));
            Assert.AreEqual(Max, RoundStageChoice.Pick(false, 999, Max, null));
        }

        [Test]
        public void No_Rng_Falls_Back_Instead_Of_Throwing()
        {
            Assert.AreEqual(9, RoundStageChoice.Pick(true, 9, Max, null));
        }

        [Test]
        public void Every_Rollable_Id_Maps_To_A_Real_Stage_Folder()
        {
            // 抽出來的 id 一路餵到 StageCatalog.Get → scenePath;範圍內每一個都得有資料夾,
            // 否則某些局會開在 Default(SCN0009)上而沒有人發現。
            for (int id = 0; id <= Max; id++)
            {
                var st = StageCatalog.Get(id);
                Assert.AreEqual(id, st.Id, "scene id " + id + " 在 StageCatalog 裡沒有對應項");
                Assert.IsFalse(string.IsNullOrEmpty(st.Folder), "scene id " + id + " 沒有資料夾");
            }
        }
    }
}
