using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    public class HealthProcessorTests
    {
        [Test]
        public void Starts_Full_And_Not_Failed()
        {
            var h = new HealthProcessor(0);
            Assert.AreEqual(HealthProcessor.MaxHealth, h.Health, 1e-9);
            Assert.AreEqual(1.0, h.Normalized, 1e-9);
            Assert.IsFalse(h.IsFailed);
        }

        [Test]
        public void Perfect_Is_Clamped_At_Max()
        {
            var h = new HealthProcessor(0);
            for (int i = 0; i < 50; i++) h.Apply(Judgment.Perfect);
            Assert.AreEqual(HealthProcessor.MaxHealth, h.Health, 1e-9);
        }

        [Test]
        public void Level0_Deltas_Are_Exact()
        {
            var h = new HealthProcessor(0);
            h.Apply(Judgment.Miss);                 // 1000 - 50
            Assert.AreEqual(950.0, h.Health, 1e-9);
            h.Apply(Judgment.Perfect);              // + 6
            Assert.AreEqual(956.0, h.Health, 1e-9);
            h.Apply(Judgment.Cool);                 // + 4
            Assert.AreEqual(960.0, h.Health, 1e-9);
            h.Apply(Judgment.Bad);                  // - 10
            Assert.AreEqual(950.0, h.Health, 1e-9);
        }

        [Test]
        public void Consecutive_Misses_Reach_Floor_And_Fail()
        {
            var h = new HealthProcessor(0);
            for (int i = 0; i < 100 && !h.IsFailed; i++) h.Apply(Judgment.Miss);
            Assert.IsTrue(h.IsFailed);
            Assert.AreEqual(HealthProcessor.FloorHealth, h.Health, 1e-9); // -150
        }

        [Test]
        public void Higher_Level_Loses_Less_On_Miss()
        {
            var lvl0 = new HealthProcessor(0); // miss -50
            var lvl2 = new HealthProcessor(2); // miss -30
            lvl0.Apply(Judgment.Miss);
            lvl2.Apply(Judgment.Miss);
            Assert.AreEqual(950.0, lvl0.Health, 1e-9);
            Assert.AreEqual(970.0, lvl2.Health, 1e-9);
            Assert.Less(lvl0.Health, lvl2.Health);
        }

        [Test]
        public void Invincible_Ignores_All_Deltas()
        {
            var h = new HealthProcessor(0, invincible: true);
            h.Apply(Judgment.Miss);
            h.Apply(Judgment.Bad);
            Assert.AreEqual(HealthProcessor.MaxHealth, h.Health, 1e-9);
            Assert.IsFalse(h.IsFailed);
        }

        [Test]
        public void Normalized_Maps_Health_To_0_1()
        {
            var h = new HealthProcessor(0);
            for (int i = 0; i < 10; i++) h.Apply(Judgment.Miss); // 1000 - 500 = 500
            Assert.AreEqual(500.0, h.Health, 1e-9);
            Assert.AreEqual(0.5, h.Normalized, 1e-9);
        }
    }
}
