using NUnit.Framework;
using Sdo.Net;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    /// <summary>
    /// 頭貼上 READY / HOST 徽章的顏色 = 那個人自己的隊伍。
    ///
    /// 為什麼值得一條測試:這個對映是「隊伍列舉」與「官方 .an 的幀序」兩張表在對帳,
    /// 而兩邊都不會因為對錯而報錯 —— 錯了只是畫面上 A 隊的人頂著 B 隊的顏色,
    /// 而那正是「選不同隊要看得出來」這個需求唯一的產出。
    /// </summary>
    public class RoomBadgeFramesTests
    {
        [Test]
        public void Free_Team_Uses_The_White_Frame()
        {
            // a06 / b06(第 0 幀)是白色 —— 沒選隊的人就是中性白。
            Assert.AreEqual(0, RoomBadgeFrames.ForTeam((int)TeamTag.Free));
        }

        [Test]
        public void Abc_Map_To_Orange_Green_Blue_In_Order()
        {
            Assert.AreEqual(1, RoomBadgeFrames.ForTeam((int)TeamTag.A));   // a07 / b07 橘
            Assert.AreEqual(2, RoomBadgeFrames.ForTeam((int)TeamTag.B));   // a08 / b08 綠
            Assert.AreEqual(3, RoomBadgeFrames.ForTeam((int)TeamTag.C));   // a09 / b09 藍
        }

        [Test]
        public void Every_Team_Maps_Inside_The_Four_Frames()
        {
            for (int team = 0; team <= (int)TeamTag.Free; team++)
            {
                int f = RoomBadgeFrames.ForTeam(team);
                Assert.GreaterOrEqual(f, 0);
                Assert.Less(f, RoomBadgeFrames.FrameCount);
            }
        }

        [Test]
        public void Each_Team_Gets_Its_Own_Frame()
        {
            // 四隊四色,不能有兩隊撞同一幀(撞了就看不出誰是哪一隊)。
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int team = 0; team <= (int)TeamTag.Free; team++)
                Assert.IsTrue(seen.Add(RoomBadgeFrames.ForTeam(team)), "隊伍 " + team + " 的徽章幀與別隊重複");
        }

        [Test]
        public void Out_Of_Range_Falls_Back_To_White()
        {
            // 值壞掉(舊版 client / 沒對到的欄位)寧可畫中性白,也不要假裝他選了某一隊。
            Assert.AreEqual(0, RoomBadgeFrames.ForTeam(-1));
            Assert.AreEqual(0, RoomBadgeFrames.ForTeam(99));
        }
    }
}
