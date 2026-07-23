using System;
using NUnit.Framework;
using UnityEngine;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// keymaps.ini（4 鍵打擊鍵位 + 遊玩功能鍵）的純函式來回：ParseInto / Sanitize / Serialize / Bind /
    /// CaptureFrom / ApplyTo。功能鍵是使用者要求「F2、F5 加速、F6 減速那些 key 都能自訂位置」的落地機制。不碰檔案。
    /// </summary>
    public class KeyMapTests
    {
        // KeyMap 是靜態狀態 → 每個 case 前還原成內建預設。
        [SetUp]
        public void Reset()
        {
            KeyMap.ParseInto("[Lane4]\nprimary=A,S,W,D\naux=LeftArrow,DownArrow,UpArrow,RightArrow\n");
            for (int i = 0; i < KeyMap.Count; i++) KeyMap.Bind((Hotkey)i, KeyMap.HotkeyDefaults[i]);
            KeyMap.Sanitize();
        }

        [Test]
        public void Hotkey_Tables_Are_Aligned_With_Enum()
        {
            int n = Enum.GetValues(typeof(Hotkey)).Length;
            Assert.AreEqual(n, KeyMap.HotkeyIds.Length, "每個 Hotkey 都要有 ini 的 key 名");
            Assert.AreEqual(n, KeyMap.HotkeyDefaults.Length, "每個 Hotkey 都要有預設鍵");
            Assert.AreEqual(n, KeyMap.Count);
            CollectionAssert.AllItemsAreUnique(KeyMap.HotkeyIds, "ini 的 key 名不能重複，否則解析會互相覆蓋");
        }

        [Test]
        public void Defaults_Match_The_Previously_Hardcoded_Keys()
        {
            // 重製前寫死在 ScreenGameplay/FrontendApp 的那幾顆 —— 換成可自訂之後預設值必須一模一樣。
            Assert.AreEqual(KeyCode.F2, KeyMap.Key(Hotkey.Camera));
            Assert.AreEqual(KeyCode.F5, KeyMap.Key(Hotkey.SpeedUp));
            Assert.AreEqual(KeyCode.F6, KeyMap.Key(Hotkey.SpeedDown));
            Assert.AreEqual(KeyCode.F7, KeyMap.Key(Hotkey.AssistTick));
            Assert.AreEqual(KeyCode.F8, KeyMap.Key(Hotkey.AutoPlay));
            Assert.AreEqual(KeyCode.Space, KeyMap.Key(Hotkey.Showtime));
            Assert.AreEqual(KeyCode.Escape, KeyMap.Key(Hotkey.Quit));
        }

        [Test]
        public void Hotkeys_RoundTrip_Through_Ini_Text()
        {
            KeyMap.Bind(Hotkey.SpeedUp, KeyCode.PageUp);
            KeyMap.Bind(Hotkey.SpeedDown, KeyCode.PageDown);
            KeyMap.Bind(Hotkey.Camera, KeyCode.C);
            string ini = KeyMap.Serialize();

            Reset();   // wipe back to defaults
            KeyMap.ParseInto(ini);
            KeyMap.Sanitize();
            Assert.AreEqual(KeyCode.PageUp, KeyMap.Key(Hotkey.SpeedUp));
            Assert.AreEqual(KeyCode.PageDown, KeyMap.Key(Hotkey.SpeedDown));
            Assert.AreEqual(KeyCode.C, KeyMap.Key(Hotkey.Camera));
            Assert.AreEqual(KeyCode.F7, KeyMap.Key(Hotkey.AssistTick), "沒改的功能鍵要留在預設");
        }

        [Test]
        public void Lane4_RoundTrips_Through_Ini_Text()
        {
            KeyMap.lane4 = new[] { "J", "K", "I", "L" };
            KeyMap.lane4aux = new[] { "Keypad4", "Keypad2", "Keypad8", "Keypad6" };
            KeyMap.Sanitize();
            string ini = KeyMap.Serialize();

            Reset();
            KeyMap.ParseInto(ini);
            KeyMap.Sanitize();
            CollectionAssert.AreEqual(new[] { "J", "K", "I", "L" }, KeyMap.lane4);
            CollectionAssert.AreEqual(new[] { "Keypad4", "Keypad2", "Keypad8", "Keypad6" }, KeyMap.lane4aux);
        }

        [Test]
        public void Empty_Value_Disables_The_Hotkey()
        {
            KeyMap.ParseInto("[Hotkeys]\nautoPlay=\n");
            KeyMap.Sanitize();
            Assert.AreEqual(KeyCode.None, KeyMap.Key(Hotkey.AutoPlay), "留空＝該功能不綁鍵，不該回退成預設");
            Assert.IsFalse(KeyMap.Down(Hotkey.AutoPlay), "沒綁鍵的功能永遠不會觸發");
        }

        [Test]
        public void Invalid_Key_Name_Falls_Back_To_Default()
        {
            KeyMap.ParseInto("[Hotkeys]\nspeedUp=NotAKey\n");
            KeyMap.Sanitize();
            Assert.AreEqual(KeyCode.F5, KeyMap.Key(Hotkey.SpeedUp), "打錯的鍵名 → 回該功能預設鍵（不是變成不綁）");
        }

        [Test]
        public void ParseInto_Ignores_Comments_Sections_And_Unknown_Keys()
        {
            KeyMap.ParseInto("# 註解\n; 註解\n[Hotkeys]\nbogus=Q\nspeedDown=Minus\n");
            KeyMap.Sanitize();
            Assert.AreEqual(KeyCode.Minus, KeyMap.Key(Hotkey.SpeedDown));
            Assert.AreEqual(KeyCode.F5, KeyMap.Key(Hotkey.SpeedUp), "沒提到的 key 保留原值");
        }

        [Test]
        public void Hotkey_Ids_Are_Case_Insensitive()
        {
            KeyMap.ParseInto("[Hotkeys]\nSPEEDUP=Equals\n");
            KeyMap.Sanitize();
            Assert.AreEqual(KeyCode.Equals, KeyMap.Key(Hotkey.SpeedUp), "手改 ini 大小寫不該影響");
        }

        [Test]
        public void Bind_None_Clears_The_Binding()
        {
            KeyMap.Bind(Hotkey.Quit, KeyCode.None);
            Assert.AreEqual(KeyCode.None, KeyMap.Key(Hotkey.Quit));
            StringAssert.Contains("quit=\n", KeyMap.Serialize(), "不綁鍵要寫成空值");
        }

        [Test]
        public void ClearedLaneSlot_Is_Preserved_As_Empty()
        {
            KeyMap.lane4 = new[] { "A", "", "W", "D" };   // 第二格刻意清空（OPTION 鍵盤頁去重會做這件事）
            KeyMap.Sanitize();
            Assert.AreEqual("", KeyMap.lane4[1], "清空的鍵格不應被還原成預設鍵");
        }

        [Test]
        public void CaptureFrom_And_ApplyTo_RoundTrip_Lane_Keys()
        {
            var src = new KeyBindSettings { lane4 = new[] { "J", "K", "I", "L" }, lane4aux = new[] { "A", "", "W", "D" } };
            KeyMap.CaptureFrom(src);

            var dst = new KeyBindSettings();
            KeyMap.ApplyTo(dst);
            CollectionAssert.AreEqual(new[] { "J", "K", "I", "L" }, dst.lane4);
            CollectionAssert.AreEqual(new[] { "A", "", "W", "D" }, dst.lane4aux);
        }

        [Test]
        public void Conflicts_Report_Keys_Bound_To_Both_Lane_And_Hotkey()
        {
            Assert.IsEmpty(KeyMap.LaneHotkeyConflicts(), "預設 A/S/W/D + 方向鍵 vs F2/F5/F6/F7/F8/Space/Esc 不該撞");

            KeyMap.Bind(Hotkey.SpeedUp, KeyCode.S);   // 撞到「下」的主鍵位
            CollectionAssert.Contains(KeyMap.LaneHotkeyConflicts(), KeyCode.S);
        }

        [Test]
        public void Serialize_Emits_Every_Hotkey_Id()
        {
            string ini = KeyMap.Serialize();
            foreach (var id in KeyMap.HotkeyIds)
                StringAssert.Contains(id + "=", ini, $"{id} 沒被寫進 keymaps.ini，玩家就改不到");
        }
    }
}
