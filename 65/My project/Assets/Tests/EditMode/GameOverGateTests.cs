using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    /// <summary>
    /// 血條見底之後「什麼時候才算出局」(<see cref="GameOverGate"/>)。
    ///
    /// 🔴 這裡釘住的是一條**連線才看得出來**的規則:自己血空不代表這一場結束 ——
    /// 房裡還有人在打就繼續打下去(等同完奏模式),等到全員陣亡(或歌播完)才收。
    /// 離線/單人那條路必須維持原行為(血空當場出局),否則單人測試與實機體驗會一起壞掉。
    /// </summary>
    public class GameOverGateTests
    {
        // ---- 某一位遠端玩家還算不算「在打」----

        [Test]
        public void A_Live_Opponent_Is_Still_Playing()
        {
            Assert.IsTrue(GameOverGate.RemoteStillPlaying(dead: false, left: false));
        }

        [Test]
        public void A_Dead_Or_Departed_Opponent_Is_Not_Waited_For()
        {
            Assert.IsFalse(GameOverGate.RemoteStillPlaying(dead: true, left: false), "他也死了 → 不用再等他");
            Assert.IsFalse(GameOverGate.RemoteStillPlaying(dead: false, left: true), "他中途走了 → 等下去只會卡住整場");
            Assert.IsFalse(GameOverGate.RemoteStillPlaying(dead: true, left: true));
        }

        [Test]
        public void Still_Playing_Matches_The_Remote_Dance_Gate()
        {
            // 站著不動的人 = 不必再等的人。兩邊各用一套判準的話,會出現「場上都站著了自己卻還在打」。
            foreach (bool dead in new[] { false, true })
                foreach (bool left in new[] { false, true })
                    Assert.AreEqual(DanceGate.RemoteEnabled(dancing: true, dead: dead, left: left),
                                    GameOverGate.RemoteStillPlaying(dead, left),
                                    "dead=" + dead + " left=" + left);
        }

        // ---- 這一幀出局了嗎 ----

        [Test]
        public void A_Living_Player_Never_Gets_Eliminated()
        {
            // 別人死光也不會讓還活著的人提早結算 —— 歌要照常打完。
            Assert.IsFalse(GameOverGate.EliminatedNow(hpDead: false, playFullSong: false, anyRemoteStillPlaying: false));
            Assert.IsFalse(GameOverGate.EliminatedNow(hpDead: false, playFullSong: false, anyRemoteStillPlaying: true));
            Assert.IsFalse(GameOverGate.EliminatedNow(hpDead: false, playFullSong: true, anyRemoteStillPlaying: false));
        }

        [Test]
        public void Solo_Death_Is_Eliminated_Immediately()
        {
            // 離線/單人:沒有別人在打 → 與加這條規則之前完全同一個行為(血空當場出局)。
            Assert.IsTrue(GameOverGate.EliminatedNow(hpDead: true, playFullSong: false, anyRemoteStillPlaying: false));
        }

        [Test]
        public void Death_Keeps_Playing_While_Someone_Else_Is_Still_Playing()
        {
            // 🔴 這條就是需求本身:房裡還有人在打 → 血空的人繼續打,不出局、不切結算。
            Assert.IsFalse(GameOverGate.EliminatedNow(hpDead: true, playFullSong: false, anyRemoteStillPlaying: true));
        }

        [Test]
        public void Elimination_Lands_On_The_Frame_The_Last_Opponent_Goes_Down()
        {
            // 每幀求值:成立的那一刻可能離血空已經過了一整段歌。
            Assert.IsFalse(GameOverGate.EliminatedNow(hpDead: true, playFullSong: false, anyRemoteStillPlaying: true));
            Assert.IsTrue(GameOverGate.EliminatedNow(hpDead: true, playFullSong: false, anyRemoteStillPlaying: false));
        }

        [Test]
        public void FullSong_Mode_Is_Never_Eliminated_Mid_Song()
        {
            // 完奏模式:整首打到曲末,由曲末那條出口收(那時是不是 GAME OVER 由 GameOverEnding 決定)。
            Assert.IsFalse(GameOverGate.EliminatedNow(hpDead: true, playFullSong: true, anyRemoteStillPlaying: false));
            Assert.IsFalse(GameOverGate.EliminatedNow(hpDead: true, playFullSong: true, anyRemoteStillPlaying: true));
        }

        // ---- 收尾要不要演 GAME OVER 大字 ----

        [Test]
        public void A_Living_Player_Never_Sees_Game_Over()
        {
            Assert.IsFalse(GameOverGate.GameOverEnding(hpDead: false, anyRemoteStillPlaying: false));
            Assert.IsFalse(GameOverGate.GameOverEnding(hpDead: false, anyRemoteStillPlaying: true));
        }

        [Test]
        public void Solo_Death_Still_Shows_Game_Over()
        {
            // 離線/單人/全員陣亡:被淘汰 = 這一場對我結束了 → 照舊死亡演出(行為不變)。
            Assert.IsTrue(GameOverGate.GameOverEnding(hpDead: true, anyRemoteStillPlaying: false));
        }

        [Test]
        public void Death_With_Someone_Else_Alive_Ends_As_A_Normal_Loss()
        {
            // 🔴 需求本身:多人時自己死了但別人還在打,歌被撐到曲末 —— 那一場是正常打完的,
            // 收尾走一般輸掉的流程(cat4 + SE_0015 + 輸的旗),不出 GAME OVER 大字。
            Assert.IsFalse(GameOverGate.GameOverEnding(hpDead: true, anyRemoteStillPlaying: true));
        }

        [Test]
        public void Every_Elimination_Ends_As_Game_Over()
        {
            // 兩條規則必須對得起來:被淘汰(EliminatedNow)那一幀切結算,一定會演 GAME OVER。
            // 少了這條,「出局了卻走輸的定格」會變成沒有人查得出原因的畫面。
            foreach (bool playFullSong in new[] { false, true })
                foreach (bool anyRemote in new[] { false, true })
                    if (GameOverGate.EliminatedNow(hpDead: true, playFullSong: playFullSong, anyRemoteStillPlaying: anyRemote))
                        Assert.IsTrue(GameOverGate.GameOverEnding(hpDead: true, anyRemoteStillPlaying: anyRemote),
                                      "playFullSong=" + playFullSong + " anyRemote=" + anyRemote);
        }
    }
}
