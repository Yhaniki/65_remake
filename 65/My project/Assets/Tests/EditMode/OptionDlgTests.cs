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
