using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    /// <summary>
    /// 舞者的「跳/停」閘門。本機與遠端**共用同一個函式**,所以這一組測試同時守住兩邊。
    ///
    /// 🔴 這裡最重要的一條是最後那組:「同一組輸入,本機路徑與遠端推導的結果必須一樣」。
    /// 那正是把規則抽出來的理由 —— 兩邊各寫一份的話,門檻一改就會變成
    /// 「別人的角色跳的跟他自己看到的不一樣」,而那種 bug 沒有人回報得清楚。
    /// </summary>
    public class DanceGateTests
    {
        // ---- 規則本體 ----

        [Test]
        public void A_Clean_Block_With_Notes_Dances()
        {
            Assert.IsTrue(DanceGate.Next(dancing: false, hadBreak: false, hadNote: true, combo: 0),
                "沒斷連又有音符 → 開始/恢復跳舞(combo 多少都不重要)");
        }

        [Test]
        public void Breaking_With_A_Strong_Combo_Keeps_Dancing()
        {
            // 斷了但 combo 還撐得住 → 繼續跳。門檻是**大於** 30。
            Assert.IsTrue(DanceGate.Next(true, hadBreak: true, hadNote: true, combo: 31));
            Assert.IsFalse(DanceGate.Next(true, hadBreak: true, hadNote: true, combo: 30),
                "剛好 30 不算「強」—— 門檻是嚴格大於");
            Assert.IsFalse(DanceGate.Next(true, hadBreak: true, hadNote: true, combo: 0));
        }

        [Test]
        public void A_Break_Overrides_The_Clean_Note_Rule()
        {
            // 同一個 block 裡既有音符又有斷連 → 走「斷連」那條(不是「有音符就跳」)。
            Assert.IsFalse(DanceGate.Next(true, hadBreak: true, hadNote: true, combo: 5));
        }

        [Test]
        public void An_Empty_Block_Holds_The_Current_State()
        {
            // 沒斷也沒音符(間奏/休息段)→ 維持現狀,兩個方向都要維持。
            Assert.IsTrue(DanceGate.Next(true, false, false, 0), "本來在跳 → 繼續跳");
            Assert.IsFalse(DanceGate.Next(false, false, false, 999), "本來站著 → 繼續站(combo 高也不會自己復活)");
        }

        // ---- 結算節奏 ----

        [Test]
        public void The_Settle_Interval_Is_Eight_Beats()
        {
            // 120 BPM → 一拍 500ms → 8 拍 = 4000ms。與計分結算同一個節奏。
            Assert.AreEqual(4000.0, DanceGate.SettleMs(120.0), 0.001);
            Assert.AreEqual(2000.0, DanceGate.SettleMs(240.0), 0.001);
        }

        [Test]
        public void A_Nonsense_Bpm_Does_Not_Divide_By_Zero()
        {
            // 壞譜面的 BPM 可能是 0 或負的。回一個很大但有限的間隔(等於「這首歌不結算」),
            // 而不是 Infinity/NaN —— 那會讓 while (now >= next) 變成無限迴圈或永遠不跑。
            Assert.AreEqual(8.0 * 60000.0, DanceGate.SettleMs(0.0), 0.001);
            Assert.AreEqual(8.0 * 60000.0, DanceGate.SettleMs(-120.0), 0.001);
        }

        // ---- 遠端推導 ----

        [Test]
        public void Break_And_Note_Flags_Come_From_The_Delta_Between_Samples()
        {
            var prev = new DanceJudgeCounts(10, 5, 1, 0);   // total 16, breaks 1
            Assert.IsTrue(DanceGate.HadNote(prev, new DanceJudgeCounts(11, 5, 1, 0)), "多了一個 Perfect");
            Assert.IsFalse(DanceGate.HadNote(prev, prev), "完全沒動 → 沒有音符");
            Assert.IsTrue(DanceGate.HadBreak(prev, new DanceJudgeCounts(10, 5, 2, 0)), "多了一個 Bad");
            Assert.IsTrue(DanceGate.HadBreak(prev, new DanceJudgeCounts(10, 5, 1, 1)), "多了一個 Miss");
            Assert.IsFalse(DanceGate.HadBreak(prev, new DanceJudgeCounts(20, 9, 1, 0)), "只多了好判定 → 沒斷");
        }

        [Test]
        public void The_Remote_Derivation_Agrees_With_The_Local_Path()
        {
            // 🔴 這是整組測試的重點:同一組「發生了什麼」,本機(直接知道旗標)與遠端(只能看兩筆的差)
            // 必須得到**一樣**的結果。不一樣的話,別人畫面上的舞者就與本人看到的不同步。
            var cases = new[]
            {
                // prev,                              cur,                                combo, dancing
                new object[] { new DanceJudgeCounts(0, 0, 0, 0),   new DanceJudgeCounts(8, 2, 0, 0),  10,  false },  // 乾淨一段
                new object[] { new DanceJudgeCounts(8, 2, 0, 0),   new DanceJudgeCounts(9, 2, 1, 0),  40,  true  },  // 斷了但 combo 高
                new object[] { new DanceJudgeCounts(8, 2, 0, 0),   new DanceJudgeCounts(9, 2, 1, 0),  3,   true  },  // 斷了且 combo 低
                new object[] { new DanceJudgeCounts(8, 2, 1, 0),   new DanceJudgeCounts(8, 2, 1, 0),  20,  true  },  // 空 block(維持跳)
                new object[] { new DanceJudgeCounts(8, 2, 1, 0),   new DanceJudgeCounts(8, 2, 1, 0),  20,  false },  // 空 block(維持站)
                new object[] { new DanceJudgeCounts(0, 0, 0, 0),   new DanceJudgeCounts(0, 0, 0, 3),  0,   true  },  // 整段全 miss
            };

            foreach (var c in cases)
            {
                var prev = (DanceJudgeCounts)c[0];
                var cur = (DanceJudgeCounts)c[1];
                int combo = (int)c[2];
                bool dancing = (bool)c[3];

                bool local = DanceGate.Next(dancing,
                                            hadBreak: cur.Breaks > prev.Breaks,
                                            hadNote: cur.Total > prev.Total,
                                            combo: combo);
                bool remote = DanceGate.NextFromSamples(dancing, prev, cur, combo);
                Assert.AreEqual(local, remote,
                    "本機與遠端對同一組輸入的結論必須一致(prev.total=" + prev.Total + " cur.total=" + cur.Total
                    + " combo=" + combo + " dancing=" + dancing + ")");
            }
        }
    }
}
