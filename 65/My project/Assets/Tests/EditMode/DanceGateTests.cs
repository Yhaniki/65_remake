using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    /// <summary>
    /// 舞者跳舞/停舞的 8 拍結算決策(<see cref="DanceGate.NextState"/>)。官方規則＋config.ini 的
    /// 「掉 miss 也照跳舞」(opt_danceIgnoreMiss) 豁免,再加上遠端舞者的推導。
    ///
    /// 🔴 這裡最重要的一條是最後那組:「同一組輸入,本機路徑與遠端推導的結果必須一樣」。
    /// 那正是把規則抽成純函式的理由 —— 兩邊各寫一份的話,門檻一改就會變成
    /// 「別人的角色跳的跟他自己看到的不一樣」,而那種 bug 沒有人回報得清楚。
    /// </summary>
    public class DanceGateTests
    {
        // ---- 官方規則(ignoreMiss = false)----

        [Test]
        public void Break_With_Strong_Combo_Keeps_Dancing()
        {
            Assert.IsTrue(DanceGate.NextState(dancing: true, hadBreak: true, hadNote: true, combo: 31, ignoreMiss: false));
        }

        [Test]
        public void Break_With_Weak_Combo_Stops_Dancing()
        {
            Assert.IsFalse(DanceGate.NextState(dancing: true, hadBreak: true, hadNote: true, combo: 30, ignoreMiss: false),
                "門檻是 combo > 30,剛好 30 要停");
            Assert.IsFalse(DanceGate.NextState(dancing: true, hadBreak: true, hadNote: true, combo: 0, ignoreMiss: false));
        }

        [Test]
        public void Clean_Block_With_Notes_Resumes_Dancing()
        {
            Assert.IsTrue(DanceGate.NextState(dancing: false, hadBreak: false, hadNote: true, combo: 1, ignoreMiss: false),
                "乾淨的 block 一律跳,即使 combo 很低");
        }

        [Test]
        public void A_Break_Overrides_The_Clean_Note_Rule()
        {
            // 同一個 block 裡既有音符又有斷連 → 走「斷連」那條(不是「有音符就跳」)。
            Assert.IsFalse(DanceGate.NextState(dancing: true, hadBreak: true, hadNote: true, combo: 5, ignoreMiss: false));
        }

        [Test]
        public void Empty_Block_Holds_Current_State()
        {
            Assert.IsFalse(DanceGate.NextState(dancing: false, hadBreak: false, hadNote: false, combo: 999, ignoreMiss: false),
                "停住的舞者不會因為一段沒音符就自己站起來");
            Assert.IsTrue(DanceGate.NextState(dancing: true, hadBreak: false, hadNote: false, combo: 0, ignoreMiss: false));
        }

        // ---- 掉 miss 也照跳舞(config.ini opt_danceIgnoreMiss = 1)----

        [Test]
        public void IgnoreMiss_Keeps_Dancing_Through_Breaks()
        {
            Assert.IsTrue(DanceGate.NextState(dancing: true, hadBreak: true, hadNote: true, combo: 0, ignoreMiss: true),
                "整段都 miss、combo 0 也照跳");
        }

        [Test]
        public void IgnoreMiss_Resumes_A_Stopped_Dancer_On_The_Next_Judged_Block()
        {
            Assert.IsTrue(DanceGate.NextState(dancing: false, hadBreak: true, hadNote: true, combo: 0, ignoreMiss: true));
        }

        [Test]
        public void IgnoreMiss_Still_Holds_State_On_An_Empty_Block()
        {
            // 空 block 不豁免:編輯器/觀察模式那種刻意停住的舞者(沒有任何判定)不能被叫起來跳。
            Assert.IsFalse(DanceGate.NextState(dancing: false, hadBreak: false, hadNote: false, combo: 0, ignoreMiss: true));
        }

        // ---- 這一幀跳不跳(DanceEnabled / RecordGate 同一條)----

        [Test]
        public void HpOut_Stops_The_Dancer_In_FullSong_Mode()
        {
            // 完奏模式:歌不切斷(failed 不設)但血用完 → 停舞,回待機站到曲末。
            Assert.IsFalse(DanceGate.Enabled(dancing: true, failed: false, hpDead: true, ignoreMiss: false));
        }

        [Test]
        public void IgnoreMiss_Outranks_Hp_And_Keeps_Dancing_After_HpOut()
        {
            // opt_danceIgnoreMiss 優先權最大:血用完照樣跳。
            Assert.IsTrue(DanceGate.Enabled(dancing: true, failed: false, hpDead: true, ignoreMiss: true));
        }

        [Test]
        public void Failed_Always_Stops_The_Dancer()
        {
            // 一般模式 HP 歸零＝遊戲當場中斷進 GAME OVER,不是「繼續跳舞」的情境 → ignoreMiss 也不豁免。
            Assert.IsFalse(DanceGate.Enabled(dancing: true, failed: true, hpDead: true, ignoreMiss: true));
        }

        [Test]
        public void Stopped_By_The_Gate_Stays_Stopped()
        {
            Assert.IsFalse(DanceGate.Enabled(dancing: false, failed: false, hpDead: false, ignoreMiss: true));
        }

        [Test]
        public void Alive_And_Dancing_Is_Enabled()
        {
            Assert.IsTrue(DanceGate.Enabled(dancing: true, failed: false, hpDead: false, ignoreMiss: false));
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
            //
            // 本機那側刻意傳 ignoreMiss: false —— 遠端拿不到對方的 config.ini,所以兩邊比較的基準
            // 就是官方規則(見 DanceGate.NextFromSamples 的說明)。
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

                bool local = DanceGate.NextState(dancing,
                                                 hadBreak: cur.Breaks > prev.Breaks,
                                                 hadNote: cur.Total > prev.Total,
                                                 combo: combo,
                                                 ignoreMiss: false);
                bool remote = DanceGate.NextFromSamples(dancing, prev, cur, combo);
                Assert.AreEqual(local, remote,
                    "本機與遠端對同一組輸入的結論必須一致(prev.total=" + prev.Total + " cur.total=" + cur.Total
                    + " combo=" + combo + " dancing=" + dancing + ")");
            }
        }
    }
}
