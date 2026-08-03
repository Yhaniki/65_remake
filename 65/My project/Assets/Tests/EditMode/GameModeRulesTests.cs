using NUnit.Framework;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    /// <summary>
    /// 「這一場是什麼模式」的規則。守的是兩件會靜默出錯的事:
    ///   ① 線上的模式來源是**房間**(server 快照),不是本機 session ——
    ///      非房主的 session 還留著自己上次選的模式,拿它開場會讓自由模式的房間照樣出名次、照樣記勝負;
    ///   ② 自由模式的判定要跟 <c>ScreenGameplay.freeMode</c> / <c>PlayStats.RecordsWinLoss</c> 對得上。
    /// </summary>
    public class GameModeRulesTests
    {
        // ---- ① 來源 ----

        [Test]
        public void Online_Room_Setting_Wins_Over_The_Local_Session()
        {
            // 房間是自由模式,本機 session 還留著上次的「普通」→ 這一場是自由模式。
            Assert.AreEqual(GameModeRules.Free, GameModeRules.Effective(GameModeRules.Free, sessionGameMode: GameModeRules.Normal));
            // 反過來也一樣:房間是普通,本機留著自由 → 普通。
            Assert.AreEqual(GameModeRules.Normal, GameModeRules.Effective(GameModeRules.Normal, sessionGameMode: GameModeRules.Free));
        }

        [Test]
        public void Offline_Falls_Back_To_The_Session()
        {
            // 離線沒有 server,session 就是房間設定。
            Assert.AreEqual(GameModeRules.Free, GameModeRules.Effective(null, GameModeRules.Free));
            Assert.AreEqual(GameModeRules.Showtime, GameModeRules.Effective(null, GameModeRules.Showtime));
        }

        [Test]
        public void Out_Of_Range_Values_Are_Clamped()
        {
            // 協定與 config.ini 都可能塞進範圍外的值;夾不住的話 IsFree 會在負數上回 false(當成普通模式跑)。
            Assert.AreEqual(GameModeRules.Free, GameModeRules.Effective(-1, GameModeRules.Normal));
            Assert.AreEqual(GameModeRules.Showtime, GameModeRules.Effective(99, GameModeRules.Normal));
            Assert.AreEqual(GameModeRules.Free, GameModeRules.Effective(null, -5));
            Assert.IsTrue(GameModeRules.IsFree(-1));
        }

        // ---- ② 模式的意思 ----

        [Test]
        public void Free_Is_Mode_Zero_Only()
        {
            Assert.IsTrue(GameModeRules.IsFree(0));
            Assert.IsFalse(GameModeRules.IsFree(1));
            Assert.IsFalse(GameModeRules.IsFree(2));
        }

        [Test]
        public void Showtime_Is_Mode_Two_Only()
        {
            Assert.IsFalse(GameModeRules.IsShowtime(0));
            Assert.IsFalse(GameModeRules.IsShowtime(1));
            Assert.IsTrue(GameModeRules.IsShowtime(2));
        }

        [Test]
        public void Free_Mode_Is_Exactly_The_One_That_Records_No_Win_Loss()
        {
            // 兩條規則各自寫在不同檔案(這裡與 PlayStats.RecordsWinLoss),它們對「自由模式」的定義必須一致。
            for (int m = 0; m <= 2; m++)
                Assert.AreNotEqual(GameModeRules.IsFree(m), Sdo.Settings.PlayStats.RecordsWinLoss(m),
                                   "模式 " + m + ":自由模式不記勝負,其餘都記");
        }
    }
}
