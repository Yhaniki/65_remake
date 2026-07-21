using NUnit.Framework;
using UnityEngine;
using Sdo.Settings;
using Sdo.UI.Screens;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>Pure-logic cover for the OPTION dialog's rebindable keys + chip captions.
    /// (Layout/art is exercised by running the app; these lock the settings + display logic.)</summary>
    public class OptionDlgTests
    {
        // ---- KeyBindSettings.ToLaneKeys ----

        [Test]
        public void KeyBind_Defaults_Map_To_ASWD_And_Arrows()
        {
            var lanes = new KeyBindSettings().ToLaneKeys();
            Assert.AreEqual(4, lanes.Length);
            Assert.AreEqual(KeyCode.A, lanes[0][0]); Assert.AreEqual(KeyCode.LeftArrow, lanes[0][1]);
            Assert.AreEqual(KeyCode.S, lanes[1][0]); Assert.AreEqual(KeyCode.DownArrow, lanes[1][1]);
            Assert.AreEqual(KeyCode.W, lanes[2][0]); Assert.AreEqual(KeyCode.UpArrow, lanes[2][1]);
            Assert.AreEqual(KeyCode.D, lanes[3][0]); Assert.AreEqual(KeyCode.RightArrow, lanes[3][1]);
        }

        [Test]
        public void KeyBind_Custom_Primary_Parsed_Aux_Defaults()
        {
            var lanes = new KeyBindSettings { lane4 = new[] { "J", "K", "I", "L" } }.ToLaneKeys();
            Assert.AreEqual(KeyCode.J, lanes[0][0]);
            Assert.AreEqual(KeyCode.L, lanes[3][0]);
            Assert.AreEqual(KeyCode.LeftArrow, lanes[0][1]);   // aux untouched -> default
        }

        // ---- KeyBindSettings.SanitizeNames (pure) ----

        [Test]
        public void SanitizeNames_Fills_Missing_And_Invalid_From_Default()
        {
            var res = KeyBindSettings.SanitizeNames(new[] { "J", "zzzz", null }, KeyBindSettings.DefaultPrimary);
            Assert.AreEqual(4, res.Length);
            Assert.AreEqual("J", res[0]);   // valid kept
            Assert.AreEqual("S", res[1]);   // invalid -> default lane1 (S)
            Assert.AreEqual("W", res[2]);   // null    -> default lane2 (W)
            Assert.AreEqual("D", res[3]);   // missing -> default lane3 (D)
        }

        [Test]
        public void SanitizeNames_Null_Returns_Defaults()
        {
            var res = KeyBindSettings.SanitizeNames(null, KeyBindSettings.DefaultAux);
            Assert.AreEqual("LeftArrow", res[0]);
            Assert.AreEqual("RightArrow", res[3]);
        }

        [Test]
        public void SanitizeNames_Preserves_Cleared_Empty_Slots()
        {
            // "" = 使用者在鍵盤頁刻意清空(去重)的格 → 必須原樣保留，不能還原成預設鍵(否則被清掉的鍵又冒出來/再重複)。
            var res = KeyBindSettings.SanitizeNames(new[] { "", "S", "", "D" }, KeyBindSettings.DefaultPrimary);
            Assert.AreEqual("", res[0]);
            Assert.AreEqual("S", res[1]);
            Assert.AreEqual("", res[2]);
            Assert.AreEqual("D", res[3]);
        }

        [Test]
        public void ToLaneKeys_Empty_Binding_Maps_To_None_Not_Default()
        {
            // 清空的主鍵位 → KeyCode.None(遊戲中不觸發)，不回退成該 lane 預設鍵；輔助鍵位不受影響仍可打。
            var lanes = new KeyBindSettings { lane4 = new[] { "", "S", "W", "D" } }.ToLaneKeys();
            Assert.AreEqual(KeyCode.None, lanes[0][0]);        // 清空 → None
            Assert.AreEqual(KeyCode.LeftArrow, lanes[0][1]);   // 該 lane 仍可用輔助鍵
            Assert.AreEqual(KeyCode.S, lanes[1][0]);           // 其餘不受影響
        }

        // ---- OptionDlgModal.ClearDuplicateBinding (pure) — 一鍵只綁一處 ----

        [Test]
        public void ClearDuplicate_Clears_Previous_Same_Key_In_Same_Row()
        {
            var prim = new[] { "A", "A", "W", "D" };   // 剛把 lane1 主鍵綁成 A，和 lane0 重複
            var aux = new[] { "LeftArrow", "DownArrow", "UpArrow", "RightArrow" };
            var cleared = OptionDlgModal.ClearDuplicateBinding(prim, aux, 0 /*prim*/, 1 /*剛綁的 lane*/);
            Assert.AreEqual("", prim[0]);              // 前一個重複的 A 被清掉
            Assert.AreEqual("A", prim[1]);             // 剛綁的保留
            Assert.IsTrue(cleared.Contains((0, 0)));
        }

        [Test]
        public void ClearDuplicate_Clears_Across_Primary_And_Aux()
        {
            var prim = new[] { "A", "S", "W", "D" };
            var aux = new[] { "A", "DownArrow", "UpArrow", "RightArrow" };   // 剛把輔助 lane0 綁成 A
            var cleared = OptionDlgModal.ClearDuplicateBinding(prim, aux, 1 /*aux*/, 0);
            Assert.AreEqual("", prim[0]);              // 主鍵位那個重複的 A 被跨排清掉
            Assert.AreEqual("A", aux[0]);              // 剛綁的保留
            Assert.IsTrue(cleared.Contains((0, 0)));
        }

        [Test]
        public void ClearDuplicate_Empty_Key_Is_NoOp()
        {
            var prim = new[] { "", "S", "W", "D" };
            var aux = new[] { "", "DownArrow", "UpArrow", "RightArrow" };
            var cleared = OptionDlgModal.ClearDuplicateBinding(prim, aux, 0, 0);   // 剛綁的是 "" → 不該去清別的空格
            Assert.IsEmpty(cleared);
            Assert.AreEqual("", aux[0]);               // 另一個 "" 不受影響
        }

        [Test]
        public void Sanitize_Repairs_Key_Bindings()
        {
            var s = new GameSettings();
            s.keys.lane4 = new[] { "A" };      // too short
            s.keys.lane4aux = null;
            DisplaySettingsManager.Sanitize(s);
            Assert.AreEqual(4, s.keys.lane4.Length);
            Assert.AreEqual("A", s.keys.lane4[0]);
            Assert.AreEqual("S", s.keys.lane4[1]);
            Assert.AreEqual(4, s.keys.lane4aux.Length);
            Assert.AreEqual("LeftArrow", s.keys.lane4aux[0]);
        }

        [Test]
        public void KeyBinds_Survive_Json_RoundTrip()
        {
            var s = new GameSettings();
            s.keys.lane4 = new[] { "J", "K", "I", "L" };
            var b = JsonUtility.FromJson<GameSettings>(JsonUtility.ToJson(s));
            CollectionAssert.AreEqual(new[] { "J", "K", "I", "L" }, b.keys.lane4);
        }

        // ---- OptionDlgModal.ShortKeyName (pure) ----

        [Test]
        public void ShortKeyName_Compacts_For_Chip()
        {
            Assert.AreEqual("A", OptionDlgModal.ShortKeyName("A"));
            Assert.AreEqual("4", OptionDlgModal.ShortKeyName("Keypad4"));   // numpad shown as plain digit (matches official)
            Assert.AreEqual("5", OptionDlgModal.ShortKeyName("Alpha5"));
            Assert.AreEqual("←", OptionDlgModal.ShortKeyName("LeftArrow"));
            Assert.AreEqual("⇧L", OptionDlgModal.ShortKeyName("LeftShift"));
            Assert.AreEqual("", OptionDlgModal.ShortKeyName(null));
            Assert.AreEqual("F10", OptionDlgModal.ShortKeyName("F10"));   // unknown short name passes through
        }

        // ---- 遊戲畫面(全屏/窗口) ↔ 進階(顯示模式/視窗大小) 連動 (pure) ----

        [Test]
        public void AspectFill_Maps_To_Borderless_And_Windowed()
        {
            Assert.AreEqual(2, OptionDlgModal.AspectFillToModeIndex(true));   // 全屏 → 無邊框全螢幕(全螢幕視窗化)
            Assert.AreEqual(0, OptionDlgModal.AspectFillToModeIndex(false));  // 窗口 → 視窗
        }

        [Test]
        public void DisplayMode_Maps_To_Fill_By_Windowed_Vs_Fullscreen()
        {
            // 「主要就是看是選視窗或全螢幕」：視窗→窗口，全螢幕/無邊框全螢幕→全屏。
            Assert.IsFalse(OptionDlgModal.ModeIndexToAspectFill(0));   // 視窗 → 窗口
            Assert.IsTrue(OptionDlgModal.ModeIndexToAspectFill(1));    // 全螢幕 → 全屏
            Assert.IsTrue(OptionDlgModal.ModeIndexToAspectFill(2));    // 無邊框全螢幕 → 全屏
        }

        [Test]
        public void Fill_And_Mode_Linkage_Is_Self_Consistent()
        {
            // 由遊戲畫面設進階、再由進階讀回，不會互相打架(全屏↔全屏、窗口↔窗口)。
            Assert.IsTrue(OptionDlgModal.ModeIndexToAspectFill(OptionDlgModal.AspectFillToModeIndex(true)));
            Assert.IsFalse(OptionDlgModal.ModeIndexToAspectFill(OptionDlgModal.AspectFillToModeIndex(false)));
        }

        [Test]
        public void Default_Settings_Fill_And_Mode_Agree()
        {
            // 全新安裝預設兩頁必須一致：顯示模式(視窗) ↔ 遊戲畫面(窗口)。
            var s = new GameSettings();
            int modeIndex = System.Array.IndexOf(new[] { "Windowed", "Fullscreen", "Borderless" }, s.display.displayMode);
            Assert.GreaterOrEqual(modeIndex, 0);
            Assert.AreEqual(s.gameplay.fullscreenFill, OptionDlgModal.ModeIndexToAspectFill(modeIndex));
        }

        // ---- KeysArt.FileFor (pure) — KeyCode name -> LOBBYDLG/KEYS glyph filename ----

        [Test]
        public void KeysArt_Maps_Letters_And_Numbers_To_Glyph_Files()
        {
            Assert.AreEqual("A", KeysArt.FileFor("A"));
            Assert.AreEqual("Z", KeysArt.FileFor("Z"));
            Assert.AreEqual("4", KeysArt.FileFor("Alpha4"));   // top-row digit
            Assert.AreEqual("4", KeysArt.FileFor("Keypad4"));  // numpad -> same digit glyph
            Assert.AreEqual("8", KeysArt.FileFor("Keypad8"));
        }

        [Test]
        public void KeysArt_Maps_Named_Keys_To_Folder_Names()
        {
            Assert.AreEqual("SPACE", KeysArt.FileFor("Space"));
            Assert.AreEqual("LEFT", KeysArt.FileFor("LeftArrow"));
            Assert.AreEqual("PAGEU", KeysArt.FileFor("PageUp"));
            Assert.AreEqual("SEM", KeysArt.FileFor("Semicolon"));
            Assert.AreEqual("APO", KeysArt.FileFor("Quote"));
            Assert.AreEqual("LBRACKET", KeysArt.FileFor("LeftBracket"));
        }

        [Test]
        public void KeysArt_Returns_Null_For_Glyphless_Keys()
        {
            Assert.IsNull(KeysArt.FileFor("LeftShift"));   // official rejects these (no glyph)
            Assert.IsNull(KeysArt.FileFor("Return"));
            Assert.IsNull(KeysArt.FileFor("F10"));
            Assert.IsNull(KeysArt.FileFor(null));
            Assert.IsNull(KeysArt.FileFor(""));
        }

        [Test]
        public void KeysArt_Covers_All_Default_Bindings()
        {
            foreach (var name in new[] { "A", "S", "W", "D", "LeftArrow", "DownArrow", "UpArrow", "RightArrow" })
                Assert.IsNotNull(KeysArt.FileFor(name), "default binding must have a glyph: " + name);
        }
    }
}
