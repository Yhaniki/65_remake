using NUnit.Framework;
using Sdo.UI.Core;

namespace Sdo.Tests
{
    /// <summary>
    /// 開場落在男/女選擇畫面（GenderSel）。從那裡按「登入」有兩條出路：
    /// 連上伺服器 → Lobby（大廳，官方流程）；連不上／沒設伺服器 → 直接進自己的 Room（原本的單機路徑）。
    /// 兩條邊都要通，少一條就會變成「登入成功卻卡在選角色畫面」或「單機按登入沒反應」。
    /// </summary>
    public class FlowManagerTests
    {
        [Test]
        public void Default_Is_GenderSel()
            => Assert.AreEqual(ScreenId.GenderSel, new FlowManager().Current);

        [Test]
        public void Allowed_Path_Through_All_Screens()
        {
            var f = new FlowManager();
            Assert.IsTrue(f.GoTo(ScreenId.Room));
            Assert.IsTrue(f.GoTo(ScreenId.SongSelect));
            Assert.IsTrue(f.GoTo(ScreenId.Room));
            Assert.IsTrue(f.GoTo(ScreenId.Gameplay));
            Assert.IsTrue(f.GoTo(ScreenId.Room));
            Assert.IsTrue(f.GoTo(ScreenId.Lobby));
        }

        [Test]
        public void Login_Can_Go_Straight_To_Lobby()
        {
            // 登入成功 → 大廳。這是官方流程,也是「按登入之後看得到房間列表」的前提。
            var f = new FlowManager();
            Assert.IsTrue(f.CanGoTo(ScreenId.Lobby));
            Assert.IsTrue(f.GoTo(ScreenId.Lobby));
            Assert.AreEqual(ScreenId.Lobby, f.Current);
        }

        [Test]
        public void Login_Failure_Falls_Back_To_Room()
        {
            // 連不上 / 沒設伺服器 → 照原本的單機路徑直接進自己的房間。
            var f = new FlowManager();
            Assert.IsTrue(f.GoTo(ScreenId.Room));
        }

        [Test]
        public void Lobby_Can_Create_Room_And_Log_Out()
        {
            var f = new FlowManager();
            f.GoTo(ScreenId.Lobby);
            Assert.IsTrue(f.CanGoTo(ScreenId.Room), "大廳按『創建舞台』要進得了房間");
            Assert.IsTrue(f.CanGoTo(ScreenId.GenderSel), "大廳登出要回得去選角色畫面");
        }

        [Test]
        public void Room_Shop_RoundTrip()
        {
            var f = new FlowManager();
            Assert.IsTrue(f.GoTo(ScreenId.Room));
            Assert.IsTrue(f.GoTo(ScreenId.Shop));    // Room -> 商城
            Assert.IsTrue(f.GoTo(ScreenId.Room));    // and back
            Assert.IsFalse(new FlowManager().CanGoTo(ScreenId.Shop));   // never straight from the entry screen
        }

        [Test]
        public void Blocked_Transitions_Are_Rejected()
        {
            var f = new FlowManager();
            Assert.IsFalse(f.CanGoTo(ScreenId.SongSelect));
            Assert.IsFalse(f.CanGoTo(ScreenId.Gameplay));
            Assert.IsFalse(f.GoTo(ScreenId.SongSelect));
            Assert.AreEqual(ScreenId.GenderSel, f.Current);
        }

        [Test]
        public void Entry_To_Gameplay_Blocked()
        {
            var f = new FlowManager();
            Assert.IsFalse(f.GoTo(ScreenId.Gameplay));
        }

        [Test]
        public void ScreenChanged_Fires_With_From_And_To()
        {
            var f = new FlowManager();
            ScreenId from = default, to = default;
            int n = 0;
            f.ScreenChanged += (a, b) => { from = a; to = b; n++; };
            f.GoTo(ScreenId.Room);
            Assert.AreEqual(1, n);
            Assert.AreEqual(ScreenId.GenderSel, from);
            Assert.AreEqual(ScreenId.Room, to);
        }

        [Test]
        public void Same_Screen_Does_Not_Fire()
        {
            var f = new FlowManager();
            int n = 0;
            f.ScreenChanged += (a, b) => n++;
            Assert.IsTrue(f.GoTo(ScreenId.GenderSel));
            Assert.AreEqual(0, n);
        }
    }
}
