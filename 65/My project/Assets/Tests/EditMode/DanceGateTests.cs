using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    /// <summary>
    /// 舞者跳舞/停舞的 8 拍結算決策（<see cref="DanceGate.NextState"/>）。官方規則＋config.ini 的
    /// 「掉 miss 也照跳舞」(opt_danceIgnoreMiss) 豁免。
    /// </summary>
    public class DanceGateTests
    {
        // ---- 官方規則（ignoreMiss = false）----

        [Test]
        public void Break_With_Strong_Combo_Keeps_Dancing()
        {
            Assert.IsTrue(DanceGate.NextState(dancing: true, hadBreak: true, hadNote: true, combo: 31, ignoreMiss: false));
        }

        [Test]
        public void Break_With_Weak_Combo_Stops_Dancing()
        {
            Assert.IsFalse(DanceGate.NextState(dancing: true, hadBreak: true, hadNote: true, combo: 30, ignoreMiss: false),
                "門檻是 combo > 30，剛好 30 要停");
            Assert.IsFalse(DanceGate.NextState(dancing: true, hadBreak: true, hadNote: true, combo: 0, ignoreMiss: false));
        }

        [Test]
        public void Clean_Block_With_Notes_Resumes_Dancing()
        {
            Assert.IsTrue(DanceGate.NextState(dancing: false, hadBreak: false, hadNote: true, combo: 1, ignoreMiss: false),
                "乾淨的 block 一律跳，即使 combo 很低");
        }

        [Test]
        public void Empty_Block_Holds_Current_State()
        {
            Assert.IsFalse(DanceGate.NextState(dancing: false, hadBreak: false, hadNote: false, combo: 999, ignoreMiss: false),
                "停住的舞者不會因為一段沒音符就自己站起來");
            Assert.IsTrue(DanceGate.NextState(dancing: true, hadBreak: false, hadNote: false, combo: 0, ignoreMiss: false));
        }

        // ---- 掉 miss 也照跳舞（config.ini opt_danceIgnoreMiss = 1）----

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
            // 空 block 不豁免：編輯器/觀察模式那種刻意停住的舞者（沒有任何判定）不能被叫起來跳。
            Assert.IsFalse(DanceGate.NextState(dancing: false, hadBreak: false, hadNote: false, combo: 0, ignoreMiss: true));
        }

        // ---- 這一幀跳不跳（DanceEnabled / RecordGate 同一條）----

        [Test]
        public void HpOut_Stops_The_Dancer_In_FullSong_Mode()
        {
            // 完奏模式：歌不切斷（failed 不設）但血用完 → 停舞，回待機站到曲末。
            Assert.IsFalse(DanceGate.Enabled(dancing: true, failed: false, hpDead: true, ignoreMiss: false));
        }

        [Test]
        public void IgnoreMiss_Outranks_Hp_And_Keeps_Dancing_After_HpOut()
        {
            // opt_danceIgnoreMiss 優先權最大：血用完照樣跳。
            Assert.IsTrue(DanceGate.Enabled(dancing: true, failed: false, hpDead: true, ignoreMiss: true));
        }

        [Test]
        public void Failed_Always_Stops_The_Dancer()
        {
            // 一般模式 HP 歸零＝遊戲當場中斷進 GAME OVER，不是「繼續跳舞」的情境 → ignoreMiss 也不豁免。
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
    }
}
