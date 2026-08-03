using NUnit.Framework;
using Sdo.Net;

namespace Sdo.Tests
{
    /// <summary>
    /// 「這位遠端玩家還在這一場裡嗎」(<see cref="MatchPresence"/>)。
    ///
    /// 這條規則存在的理由:中途 Esc 回房間在分數流裡看起來什麼都沒發生(他只是不再送 frame,
    /// 而那與「這一段沒有音符」長得一模一樣)—— 場上那尊會一直跳下去。座位的 playState 是唯一的來源。
    /// </summary>
    public class MatchPresenceTests
    {
        [Test]
        public void Playing_And_Watching_Results_Are_Both_In_The_Match()
        {
            Assert.IsTrue(MatchPresence.InMatch(PlayState.Playing));
            Assert.IsTrue(MatchPresence.InMatch(PlayState.Finished), "打完歌在等結算 —— 人還沒回房間");
            Assert.IsTrue(MatchPresence.InMatch(PlayState.Results));
            Assert.IsFalse(MatchPresence.InMatch(PlayState.Idle));
            Assert.IsFalse(MatchPresence.InMatch(PlayState.Ready));
        }

        [Test]
        public void Back_To_Idle_After_Playing_Counts_As_Leaving()
        {
            // 中離的 client 會連著送 playFinished + setPlayState{idle}(FrontendApp.AbortGameplay)。
            Assert.IsTrue(MatchPresence.HasLeft(PlayState.Idle, sawPlaying: true));
        }

        [Test]
        public void A_Missing_Seat_Counts_As_Leaving()
        {
            // 座位不見 = 離開房間 / 斷線。
            Assert.IsTrue(MatchPresence.HasLeft(null, sawPlaying: true));
        }

        [Test]
        public void Before_The_Song_Starts_Nobody_Has_Left()
        {
            // 🔴 這條是 latch 的理由:開跳前座位還停在 loaded / readyForGameplay,
            // 少了 sawPlaying 全場遠端會一開跳就站著不動。
            Assert.IsFalse(MatchPresence.HasLeft(PlayState.Loaded, sawPlaying: false));
            Assert.IsFalse(MatchPresence.HasLeft(PlayState.ReadyForGameplay, sawPlaying: false));
            Assert.IsFalse(MatchPresence.HasLeft(PlayState.Idle, sawPlaying: false));
        }

        [Test]
        public void Someone_Still_Playing_Has_Not_Left()
        {
            Assert.IsFalse(MatchPresence.HasLeft(PlayState.Playing, sawPlaying: true));
        }

        [Test]
        public void Finishing_The_Song_Normally_Is_Not_Leaving()
        {
            // 正常打完的人在看結算,場上不該把他當成中離(曲末大家差不多同時結束,
            // 提早幾百毫秒就讓他站住只會看起來像卡住)。
            Assert.IsFalse(MatchPresence.HasLeft(PlayState.Finished, sawPlaying: true));
            Assert.IsFalse(MatchPresence.HasLeft(PlayState.Results, sawPlaying: true));
        }
    }
}
