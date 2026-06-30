using NUnit.Framework;
using Sdo.UI.Core;

namespace Sdo.Tests
{
    public class GameSessionTests
    {
        private static readonly float[] Steps = { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 4.0f, 5.0f, 6.0f, 8.0f };

        [Test]
        public void NearestSpeed_Exact_Match_Returns_Same()
        {
            Assert.AreEqual(2.5f, GameSession.NearestSpeed(Steps, 2.5f), 1e-4f);
            Assert.AreEqual(8.0f, GameSession.NearestSpeed(Steps, 8.0f), 1e-4f);
        }

        [Test]
        public void NearestSpeed_Snaps_To_Closest_Step()
        {
            Assert.AreEqual(2.5f, GameSession.NearestSpeed(Steps, 2.6f), 1e-4f);  // closer to 2.5 than 3.0
            Assert.AreEqual(4.0f, GameSession.NearestSpeed(Steps, 4.4f), 1e-4f);  // 4.0 vs 5.0
        }

        [Test]
        public void NearestSpeed_Out_Of_Range_Clamps_To_Ends()
        {
            Assert.AreEqual(1.0f, GameSession.NearestSpeed(Steps, 0.1f), 1e-4f);
            Assert.AreEqual(8.0f, GameSession.NearestSpeed(Steps, 99f), 1e-4f);
        }

        [Test]
        public void NearestSpeed_Empty_Or_Null_Returns_Want()
        {
            Assert.AreEqual(3.3f, GameSession.NearestSpeed(null, 3.3f), 1e-4f);
            Assert.AreEqual(3.3f, GameSession.NearestSpeed(new float[0], 3.3f), 1e-4f);
        }

        [Test]
        public void Default_Session_Starts_On_Random_Scene_And_Free_Team()
        {
            var s = new GameSession();
            Assert.IsTrue(s.StageRandom);        // 房間第二層圖預設顯示 RANDOM
            Assert.AreEqual(3, s.Team);          // 組隊預設自由
            Assert.AreEqual(0, s.DropDirection); // 掉落方式預設向上
            Assert.AreEqual(0, s.GameMode);      // 預設自由模式
            Assert.AreEqual(-1, s.NoteType);     // note 種類預設隨機
        }
    }
}
