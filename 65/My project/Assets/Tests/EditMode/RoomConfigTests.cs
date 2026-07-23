using System.IO;
using NUnit.Framework;
using Sdo.Settings;

namespace Sdo.Tests
{
    public class RoomConfigTests
    {
        [Test]
        public void FilePath_Resolves_Under_ProfileRoot_Not_ExeDir()
        {
            // config.ini 現在放在存檔層 DATA/PROFILE/（＝ProfileManager.Root，與 settings.json / active.txt 同層），
            // 不再是執行檔同層。這是「把 config.ini 搬進 profile 資料夾」的核心行為。
            string root = Path.Combine(Path.GetTempPath(), "sdo_cfg_root");
            try
            {
                ProfileManager.Root = root;
                Assert.AreEqual(Path.GetFullPath(Path.Combine(root, RoomConfig.FileName)),
                                Path.GetFullPath(RoomConfig.FilePath), "config.ini 應落在 PROFILE 資料夾下");
                Assert.AreNotEqual(Path.GetFullPath(RoomConfig.FilePath),
                                   Path.GetFullPath(RoomConfig.LegacyExePath), "新位置要跟舊的執行檔同層不同");
            }
            finally { ProfileManager.Root = null; }   // 還原 lazy 解析，避免污染其他測試
        }

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
            RoomConfig.judgeLevel = 2;
            RoomConfig.familyName = "";
            RoomConfig.familyEmblem = "SMALL43";
            RoomConfig.playerLevel = "";
        }

        [Test]
        public void Family_And_Level_Parse_And_RoundTrip()
        {
            RoomConfig.ParseInto("[Profile]\nfamilyName=天使家族\nfamilyEmblem=SMALL7\nplayerLevel=42\n");
            Assert.AreEqual("天使家族", RoomConfig.familyName);
            Assert.AreEqual("SMALL7", RoomConfig.familyEmblem);
            Assert.AreEqual("42", RoomConfig.playerLevel);

            string ini = RoomConfig.Serialize();
            Reset();
            RoomConfig.ParseInto(ini);
            Assert.AreEqual("天使家族", RoomConfig.familyName);
            Assert.AreEqual("SMALL7", RoomConfig.familyEmblem);
            Assert.AreEqual("42", RoomConfig.playerLevel);
        }

        [Test]
        public void Family_And_Level_Sanitize_Trims_Whitespace()
        {
            // 前後空白會讓「留空＝不顯示」的判定失準(看似有值其實是空白) → Sanitize 去頭尾空白。
            RoomConfig.familyName = "  ";
            RoomConfig.playerLevel = "  ";
            RoomConfig.familyEmblem = "  SMALL43  ";
            RoomConfig.Sanitize();
            Assert.AreEqual("", RoomConfig.familyName);
            Assert.AreEqual("", RoomConfig.playerLevel);
            Assert.AreEqual("SMALL43", RoomConfig.familyEmblem);
        }

        [Test]
        public void LevelLabel_Formats_NonEmpty_And_Blank_For_Empty()
        {
            Assert.AreEqual("LV:11", RoomConfig.LevelLabel("11"));
            Assert.AreEqual("LV:11", RoomConfig.LevelLabel("  11  "));   // 去頭尾空白後仍成立
            Assert.AreEqual("", RoomConfig.LevelLabel(""));              // 留空 → 不顯示
            Assert.AreEqual("", RoomConfig.LevelLabel("   "));
            Assert.AreEqual("", RoomConfig.LevelLabel(null));
        }

        [Test]
        public void Defaults_Hide_Family_And_Level()
        {
            // 內建預設：家族名稱/等級留空 → 不顯示；徽章雖預設 SMALL43，但沒有家族名就整條不畫。
            Assert.AreEqual("", RoomConfig.familyName);
            Assert.AreEqual("", RoomConfig.playerLevel);
            Assert.AreEqual("SMALL43", RoomConfig.familyEmblem);
            Assert.AreEqual("", RoomConfig.LevelLabel(RoomConfig.playerLevel));
        }

        [Test]
        public void JudgeLevel_Parses_Clamps_And_RoundTrips()
        {
            RoomConfig.ParseInto("[Room]\njudgeLevel=7\n");
            Assert.AreEqual(7, RoomConfig.judgeLevel);

            RoomConfig.judgeLevel = 0;  RoomConfig.Sanitize();   // 精1 是下限
            Assert.AreEqual(1, RoomConfig.judgeLevel);
            RoomConfig.judgeLevel = 42; RoomConfig.Sanitize();   // 9 = JUSTICE 是上限
            Assert.AreEqual(9, RoomConfig.judgeLevel);

            RoomConfig.judgeLevel = 4;
            string ini = RoomConfig.Serialize();
            Reset();
            RoomConfig.ParseInto(ini);
            Assert.AreEqual(4, RoomConfig.judgeLevel);
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
            Assert.AreEqual(2, RoomConfig.defaultGameMode);   // 0=自由 1=普通 2=ShowTime → 上限是 2
        }

        [Test]
        public void Sanitize_Allows_Tilt_And_Clamps_Above()
        {
            RoomConfig.defaultDropDirection = 2;   // 傾斜 is a valid third option now
            RoomConfig.Sanitize();
            Assert.AreEqual(2, RoomConfig.defaultDropDirection);

            RoomConfig.defaultDropDirection = 3;   // out of range → clamps down to 傾斜(2)
            RoomConfig.Sanitize();
            Assert.AreEqual(2, RoomConfig.defaultDropDirection);
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
