using NUnit.Framework;
using UnityEngine;
using Sdo.Game;
using Sdo.Net;

namespace Sdo.Tests
{
    /// <summary>
    /// 官方隊伍色表。
    ///
    /// 為什麼要測一張常數表:它同時餵給四個地方(房間名牌、頭貼徽章、遊戲中頭上名字、腳下光暈),
    /// 而且 remake 的 <see cref="TeamTag"/>(0=A 1=B 2=C 3=自由)與官方的隊伍 byte(0=無 1=A 2=B 3=C)
    /// **差一位**。接錯的症狀是「A 隊的人頂著 B 隊的顏色」—— 不會報錯,只會讓場上分不出敵我。
    /// </summary>
    public class TeamColorsTests
    {
        // EXE 的 DAT_00586274(檔案位移 0x186274)那張四筆表,去掉「無隊伍」那一格。
        private static readonly Color32 OfficialA = new Color32(0xFF, 0xA5, 0x00, 0xFF);
        private static readonly Color32 OfficialB = new Color32(0x4F, 0xE4, 0x00, 0xFF);
        private static readonly Color32 OfficialC = new Color32(0x53, 0xC8, 0xFF, 0xFF);

        private static void AssertSame(Color32 want, Color got, string what)
        {
            var g = (Color32)got;
            Assert.AreEqual(want.r, g.r, what + " R");
            Assert.AreEqual(want.g, g.g, what + " G");
            Assert.AreEqual(want.b, g.b, what + " B");
            Assert.AreEqual(255, g.a, what + " A");
        }

        [Test]
        public void Colors_Match_The_Official_Exe_Table()
        {
            AssertSame(OfficialA, TeamColors.A, "A 隊(橘 0xFFA500)");
            AssertSame(OfficialB, TeamColors.B, "B 隊(綠 0x4FE400)");
            AssertSame(OfficialC, TeamColors.C, "C 隊(青藍 0x53C8FF)");
        }

        [Test]
        public void TryFor_Maps_TeamTag_To_The_Right_Colour()
        {
            Color c;
            Assert.IsTrue(TeamColors.TryFor((int)TeamTag.A, out c)); AssertSame(OfficialA, c, "TeamTag.A");
            Assert.IsTrue(TeamColors.TryFor((int)TeamTag.B, out c)); AssertSame(OfficialB, c, "TeamTag.B");
            Assert.IsTrue(TeamColors.TryFor((int)TeamTag.C, out c)); AssertSame(OfficialC, c, "TeamTag.C");
        }

        [Test]
        public void Free_Has_No_Team_Colour()
        {
            // 沒選隊 → 呼叫端要維持中性外觀(名牌不畫、名字乳白、腳下不加光暈),
            // 而不是拿某一隊的顏色頂替。
            Color c;
            Assert.IsFalse(TeamColors.TryFor((int)TeamTag.Free, out c));
            Assert.IsFalse(TeamColors.IsTeam((int)TeamTag.Free));
        }

        [Test]
        public void Broken_Values_Behave_Like_No_Team()
        {
            Color c;
            Assert.IsFalse(TeamColors.TryFor(-1, out c));
            Assert.IsFalse(TeamColors.TryFor(99, out c));
            Assert.IsFalse(TeamColors.IsTeam(-1));
            Assert.IsFalse(TeamColors.IsTeam(3));
        }

        [Test]
        public void Every_Team_Has_A_Distinct_Colour()
        {
            Assert.AreNotEqual(TeamColors.A, TeamColors.B);
            Assert.AreNotEqual(TeamColors.B, TeamColors.C);
            Assert.AreNotEqual(TeamColors.A, TeamColors.C);
        }

        [Test]
        public void Free_Constant_Matches_The_TeamTag_Enum()
        {
            // TeamColors 被 Sdo.Game 用(它編不到 Sdo.UI),所以自己抄了一份「自由」的值 —— 不能漂。
            Assert.AreEqual((int)TeamTag.Free, TeamColors.Free);
            Assert.AreEqual(3, TeamColors.TeamCount + 0 + (TeamColors.TeamCount == 3 ? 0 : 1));
            Assert.AreEqual(3, TeamColors.TeamCount);
        }

        [Test]
        public void Badge_Frame_Index_Equals_The_Official_Team_Byte()
        {
            // 官方三張四幀圖(Team.an 名牌 / Room66.an READY / master.an HOST)的幀序就是官方隊伍 byte:
            // 0=無隊伍 1=A 2=B 3=C。RoomBadgeFrames 做的正是 TeamTag → 官方 byte 這個換算。
            Assert.AreEqual(0, Sdo.UI.Services.RoomBadgeFrames.ForTeam((int)TeamTag.Free));
            Assert.AreEqual(1, Sdo.UI.Services.RoomBadgeFrames.ForTeam((int)TeamTag.A));
            Assert.AreEqual(2, Sdo.UI.Services.RoomBadgeFrames.ForTeam((int)TeamTag.B));
            Assert.AreEqual(3, Sdo.UI.Services.RoomBadgeFrames.ForTeam((int)TeamTag.C));
        }
    }
}
