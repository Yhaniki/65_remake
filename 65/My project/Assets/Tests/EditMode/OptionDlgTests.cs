using NUnit.Framework;
using UnityEngine;
using Sdo.Settings;
using Sdo.UI.Screens;

namespace Sdo.Tests
{
    /// <summary>Pure-logic cover for the OPTION dialog's rebindable keys + chip captions.
    /// (Layout/art is exercised by running the app; these lock the settings + display logic.)</summary>
    public class OptionDlgTests
    {
        // ---- KeyBindSettings.ToLaneKeys ----

        [Test]
        public void KeyBind_Defaults_Map_To_ASWD_And_Numpad()
        {
            var lanes = new KeyBindSettings().ToLaneKeys();
            Assert.AreEqual(4, lanes.Length);
            Assert.AreEqual(KeyCode.A, lanes[0][0]); Assert.AreEqual(KeyCode.Keypad4, lanes[0][1]);
            Assert.AreEqual(KeyCode.S, lanes[1][0]); Assert.AreEqual(KeyCode.Keypad5, lanes[1][1]);
            Assert.AreEqual(KeyCode.W, lanes[2][0]); Assert.AreEqual(KeyCode.Keypad8, lanes[2][1]);
            Assert.AreEqual(KeyCode.D, lanes[3][0]); Assert.AreEqual(KeyCode.Keypad6, lanes[3][1]);
        }

        [Test]
        public void KeyBind_Custom_Primary_Parsed_Aux_Defaults()
        {
            var lanes = new KeyBindSettings { lane4 = new[] { "J", "K", "I", "L" } }.ToLaneKeys();
            Assert.AreEqual(KeyCode.J, lanes[0][0]);
            Assert.AreEqual(KeyCode.L, lanes[3][0]);
            Assert.AreEqual(KeyCode.Keypad4, lanes[0][1]);   // aux untouched -> default
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
            Assert.AreEqual("Keypad4", res[0]);
            Assert.AreEqual("Keypad6", res[3]);
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
            Assert.AreEqual("Keypad4", s.keys.lane4aux[0]);
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
            Assert.AreEqual("#4", OptionDlgModal.ShortKeyName("Keypad4"));
            Assert.AreEqual("5", OptionDlgModal.ShortKeyName("Alpha5"));
            Assert.AreEqual("←", OptionDlgModal.ShortKeyName("LeftArrow"));
            Assert.AreEqual("⇧L", OptionDlgModal.ShortKeyName("LeftShift"));
            Assert.AreEqual("", OptionDlgModal.ShortKeyName(null));
            Assert.AreEqual("F10", OptionDlgModal.ShortKeyName("F10"));   // unknown short name passes through
        }
    }
}
