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
            RoomConfig.familyName = "";
            RoomConfig.familyEmblem = "SMALL43";
            RoomConfig.playerLevel = "";
            RoomConfig.comboTextScale = 1f;
            RoomConfig.judgeTextScale = 1f;
            RoomConfig.hasTextScaleKeys = false;
            RoomConfig.comboTextAlpha = 0.6f;
            RoomConfig.judgeTextAlpha = 0.6f;
            RoomConfig.hasTextAlphaKeys = false;
            RoomConfig.comboTextPop = 2f;
            RoomConfig.judgeTextPop = 2f;
            RoomConfig.hasTextPopKeys = false;
        }

        [Test]
        public void TextScales_Missing_From_An_Old_File_Flag_A_Template_TopUp()
        {
            // 舊 config.ini 沒有這兩個鍵 → Load 要重寫一次模板，否則使用者在檔案裡根本找不到鍵可以改。
            RoomConfig.ParseInto("[Room]\njudgeLevel=4\n");
            Assert.IsFalse(RoomConfig.hasTextScaleKeys);
            RoomConfig.ParseInto("[Room]\ncomboTextScale=1.2\n");
            Assert.IsTrue(RoomConfig.hasTextScaleKeys);
            // 自己寫出來的模板一定帶鍵（補寫一次之後不會每次開機都重寫）。
            Reset();
            RoomConfig.ParseInto(RoomConfig.Serialize());
            Assert.IsTrue(RoomConfig.hasTextScaleKeys);
        }

        [Test]
        public void TextScales_Default_To_One_Parse_Clamp_And_RoundTrip()
        {
            Assert.AreEqual(1f, RoomConfig.comboTextScale, 1e-4f, "預設＝官方原尺寸");
            Assert.AreEqual(1f, RoomConfig.judgeTextScale, 1e-4f);

            RoomConfig.ParseInto("[Room]\ncomboTextScale=1.35\njudgeTextScale=0.75\n");
            Assert.AreEqual(1.35f, RoomConfig.comboTextScale, 1e-4f);
            Assert.AreEqual(0.75f, RoomConfig.judgeTextScale, 1e-4f);

            RoomConfig.comboTextScale = 0f;  RoomConfig.judgeTextScale = 99f;   // 0 會整組消失、99 會蓋滿畫面
            RoomConfig.Sanitize();
            Assert.AreEqual(0.2f, RoomConfig.comboTextScale, 1e-4f);
            Assert.AreEqual(3f, RoomConfig.judgeTextScale, 1e-4f);

            RoomConfig.comboTextScale = 1.5f; RoomConfig.judgeTextScale = 0.8f;
            string ini = RoomConfig.Serialize();
            Reset();
            RoomConfig.ParseInto(ini);
            Assert.AreEqual(1.5f, RoomConfig.comboTextScale, 1e-4f);
            Assert.AreEqual(0.8f, RoomConfig.judgeTextScale, 1e-4f);
        }

        [Test]
        public void TextAlphas_Missing_From_An_Old_File_Flag_A_Template_TopUp()
        {
            // 透明度鍵比大小鍵晚加：只有 comboTextScale/judgeTextScale 的檔一樣要補寫模板，
            // 否則使用者在檔案裡找不到透明度可以改（hasTextScaleKeys 不能拿來代表這兩個鍵）。
            RoomConfig.ParseInto("[Room]\ncomboTextScale=1.2\njudgeTextScale=0.8\n");
            Assert.IsTrue(RoomConfig.hasTextScaleKeys);
            Assert.IsFalse(RoomConfig.hasTextAlphaKeys, "只有大小鍵的舊檔仍要補寫透明度鍵");

            RoomConfig.ParseInto("[Room]\ncomboTextAlpha=0.4\n");
            Assert.IsTrue(RoomConfig.hasTextAlphaKeys);

            Reset();
            RoomConfig.ParseInto(RoomConfig.Serialize());   // 自己寫出來的模板一定帶鍵 → 補寫一次就不再重寫
            Assert.IsTrue(RoomConfig.hasTextAlphaKeys);
        }

        [Test]
        public void TextAlphas_Default_To_Six_Tenths_Parse_Clamp_And_RoundTrip()
        {
            Assert.AreEqual(0.6f, RoomConfig.comboTextAlpha, 1e-4f, "預設 0.6：字疊在音符板上，全不透明會擋住音符");
            Assert.AreEqual(0.6f, RoomConfig.judgeTextAlpha, 1e-4f);

            RoomConfig.ParseInto("[Room]\ncomboTextAlpha=0.35\njudgeTextAlpha=1\n");
            Assert.AreEqual(0.35f, RoomConfig.comboTextAlpha, 1e-4f);
            Assert.AreEqual(1f, RoomConfig.judgeTextAlpha, 1e-4f);

            RoomConfig.comboTextAlpha = -0.5f; RoomConfig.judgeTextAlpha = 4f;   // 只有 0~1 有意義
            RoomConfig.Sanitize();
            Assert.AreEqual(0f, RoomConfig.comboTextAlpha, 1e-4f, "0＝完全隱藏，是合法用法不是錯誤值");
            Assert.AreEqual(1f, RoomConfig.judgeTextAlpha, 1e-4f);

            RoomConfig.comboTextAlpha = 0.75f; RoomConfig.judgeTextAlpha = 0.2f;
            string ini = RoomConfig.Serialize();
            Reset();
            RoomConfig.ParseInto(ini);
            Assert.AreEqual(0.75f, RoomConfig.comboTextAlpha, 1e-4f);
            Assert.AreEqual(0.2f, RoomConfig.judgeTextAlpha, 1e-4f);
        }

        [Test]
        public void TextPops_Default_To_Official_Two_Parse_Clamp_And_RoundTrip()
        {
            Assert.AreEqual(2f, RoomConfig.comboTextPop, 1e-4f, "官方＝彈到靜止大小的兩倍再收回");
            Assert.AreEqual(2f, RoomConfig.judgeTextPop, 1e-4f);

            RoomConfig.ParseInto("[Room]\ncomboTextPop=3\njudgeTextPop=1\n");
            Assert.AreEqual(3f, RoomConfig.comboTextPop, 1e-4f);
            Assert.AreEqual(1f, RoomConfig.judgeTextPop, 1e-4f, "1＝完全不彈跳，是合法設定");
            Assert.IsTrue(RoomConfig.hasTextPopKeys);

            RoomConfig.comboTextPop = 0.3f; RoomConfig.judgeTextPop = 99f;   // <1 會變成先縮再放、99 直接衝出面板
            RoomConfig.Sanitize();
            Assert.AreEqual(1f, RoomConfig.comboTextPop, 1e-4f);
            Assert.AreEqual(4f, RoomConfig.judgeTextPop, 1e-4f);

            RoomConfig.comboTextPop = 2.5f; RoomConfig.judgeTextPop = 1.4f;
            string ini = RoomConfig.Serialize();
            Reset();
            RoomConfig.ParseInto(ini);
            Assert.AreEqual(2.5f, RoomConfig.comboTextPop, 1e-4f);
            Assert.AreEqual(1.4f, RoomConfig.judgeTextPop, 1e-4f);
        }

        [Test]
        public void TextPops_Missing_From_An_Old_File_Flag_A_Template_TopUp()
        {
            // 彈跳鍵比大小/透明度鍵都晚加 → 已經帶那兩組的檔仍要補寫一次，不然找不到鍵可改。
            RoomConfig.ParseInto("[Room]\ncomboTextScale=1.2\ncomboTextAlpha=0.5\n");
            Assert.IsTrue(RoomConfig.hasTextScaleKeys);
            Assert.IsTrue(RoomConfig.hasTextAlphaKeys);
            Assert.IsFalse(RoomConfig.hasTextPopKeys);

            Reset();
            RoomConfig.ParseInto(RoomConfig.Serialize());
            Assert.IsTrue(RoomConfig.hasTextPopKeys);
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
