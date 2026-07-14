using NUnit.Framework;
using Sdo.UI.Core;

namespace Sdo.Tests
{
    /// <summary>單機開場落在男/女選擇畫面（GenderSel），選完直接進 Room —— Lobby 只在 Room 之後才回得去。</summary>
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
