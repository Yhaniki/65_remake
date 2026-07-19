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

        [Test]
        public void IsMissingCurrentKey_DetectsOldConfigMissingNewKeys()
        {
            // canonical（Serialize 剛寫出的）內容 → 一個 key 都不缺
            Assert.IsFalse(RoomConfig.IsMissingCurrentKey(RoomConfig.Serialize()));
            // 舊版存的內容缺這版新增的 AdditionalSongFolders → 偵測為缺 key（Load 會補寫升級，讓新 key 出現可手改）
            string old = "[Room]\ndefaultSpeed=2.5\ndefaultTeam=3\n[Option]\nopt_bgm=0.5\n";
            Assert.IsFalse(old.Contains("AdditionalSongFolders"), "前提：這份舊內容確實沒有該 key");
            Assert.IsTrue(RoomConfig.IsMissingCurrentKey(old));
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
