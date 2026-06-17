using NUnit.Framework;
using Sdo.UI.Core;

namespace Sdo.Tests
{
    public class FlowManagerTests
    {
        [Test]
        public void Default_Is_Lobby()
            => Assert.AreEqual(ScreenId.Lobby, new FlowManager().Current);

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
        public void Blocked_Transitions_Are_Rejected()
        {
            var f = new FlowManager();
            Assert.IsFalse(f.CanGoTo(ScreenId.SongSelect));
            Assert.IsFalse(f.CanGoTo(ScreenId.Gameplay));
            Assert.IsFalse(f.GoTo(ScreenId.SongSelect));
            Assert.AreEqual(ScreenId.Lobby, f.Current);
        }

        [Test]
        public void Lobby_To_Gameplay_Blocked()
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
            Assert.AreEqual(ScreenId.Lobby, from);
            Assert.AreEqual(ScreenId.Room, to);
        }

        [Test]
        public void Same_Screen_Does_Not_Fire()
        {
            var f = new FlowManager();
            int n = 0;
            f.ScreenChanged += (a, b) => n++;
            Assert.IsTrue(f.GoTo(ScreenId.Lobby));
            Assert.AreEqual(0, n);
        }
    }
}
