using NUnit.Framework;
using Sdo.Settings;
using Sdo.UI.Core;

namespace Sdo.Tests
{
    /// <summary>
    /// 累計統計的算術與「哪些場次算數」的政策。
    ///
    /// 這條測試守的是個人資料頁上那幾個百分比:分母錯了、或是自由模式/單機場偷偷被記進勝率,
    /// 畫面上看到的只是一個「看起來合理但其實是假的」數字 —— 那種錯誤沒有人會發現。
    /// </summary>
    public class PlayStatsTests
    {
        [Test]
        public void Accuracy_Is_Perfect_Plus_Cool_Over_Judged()
        {
            var s = new PlayStats();
            s.AddPlay(70, 10, 15, 5);   // judged = 100
            Assert.AreEqual(100, s.Judged);
            Assert.AreEqual(80.0, s.Accuracy, 0.0001);
            Assert.AreEqual(70.0, s.PerfectRate, 0.0001);
            Assert.AreEqual(10.0, s.CoolRate, 0.0001);
            Assert.AreEqual(15.0, s.BadRate, 0.0001);
            Assert.AreEqual(5.0, s.MissRate, 0.0001);
        }

        [Test]
        public void Rates_Are_Zero_Before_Any_Play()
        {
            // 分母 0 → 回 0,不是 NaN 也不是 100。個人資料頁會直接把它格式化出來,NaN 會變成「NaN%」。
            var s = new PlayStats();
            Assert.AreEqual(0, s.Judged);
            Assert.AreEqual(0.0, s.Accuracy, 0.0001);
            Assert.AreEqual(0.0, s.MissRate, 0.0001);
            Assert.AreEqual(0.0, s.WinRate, 0.0001);
        }

        [Test]
        public void AddPlay_Accumulates_Across_Songs()
        {
            var s = new PlayStats();
            s.AddPlay(10, 0, 0, 0);
            s.AddPlay(20, 5, 5, 0);
            Assert.AreEqual(30, s.perfect);
            Assert.AreEqual(5, s.cool);
            Assert.AreEqual(5, s.bad);
            Assert.AreEqual(2, s.plays);
        }

        [Test]
        public void AddPlay_Ignores_Negative_Counts()
        {
            // 壞掉的來源(或還沒初始化的 ScoreProcessor)不該把已經累積的成績拉低。
            var s = new PlayStats();
            s.AddPlay(10, 10, 10, 10);
            s.AddPlay(-5, -5, -5, -5);
            Assert.AreEqual(40, s.Judged);
        }

        [Test]
        public void WinRate_Counts_Only_Recorded_Results()
        {
            var s = new PlayStats();
            s.AddResult(true);
            s.AddResult(true);
            s.AddResult(false);
            Assert.AreEqual(2, s.wins);
            Assert.AreEqual(1, s.losses);
            Assert.AreEqual(200.0 / 3.0, s.WinRate, 0.0001);
        }

        // ---- 政策:哪些場次算數 ----

        [Test]
        public void Judgements_Are_Recorded_Offline_Too()
        {
            // 判定數是玩家真的按出來的 —— 單機打的也算。使用者指定的政策。
            Assert.IsTrue(PlayStatsRecorder.ShouldRecordPlay(finished: true, spectator: false));
        }

        [Test]
        public void Nothing_Is_Recorded_When_Spectating()
        {
            Assert.IsFalse(PlayStatsRecorder.ShouldRecordPlay(finished: true, spectator: true));
            Assert.IsFalse(PlayStatsRecorder.ShouldRecordWinLoss(true, spectator: true, online: true, gameMode: 1, participants: 2));
        }

        [Test]
        public void Nothing_Is_Recorded_When_Quitting_Midway()
        {
            // 中途 ESC:判定數只有半首歌,而且「快輸了就退出」不該變成保住勝率的操作。
            Assert.IsFalse(PlayStatsRecorder.ShouldRecordPlay(finished: false, spectator: false));
            Assert.IsFalse(PlayStatsRecorder.ShouldRecordWinLoss(false, false, online: true, gameMode: 1, participants: 2));
        }

        [Test]
        public void WinLoss_Only_In_Normal_And_Showtime()
        {
            // 0=自由 1=普通 2=ShowTime
            Assert.IsFalse(PlayStats.RecordsWinLoss(0));
            Assert.IsTrue(PlayStats.RecordsWinLoss(1));
            Assert.IsTrue(PlayStats.RecordsWinLoss(2));

            Assert.IsFalse(PlayStatsRecorder.ShouldRecordWinLoss(true, false, online: true, gameMode: 0, participants: 2));
            Assert.IsTrue(PlayStatsRecorder.ShouldRecordWinLoss(true, false, online: true, gameMode: 1, participants: 2));
            Assert.IsTrue(PlayStatsRecorder.ShouldRecordWinLoss(true, false, online: true, gameMode: 2, participants: 2));
        }

        [Test]
        public void WinLoss_Is_Not_Recorded_Offline()
        {
            // 單機的對手是假資料 bot、本機幾乎必勝 —— 記了勝率就沒有意義。
            Assert.IsFalse(PlayStatsRecorder.ShouldRecordWinLoss(true, false, online: false, gameMode: 1, participants: 2));
            Assert.IsFalse(PlayStatsRecorder.ShouldRecordWinLoss(true, false, online: false, gameMode: 2, participants: 2));
            // 但判定數照記。
            Assert.IsTrue(PlayStatsRecorder.ShouldRecordPlay(finished: true, spectator: false));
        }

        [Test]
        public void WinLoss_Is_Not_Recorded_When_Playing_Alone()
        {
            // 🔴 使用者回報:一個人在房裡開普通模式打完,結算就是第一名 —— 每首歌白送一場勝場,
            //    勝率從此永遠 100%。沒有對手就沒有輸贏,連線與否都一樣。
            Assert.IsFalse(PlayStatsRecorder.ShouldRecordWinLoss(true, false, online: true, gameMode: 1, participants: 1));
            Assert.IsFalse(PlayStatsRecorder.ShouldRecordWinLoss(true, false, online: true, gameMode: 2, participants: 1));
            // 座位數壞掉(0/負數)也一樣不記 —— 不確定有幾個人時,寧可不記。
            Assert.IsFalse(PlayStatsRecorder.ShouldRecordWinLoss(true, false, online: true, gameMode: 1, participants: 0));
            // 兩個人才是一場真的對局。
            Assert.IsTrue(PlayStatsRecorder.ShouldRecordWinLoss(true, false, online: true, gameMode: 1, participants: 2));
            // 判定數不受影響:自己一個人打完的 P/C/B/M 照樣是玩家真的按出來的。
            Assert.IsTrue(PlayStatsRecorder.ShouldRecordPlay(finished: true, spectator: false));
        }

        [Test]
        public void A_Run_Is_Recorded_Only_Once()
        {
            // 🔴 這條守的是一個真的發生過的 bug:回房那條路是被 Update 每幀輪詢 ResultConfirmed 驅動的,
            //    而遊戲物件要等轉場漸黑結束才拆 —— 中間十幾幀每幀都會落地一次戰績。
            //    症狀:打一場勝場從 14 變 27(一次 +13),plays/判定數同倍膨脹,勝率因此永遠是 100%。
            Assert.IsTrue(PlayStatsRecorder.ShouldRecordRun(alreadyRecorded: false));
            Assert.IsFalse(PlayStatsRecorder.ShouldRecordRun(alreadyRecorded: true));
        }

        [Test]
        public void Repeated_Recording_Multiplies_The_Numbers()
        {
            // 為什麼上面那道門非有不可:累加本身沒有(也不該有)去重 —— 同一場記 13 次就真的是 13 場。
            var s = new PlayStats();
            for (int frame = 0; frame < 13; frame++) { s.AddPlay(100, 0, 0, 0); s.AddResult(true); }
            Assert.AreEqual(13, s.plays);
            Assert.AreEqual(13, s.wins);
            Assert.AreEqual(1300, s.perfect);
        }

        [Test]
        public void Sanitize_Clamps_Corrupt_Values()
        {
            var s = new PlayStats { perfect = -1, cool = -1, bad = -1, miss = -1, plays = -1, wins = -1, losses = -1 };
            s.Sanitize();
            Assert.AreEqual(0, s.Judged);
            Assert.AreEqual(0, s.plays);
            Assert.AreEqual(0, s.wins);
            Assert.AreEqual(0, s.losses);
        }
    }
}
