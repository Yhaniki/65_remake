using NUnit.Framework;
using Sdo.Net;
using Sdo.UI.Core;

namespace Sdo.Tests
{
    /// <summary>
    /// 「只有普通模式才能組隊」這條規則本身,以及讓它在線上成立所需要的房間設定同步。
    ///
    /// 官方的依據在反編譯的 <c>022_state_004792d0</c>:模式是自由(或房間已經進遊戲)時,
    /// 那四個 team CheckBox 一律 <c>SetEnable(0)</c>。remake 把同一條規則寫成純函式,
    /// client 與 server 共用(<see cref="TeamLayoutRules.TeamsAllowedIn"/>)。
    /// </summary>
    public class TeamModeGateTests
    {
        [Test]
        public void Only_Normal_Mode_Allows_Teams()
        {
            Assert.IsFalse(TeamLayoutRules.TeamsAllowedIn(0), "自由模式不能組隊");
            Assert.IsTrue(TeamLayoutRules.TeamsAllowedIn(1), "普通模式可以組隊");
            Assert.IsFalse(TeamLayoutRules.TeamsAllowedIn(2), "ShowTime 模式不能組隊");
        }

        [Test]
        public void Team_Game_Mode_Constant_Is_Normal()
        {
            // GameSession.GameMode 的定義:0=自由 1=普通 2=ShowTime。
            Assert.AreEqual(1, TeamLayoutRules.TeamGameMode);
        }

        [Test]
        public void Broken_Mode_Values_Do_Not_Allow_Teams()
        {
            Assert.IsFalse(TeamLayoutRules.TeamsAllowedIn(-1));
            Assert.IsFalse(TeamLayoutRules.TeamsAllowedIn(99));
        }

        // ---- 房間設定同步(沒有它,線上永遠沒有一間房是普通模式)----

        private static NetRoomSettings S(int mode, int form, int looker, int scene, bool rand)
            => new NetRoomSettings { GameMode = mode, Formation = form, LookerCount = looker, SceneId = scene, SceneRandom = rand };

        [Test]
        public void SameAs_Is_True_Only_When_Every_Field_Matches()
        {
            var a = S(1, 2, 7, 12, false);
            Assert.IsTrue(a.SameAs(S(1, 2, 7, 12, false)));
            Assert.IsFalse(a.SameAs(S(0, 2, 7, 12, false)), "模式不同");
            Assert.IsFalse(a.SameAs(S(1, 3, 7, 12, false)), "隊形不同");
            Assert.IsFalse(a.SameAs(S(1, 2, 8, 12, false)), "旁觀人數不同");
            Assert.IsFalse(a.SameAs(S(1, 2, 7, 13, false)), "場景不同");
            Assert.IsFalse(a.SameAs(S(1, 2, 7, 12, true)), "隨機場景旗標不同");
            Assert.IsFalse(a.SameAs(null));
        }

        [Test]
        public void Publisher_Clamps_The_Same_Way_The_Wire_Does()
        {
            // 🔴 這是「不會變成無窮迴圈」的關鍵:送出去的值如果被 server 夾成別的數字,
            //    echo 回來就永遠不等於我們想要的 → 每一份房間快照都會再送一次。
            var s = new GameSession { GameMode = 9, Formation = -3, LookerCount = 999, StageId = 99999, StageRandom = false };
            var want = NetRoomSettingsPublisher.FromSession(s);

            var afterWire = NetRoomSettings.Decode(Parse(want.Encode().Json()));
            Assert.IsTrue(afterWire.SameAs(want), "編碼→解碼(含 server 端夾值)之後必須與送出去的完全一樣");
        }

        [Test]
        public void Publisher_Carries_The_Panel_Values_Through()
        {
            var s = new GameSession { GameMode = 1, Formation = 2, LookerCount = 4, StageId = 12, StageRandom = false };
            var want = NetRoomSettingsPublisher.FromSession(s);

            Assert.AreEqual(1, want.GameMode);
            Assert.AreEqual(2, want.Formation);
            Assert.AreEqual(4, want.LookerCount);
            Assert.AreEqual(12, want.SceneId);
            Assert.IsFalse(want.SceneRandom);
        }

        [Test]
        public void Random_Scene_Survives_The_Round_Trip()
        {
            var s = new GameSession { GameMode = 0, StageRandom = true, StageId = 9 };
            var want = NetRoomSettingsPublisher.FromSession(s);
            Assert.IsTrue(want.SceneRandom);
            Assert.IsTrue(NetRoomSettings.Decode(Parse(want.Encode().Json())).SameAs(want));
        }

        /// <summary>把 <see cref="JObj"/> 收成的 JSON 字串再解回節點 —— <see cref="NetRoomSettings.Decode"/>
        /// 吃的是**解析後**的節點,不是 JObj 這個 builder(直接傳 JObj 進去每個欄位都會讀到預設值)。</summary>
        private static object Parse(string json)
        {
            object node;
            Assert.IsTrue(NetJson.TryParse(json, out node), "測試自己組的 JSON 應該合法:" + json);
            return node;
        }
    }
}
