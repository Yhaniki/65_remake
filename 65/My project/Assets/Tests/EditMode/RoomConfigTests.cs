using NUnit.Framework;
using Sdo.Settings;

namespace Sdo.Tests
{
    public class RoomConfigTests
    {
        // Reset to built-in defaults before each case (RoomConfig holds static state).
        [SetUp]
        public void Reset()
        {
            RoomConfig.speedSteps = new[] { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 4.0f, 5.0f, 6.0f, 8.0f };
            RoomConfig.defaultSpeed = 2.5f;
            RoomConfig.defaultNoteType = -1;
            RoomConfig.defaultTeam = 3;
            RoomConfig.defaultDropDirection = 0;
            RoomConfig.defaultGameMode = 0;
        }

        [Test]
        public void ParseInto_Reads_Keys_And_SpeedArray()
        {
            RoomConfig.ParseInto(
                "# comment\n[Room]\nspeedSteps = 2.0, 4.0 ,8.0\ndefaultSpeed=4.0\n" +
                "defaultNoteType=2\ndefaultTeam=1\ndefaultDropDirection=1\ndefaultGameMode=1\n");
            CollectionAssert.AreEqual(new[] { 2.0f, 4.0f, 8.0f }, RoomConfig.speedSteps);
            Assert.AreEqual(4.0f, RoomConfig.defaultSpeed, 1e-4f);
            Assert.AreEqual(2, RoomConfig.defaultNoteType);
            Assert.AreEqual(1, RoomConfig.defaultTeam);
            Assert.AreEqual(1, RoomConfig.defaultDropDirection);
            Assert.AreEqual(1, RoomConfig.defaultGameMode);
        }

        [Test]
        public void ParseInto_Ignores_Comments_Sections_And_Unknown_Keys()
        {
            RoomConfig.ParseInto("; semicolon comment\n[Other]\nbogus=123\ndefaultTeam=2\n");
            Assert.AreEqual(2, RoomConfig.defaultTeam);
            Assert.AreEqual(2.5f, RoomConfig.defaultSpeed, 1e-4f);   // untouched
        }

        [Test]
        public void Sanitize_Repairs_Invalid()
        {
            RoomConfig.speedSteps = new float[0];
            RoomConfig.defaultSpeed = 0f;
            RoomConfig.defaultNoteType = -9;
            RoomConfig.defaultTeam = 99;
            RoomConfig.defaultDropDirection = -5;
            RoomConfig.defaultGameMode = 7;
            RoomConfig.Sanitize();
            Assert.Greater(RoomConfig.speedSteps.Length, 0);
            Assert.AreEqual(2.5f, RoomConfig.defaultSpeed, 1e-4f);
            Assert.AreEqual(-1, RoomConfig.defaultNoteType);
            Assert.AreEqual(3, RoomConfig.defaultTeam);
            Assert.AreEqual(0, RoomConfig.defaultDropDirection);
            Assert.AreEqual(1, RoomConfig.defaultGameMode);
        }

        [Test]
        public void Serialize_Then_ParseInto_RoundTrips()
        {
            RoomConfig.speedSteps = new[] { 1.5f, 3.0f, 6.0f };
            RoomConfig.defaultSpeed = 3.0f;
            RoomConfig.defaultTeam = 2;
            RoomConfig.defaultDropDirection = 1;
            string ini = RoomConfig.Serialize();
            Reset();   // wipe back to defaults
            RoomConfig.ParseInto(ini);
            CollectionAssert.AreEqual(new[] { 1.5f, 3.0f, 6.0f }, RoomConfig.speedSteps);
            Assert.AreEqual(3.0f, RoomConfig.defaultSpeed, 1e-4f);
            Assert.AreEqual(2, RoomConfig.defaultTeam);
            Assert.AreEqual(1, RoomConfig.defaultDropDirection);
        }
    }
}
