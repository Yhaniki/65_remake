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
            {
                // Action 列是按鈕不是設定值（例：把 MMD 物理存成模型自己的 physics.ini），本來就沒有 config.ini key。
                if (f.Kind == ConfigFieldKind.Action) continue;
                Assert.IsTrue(all.Contains(f.Key), $"面板上的 {f.Key} 在 config.ini 裡沒有對應的 key（打錯字？）");
            }
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
                if (f.Kind == ConfigFieldKind.Action)
                {
                    Assert.IsNotNull(f.Invoke, f.Key + " 是按鈕列卻沒有 Invoke");
                    Assert.IsNotEmpty(f.Actions, f.Key + " 是按鈕列卻沒有按鈕文字");
                    continue;
                }
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
        public void Only_JudgeLevel_Blocks_Typing_A_Value()
        {
            // 使用者要求：滑桿右邊都要能直接打字，只有判定精度例外（它的值是「精4」「JUSTICE」，打字沒有意義）。
            foreach (var f in StartupConfigSchema.Fields)
            {
                if (f.Kind != ConfigFieldKind.Slider) continue;
                bool expected = f.Key == "judgeLevel";
                Assert.AreEqual(expected, f.NoValueEntry, f.Key + " 的「可否打字」跟預期不符");
                if (f.NoValueEntry) Assert.IsNotNull(f.Format, f.Key + " 不能打字就要有 Format 把值畫成文字");
                else Assert.AreEqual(ConfigField.NumberToText(f.GetNumber()), f.NumberText(),
                                     f.Key + " 可打字的欄位要顯示純數字（單位走 Unit）");
            }
        }

        [Test]
        public void Units_Only_On_Sliders()
        {
            foreach (var f in StartupConfigSchema.Fields)
                if (f.Kind != ConfigFieldKind.Slider)
                    Assert.IsTrue(string.IsNullOrEmpty(f.Unit), f.Key + " 不是滑桿卻帶了單位");
        }

        [Test]
        public void Typed_Value_Is_Clamped_To_The_Slider_Range()
        {
            var f = StartupConfigSchema.ByKey("scrollBaseBpm");
            Assert.IsNotNull(f);
            f.SetNumber(1000f);                     // 手打超出上限 → 夾到 400
            Assert.AreEqual(400f, RoomConfig.scrollBaseBpm, 0.001f);
            f.SetNumber(-5f);                       // 低於下限 → 夾到 30
            Assert.AreEqual(30f, RoomConfig.scrollBaseBpm, 0.001f);
        }

        [Test]
        public void Choice_Fields_Are_Well_Formed()
        {
            foreach (var f in StartupConfigSchema.Fields)
            {
                if (f.Kind != ConfigFieldKind.Choice) continue;
                if (f.ChoicesProvider != null)
                {
                    // 動態選項（MMD 模型＝掃資料夾掃出來的）：清單可以是空的（一個模型都沒裝），
                    // 但值不在清單裡時一定要有話可說，否則面板會顯示一片空白。
                    Assert.IsNotNull(f.UnknownChoiceText, f.Key + " 是動態選項，卻沒有 UnknownChoiceText");
                    continue;
                }
                Assert.IsNotNull(f.Choices, f.Key + " 沒有 Choices");
                Assert.Greater(f.Choices.Length, 1, f.Key + " 的 Choices 少於兩個");
                Assert.AreEqual(f.Choices.Length, f.ChoiceLabels?.Length ?? f.Choices.Length,
                                f.Key + " 的 ChoiceLabels 數量對不上");
            }
        }

        // ---------------------------------------------------------------- 動態選項（MMD 模型）
        [Test]
        public void Dynamic_Choice_Cycles_Through_Whatever_Is_Installed()
        {
            var f = StartupConfigSchema.ByKey("mmdModel");
            Assert.IsNotNull(f);
            var saved = StartupConfigSchema.MmdModelsProvider;
            try
            {
                StartupConfigSchema.MmdModelsProvider = () => new[] { "Miku", "Rin" };
                f.Set("Miku");
                Assert.AreEqual("Miku", f.ChoiceText());
                f.StepChoice(1);
                Assert.AreEqual("Rin", RoomConfig.mmdModel);
                f.StepChoice(1);                       // 循環回第一個
                Assert.AreEqual("Miku", RoomConfig.mmdModel);
                f.StepChoice(-1);
                Assert.AreEqual("Rin", RoomConfig.mmdModel);
            }
            finally { StartupConfigSchema.MmdModelsProvider = saved; }
        }

        [Test]
        public void Dynamic_Choice_Shows_A_Value_That_Is_Not_Installed_Verbatim()
        {
            // 設定檔指名的模型被刪掉/還沒掃到時，面板要照實說「找不到」而不是默默跳成別的模型
            // —— 默默跳掉的話，按一次「儲存設定」玩家指定的名字就永久沒了。
            var f = StartupConfigSchema.ByKey("mmdModel");
            var saved = StartupConfigSchema.MmdModelsProvider;
            try
            {
                StartupConfigSchema.MmdModelsProvider = () => new[] { "Miku" };
                f.Set("NotInstalled");
                StringAssert.Contains("NotInstalled", f.ChoiceText());
                Assert.AreEqual("NotInstalled", RoomConfig.mmdModel, "只是顯示，值不該被改掉");
                f.StepChoice(1);                       // 不在清單裡 → 往右進第一個
                Assert.AreEqual("Miku", RoomConfig.mmdModel);

                // 一個模型都沒裝：按 ◀▶ 不能爆、也不能亂寫值
                StartupConfigSchema.MmdModelsProvider = () => new string[0];
                f.Set("Miku");
                f.StepChoice(1);
                Assert.AreEqual("Miku", RoomConfig.mmdModel);
                Assert.IsNotEmpty(f.ChoiceText());
            }
            finally { StartupConfigSchema.MmdModelsProvider = saved; }
        }

        [Test]
        public void Mmd_Settings_Write_Through_To_RoomConfig()
        {
            var on = StartupConfigSchema.ByKey("mmdEnabled");
            Assert.IsNotNull(on);
            on.SetBool(true);
            Assert.IsTrue(RoomConfig.mmdEnabled);
            on.SetBool(false);
            Assert.IsFalse(RoomConfig.mmdEnabled);

            var grav = StartupConfigSchema.ByKey("mmdGravity");
            grav.SetNumber(99f);                        // 夾到上限
            Assert.AreEqual(8f, RoomConfig.mmdGravity, 0.001f);
            grav.SetNumber(0f);                         // 夾到下限（0 = 布料不落下）
            Assert.AreEqual(0.05f, RoomConfig.mmdGravity, 0.001f);
        }

        [Test]
        public void Mmd_Values_Survive_A_Config_Round_Trip()
        {
            // 這一整組就是這次搬家的重點：以前只活在記憶體裡的除錯面板值，現在要寫得進也讀得回 config.ini。
            RoomConfig.mmdEnabled = true;
            RoomConfig.mmdModel = "SomeModel";
            RoomConfig.mmdPhysics = false;
            RoomConfig.mmdFlipV = false;
            RoomConfig.mmdGravity = 2.5f;
            RoomConfig.mmdStiffness = 0.4f;
            RoomConfig.mmdColliderScale = 1.75f;
            string ini = RoomConfig.Serialize();

            RoomConfig.mmdEnabled = false; RoomConfig.mmdModel = ""; RoomConfig.mmdPhysics = true;
            RoomConfig.mmdFlipV = true; RoomConfig.mmdGravity = 1f; RoomConfig.mmdStiffness = 0.12f;
            RoomConfig.mmdColliderScale = 1f;

            RoomConfig.ParseInto(ini);
            Assert.IsTrue(RoomConfig.mmdEnabled);
            Assert.AreEqual("SomeModel", RoomConfig.mmdModel);
            Assert.IsFalse(RoomConfig.mmdPhysics);
            Assert.IsFalse(RoomConfig.mmdFlipV);
            Assert.AreEqual(2.5f, RoomConfig.mmdGravity, 0.001f);
            Assert.AreEqual(0.4f, RoomConfig.mmdStiffness, 0.001f);
            Assert.AreEqual(1.75f, RoomConfig.mmdColliderScale, 0.001f);
        }

        [Test]
        public void Mmd_Sanitize_Clamps_To_The_Same_Range_As_The_Sliders()
        {
            // 兩邊範圍不一致的話，面板一開就會把手改的設定夾掉（或滑桿拉不到設定檔允許的值）。
            RoomConfig.mmdGravity = 999f; RoomConfig.mmdStiffness = 999f; RoomConfig.mmdColliderScale = 999f;
            RoomConfig.Sanitize();
            Assert.AreEqual(StartupConfigSchema.ByKey("mmdGravity").Max, RoomConfig.mmdGravity, 0.001f);
            Assert.AreEqual(StartupConfigSchema.ByKey("mmdStiffness").Max, RoomConfig.mmdStiffness, 0.001f);
            Assert.AreEqual(StartupConfigSchema.ByKey("mmdColliderScale").Max, RoomConfig.mmdColliderScale, 0.001f);

            RoomConfig.mmdGravity = -1f; RoomConfig.mmdStiffness = -1f; RoomConfig.mmdColliderScale = -1f;
            RoomConfig.Sanitize();
            Assert.AreEqual(StartupConfigSchema.ByKey("mmdGravity").Min, RoomConfig.mmdGravity, 0.001f);
            Assert.AreEqual(StartupConfigSchema.ByKey("mmdStiffness").Min, RoomConfig.mmdStiffness, 0.001f);
            Assert.AreEqual(StartupConfigSchema.ByKey("mmdColliderScale").Min, RoomConfig.mmdColliderScale, 0.001f);
        }

        [Test]
        public void Get_Set_Round_Trips_For_Every_Field()
        {
            foreach (var f in StartupConfigSchema.Fields)
            {
                if (f.Kind == ConfigFieldKind.Action) continue;   // 按鈕列沒有值
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
