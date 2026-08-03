using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// 開場設定面板那張表（<see cref="StartupConfigSchema"/>）。重點是**覆蓋率**：config.ini 裡每一個
    /// 「遊戲內沒有其它 UI 可以改」的 key 都要在表上，否則之後新增 key 卻忘了接 UI 這裡就會紅。
    /// 其餘驗每列的 Get/Set 來回一致與純小工具。不碰檔案、不畫 UI。
    /// </summary>
    public class StartupConfigSchemaTests
    {
        // 每個測試都直接讀寫 RoomConfig / DisplaySettingsManager 的 static 值 → 先備份、跑完還原，
        // 免得污染同一輪跑的其它測試（NUnit 同一個 domain）。
        private string _ini;
        private bool _hasOption, _hasOptUiScale, _hasSongBombsKey;
        private bool _danceIgnoreMiss, _collapseShortHolds;
        private float _uiScale;

        [SetUp]
        public void SetUp()
        {
            _ini = RoomConfig.Serialize();
            _hasOption = RoomConfig.hasOption;
            _hasOptUiScale = RoomConfig.hasOptUiScale;
            _hasSongBombsKey = RoomConfig.hasSongBombsKey;
            var s = DisplaySettingsManager.Settings;
            s.gameplay ??= new GameplaySettings();
            s.display ??= new DisplaySettings();
            _danceIgnoreMiss = s.gameplay.danceIgnoreMiss;
            _collapseShortHolds = s.gameplay.collapseShortHolds;
            _uiScale = s.display.uiScale;
        }

        [TearDown]
        public void TearDown()
        {
            RoomConfig.ParseInto(_ini);          // 所有 [Net]/[Room]/[Option] 欄位一次還原
            RoomConfig.hasOption = _hasOption;   // ParseInto 會把這幾個旗標打開 → 也要還原
            RoomConfig.hasOptUiScale = _hasOptUiScale;
            RoomConfig.hasSongBombsKey = _hasSongBombsKey;
            var s = DisplaySettingsManager.Settings;
            s.gameplay.danceIgnoreMiss = _danceIgnoreMiss;
            s.gameplay.collapseShortHolds = _collapseShortHolds;
            s.display.uiScale = _uiScale;
        }

        // ---------------------------------------------------------------- 覆蓋率
        [Test]
        public void Every_Config_Key_Without_Other_Ui_Is_On_The_Panel()
        {
            var all = KeysIn(RoomConfig.Serialize());
            var covered = new HashSet<string>(StartupConfigSchema.CoveredElsewhere);
            var onPanel = new HashSet<string>();
            foreach (var f in StartupConfigSchema.Fields) onPanel.Add(f.Key);

            var missing = new List<string>();
            foreach (var k in all) if (!covered.Contains(k) && !onPanel.Contains(k)) missing.Add(k);

            Assert.IsEmpty(missing,
                "config.ini 有 key 既不在 OPTION/房間面板（CoveredElsewhere），也不在開場設定面板上：" +
                string.Join(", ", missing) + " —— 新增設定時要接 UI，或明確列進 CoveredElsewhere。");
        }

        [Test]
        public void Panel_Has_No_Key_That_Config_Does_Not_Write()
        {
            var all = new HashSet<string>(KeysIn(RoomConfig.Serialize()));
            foreach (var f in StartupConfigSchema.Fields)
                Assert.IsTrue(all.Contains(f.Key), $"面板上的 {f.Key} 在 config.ini 裡沒有對應的 key（打錯字？）");
        }

        [Test]
        public void CoveredElsewhere_Keys_All_Exist_In_Config()
        {
            var all = new HashSet<string>(KeysIn(RoomConfig.Serialize()));
            foreach (var k in StartupConfigSchema.CoveredElsewhere)
                Assert.IsTrue(all.Contains(k), $"CoveredElsewhere 列了 {k}，但 config.ini 沒有這個 key（已改名/移除？）");
        }

        // ---------------------------------------------------------------- 表本身
        [Test]
        public void Fields_Have_Unique_Keys_And_A_Known_Category()
        {
            var seen = new HashSet<string>();
            var cats = new HashSet<string>(StartupConfigSchema.Categories);
            foreach (var f in StartupConfigSchema.Fields)
            {
                Assert.IsTrue(seen.Add(f.Key), $"重複的 key：{f.Key}");
                Assert.IsTrue(cats.Contains(f.Category), $"{f.Key} 的分頁 '{f.Category}' 不在 Categories 裡");
                Assert.IsNotEmpty(f.Label, f.Key + " 沒有標籤");
                Assert.IsNotEmpty(f.Help, f.Key + " 沒有說明");
                Assert.IsNotNull(f.Get, f.Key + " 沒有 Get");
                Assert.IsNotNull(f.Set, f.Key + " 沒有 Set");
            }
        }

        [Test]
        public void Every_Category_Has_Fields()
        {
            foreach (var c in StartupConfigSchema.Categories)
                Assert.IsNotEmpty(StartupConfigSchema.InCategory(c), $"分頁 {c} 是空的");
        }

        [Test]
        public void Slider_Fields_Have_A_Usable_Range()
        {
            foreach (var f in StartupConfigSchema.Fields)
            {
                if (f.Kind != ConfigFieldKind.Slider) continue;
                Assert.Less(f.Min, f.Max, f.Key + " 的滑桿範圍不合法");
                // 目前值必須落在範圍內 —— 否則面板一開就會把玩家的設定夾掉
                float v = f.GetNumber();
                Assert.GreaterOrEqual(v, f.Min, f.Key + " 目前值低於滑桿下限");
                Assert.LessOrEqual(v, f.Max, f.Key + " 目前值高於滑桿上限");
            }
        }

        [Test]
        public void Choice_Fields_Are_Well_Formed()
        {
            foreach (var f in StartupConfigSchema.Fields)
            {
                if (f.Kind != ConfigFieldKind.Choice) continue;
                Assert.IsNotNull(f.Choices, f.Key + " 沒有 Choices");
                Assert.Greater(f.Choices.Length, 1, f.Key + " 的 Choices 少於兩個");
                Assert.AreEqual(f.Choices.Length, f.ChoiceLabels?.Length ?? f.Choices.Length,
                                f.Key + " 的 ChoiceLabels 數量對不上");
            }
        }

        [Test]
        public void Get_Set_Round_Trips_For_Every_Field()
        {
            foreach (var f in StartupConfigSchema.Fields)
            {
                string before = f.Get();
                f.Set(before);
                Assert.AreEqual(before, f.Get(), f.Key + " 寫回自己的值之後變了");
            }
        }

        // ---------------------------------------------------------------- 個別欄位真的接到設定
        [Test]
        public void Toggle_Writes_Through_To_RoomConfig()
        {
            var f = StartupConfigSchema.ByKey("serverTls");
            Assert.IsNotNull(f);
            f.SetBool(true);
            Assert.IsTrue(RoomConfig.serverTls);
            Assert.IsTrue(f.GetBool());
            f.SetBool(false);
            Assert.IsFalse(RoomConfig.serverTls);
        }

        [Test]
        public void Slider_Snaps_To_Integer_Steps()
        {
            var f = StartupConfigSchema.ByKey("judgeLevel");
            Assert.IsNotNull(f);
            f.SetNumber(4.4f);
            Assert.AreEqual(4, RoomConfig.judgeLevel);
            f.SetNumber(99f);                       // 夾到上限
            Assert.AreEqual(9, RoomConfig.judgeLevel);
            Assert.AreEqual("JUSTICE", f.NumberText());
        }

        [Test]
        public void Choice_Cycles_And_Wraps()
        {
            var f = StartupConfigSchema.ByKey("DifficultyCalc");
            Assert.IsNotNull(f);
            f.Set("minacalc");
            f.StepChoice(1);
            Assert.AreEqual("osu", RoomConfig.difficultyCalc);
            f.StepChoice(1);                        // 循環回第一個
            Assert.AreEqual("minacalc", RoomConfig.difficultyCalc);
            f.StepChoice(-1);
            Assert.AreEqual("osu", RoomConfig.difficultyCalc);
        }

        [Test]
        public void Text_Field_Parses_Lists()
        {
            var f = StartupConfigSchema.ByKey("speedSteps");
            Assert.IsNotNull(f);
            f.Set("1, 2 ,3.5");
            CollectionAssert.AreEqual(new[] { 1f, 2f, 3.5f }, RoomConfig.speedSteps);
            f.Set("");                              // 空清單不接受（房間會沒有速度可選）→ 保留舊值
            CollectionAssert.AreEqual(new[] { 1f, 2f, 3.5f }, RoomConfig.speedSteps);

            var folders = StartupConfigSchema.ByKey("AdditionalSongFolders");
            folders.Set("D:/a ; E:/b");
            CollectionAssert.AreEqual(new[] { "D:/a", "E:/b" }, RoomConfig.additionalSongFolders);
            Assert.AreEqual("D:/a;E:/b", folders.Get());
        }

        [Test]
        public void Numeric_Text_Field_Keeps_Old_Value_On_Garbage()
        {
            var f = StartupConfigSchema.ByKey("serverPort");
            Assert.IsNotNull(f);
            f.Set("27017");
            Assert.AreEqual(27017, RoomConfig.serverPort);
            f.Set("");                              // 打到一半清空 → 不要變 0（會被 Sanitize 夾成 1）
            Assert.AreEqual(27017, RoomConfig.serverPort);
            f.Set("abc");
            Assert.AreEqual(27017, RoomConfig.serverPort);
        }

        [Test]
        public void Option_Mirror_Fields_Write_To_The_Runtime_Working_Copy()
        {
            // 這三個要寫進 DisplaySettingsManager.Settings（存檔走 CaptureOptionFrom），
            // 直接改 RoomConfig 鏡像的話下次 OPTION 按保存就會被工作副本蓋回去。
            var f = StartupConfigSchema.ByKey("opt_danceIgnoreMiss");
            Assert.IsNotNull(f);
            f.SetBool(true);
            Assert.IsTrue(DisplaySettingsManager.Settings.gameplay.danceIgnoreMiss);
            f.SetBool(false);
            Assert.IsFalse(DisplaySettingsManager.Settings.gameplay.danceIgnoreMiss);
        }

        // ---------------------------------------------------------------- 純小工具
        [Test]
        public void ParseBool_Accepts_The_Usual_Spellings()
        {
            Assert.IsTrue(StartupConfigSchema.ParseBool("1"));
            Assert.IsTrue(StartupConfigSchema.ParseBool("true"));
            Assert.IsTrue(StartupConfigSchema.ParseBool(" ON "));
            Assert.IsFalse(StartupConfigSchema.ParseBool("0"));
            Assert.IsFalse(StartupConfigSchema.ParseBool(""));
            Assert.IsFalse(StartupConfigSchema.ParseBool(null));
        }

        [Test]
        public void ParseFloatList_Skips_Junk()
        {
            CollectionAssert.AreEqual(new[] { 1f, 2.5f }, StartupConfigSchema.ParseFloatList("1, ,2.5,abc"));
            CollectionAssert.IsEmpty(StartupConfigSchema.ParseFloatList(null));
        }

        [Test]
        public void ParseStringList_Takes_Semicolons_And_Commas()
        {
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, StartupConfigSchema.ParseStringList(" a; b ,c ; "));
            CollectionAssert.IsEmpty(StartupConfigSchema.ParseStringList(""));
        }

        [Test]
        public void JudgeLevelText_Names_Nine_As_Justice()
        {
            Assert.AreEqual("精1", StartupConfigSchema.JudgeLevelText(1f));
            Assert.AreEqual("精4", StartupConfigSchema.JudgeLevelText(4.2f));
            Assert.AreEqual("JUSTICE", StartupConfigSchema.JudgeLevelText(9f));
            Assert.AreEqual("JUSTICE", StartupConfigSchema.JudgeLevelText(50f));   // 夾上限
        }

        // config.ini 文字裡所有 "key=" 的 key（略過註解/區段標頭/空行）。
        private static List<string> KeysIn(string ini)
        {
            var res = new List<string>();
            foreach (var raw in ini.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq > 0) res.Add(line.Substring(0, eq).Trim());
            }
            return res;
        }
    }
}
