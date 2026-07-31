using NUnit.Framework;
using Sdo.Net;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    /// <summary>
    /// 房間 3D 裡別人頭上那塊名牌寫什麼字。
    ///
    /// 為什麼值得一條測試:名牌是「那個人現在在不在房間裡」在 3D 場景中唯一的線索
    /// (頭貼那條 PLAYING 徽章只有前六格看得到,旁觀者與走動中的人只有名牌)。
    /// 兩邊必須用同一條 IsInMatch —— 這裡把「共用」這件事釘住。
    /// </summary>
    public class RoomHeadNameTests
    {
        [Test]
        public void Idle_Shows_Name_And_Level()
        {
            Assert.AreEqual("阿明  LV:12", RoomHeadName.For("阿明", 12, PlayState.Idle));
        }

        [Test]
        public void Level_Zero_Is_Omitted()
        {
            // 剛進房、等級還沒同步過來的人不該頂著「LV:0」。
            Assert.AreEqual("阿明", RoomHeadName.For("阿明", 0, PlayState.Idle));
            Assert.AreEqual("阿明", RoomHeadName.For("阿明", -1, PlayState.Idle));
        }

        [Test]
        public void Null_Name_Is_Not_A_Crash()
        {
            Assert.AreEqual("", RoomHeadName.For(null, 0, PlayState.Idle));
            Assert.AreEqual("  LV:3", RoomHeadName.For(null, 3, PlayState.Idle));
        }

        [Test]
        public void In_Match_Shows_Playing_Instead_Of_The_Name()
        {
            // 從被納入載入一直到停在結算畫面,那個人都還沒回房間 → 名牌一路寫 Playing。
            foreach (var s in new[]
                     {
                         PlayState.WaitingForLoad, PlayState.Loaded, PlayState.ReadyForGameplay,
                         PlayState.Playing, PlayState.Finished, PlayState.Results,
                     })
                Assert.AreEqual(RoomHeadName.PlayingText, RoomHeadName.For("阿明", 12, s), s + " 應該寫 Playing");
        }

        [Test]
        public void Idle_Ready_And_Spectating_Keep_Their_Name()
        {
            // 按了準備、或坐在旁邊看的人**還在房間裡**,名牌就該是名字 —— 不然找不到人是誰。
            foreach (var s in new[] { PlayState.Idle, PlayState.Ready, PlayState.Spectating })
                Assert.AreEqual("阿明  LV:12", RoomHeadName.For("阿明", 12, s), s + " 應該寫名字");
        }

        [Test]
        public void Plate_And_Badge_Agree_On_Who_Is_In_The_Match()
        {
            // 🔴 同一份狀態不能一邊寫 Playing、一邊畫 HOST。共用 RoomBadgeChoice.IsInMatch 就是為了這個。
            foreach (PlayState s in System.Enum.GetValues(typeof(PlayState)))
            {
                bool plateSaysPlaying = RoomHeadName.For("阿明", 12, s) == RoomHeadName.PlayingText;
                Assert.AreEqual(RoomBadgeChoice.IsInMatch(s), plateSaysPlaying, s + " 名牌與徽章判定不一致");
            }
        }
    }
}
