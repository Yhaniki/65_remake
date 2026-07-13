using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Sdo.UI.Screens;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 設定頁「綁鍵不用切輸入法」的兩塊純邏輯：
    /// KeyCode→Win32 虛擬鍵碼（RawKeyboard 走 GetAsyncKeyState 讀實體鍵，IME 組字吃不到），
    /// 以及由即時按住狀態推出的「這一幀剛按下」邊緣（KeyDownEdge，Raw 路徑沒有 GetKeyDown 可用）。
    /// </summary>
    public class RawKeyboardTests
    {
        // ---- KeyCode → virtual-key ----

        [Test]
        public void VirtualKey_Maps_Letters_Digits_And_Keypad()
        {
            Assert.AreEqual(0x41, RawKeyboard.VirtualKey(KeyCode.A));
            Assert.AreEqual(0x5A, RawKeyboard.VirtualKey(KeyCode.Z));
            Assert.AreEqual(0x30, RawKeyboard.VirtualKey(KeyCode.Alpha0));
            Assert.AreEqual(0x39, RawKeyboard.VirtualKey(KeyCode.Alpha9));
            Assert.AreEqual(0x60, RawKeyboard.VirtualKey(KeyCode.Keypad0));
            Assert.AreEqual(0x69, RawKeyboard.VirtualKey(KeyCode.Keypad9));
        }

        [Test]
        public void VirtualKey_Maps_Arrows_Punctuation_And_Escape()
        {
            Assert.AreEqual(0x25, RawKeyboard.VirtualKey(KeyCode.LeftArrow));
            Assert.AreEqual(0x28, RawKeyboard.VirtualKey(KeyCode.DownArrow));
            Assert.AreEqual(0x20, RawKeyboard.VirtualKey(KeyCode.Space));
            Assert.AreEqual(0x1B, RawKeyboard.VirtualKey(KeyCode.Escape));
            Assert.AreEqual(0xBC, RawKeyboard.VirtualKey(KeyCode.Comma));     // VK_OEM_COMMA
            Assert.AreEqual(0xBF, RawKeyboard.VirtualKey(KeyCode.Slash));     // VK_OEM_2
            Assert.AreEqual(0xDE, RawKeyboard.VirtualKey(KeyCode.Quote));     // VK_OEM_7
        }

        [Test]
        public void VirtualKey_Is_Zero_For_Unmapped_Key()
        {
            Assert.AreEqual(0, RawKeyboard.VirtualKey(KeyCode.None));
        }

        /// 綁鍵的 raw 路徑只認得有虛擬鍵碼的鍵；漏一顆 = 那顆在中文輸入法下按不到（會靜靜退回被 IME 吃掉的 Input）。
        [Test]
        public void Every_Bindable_Key_Has_A_VirtualKey()
        {
            foreach (var k in OptionDlgModal.BindableKeys)
                Assert.AreNotEqual(0, RawKeyboard.VirtualKey(k), "沒有虛擬鍵碼對應：" + k);
        }

        // ---- KeyDownEdge ----

        private static readonly KeyCode[] Scan = { KeyCode.D, KeyCode.F, KeyCode.Escape };

        private static System.Func<KeyCode, bool> Held(params KeyCode[] down)
        {
            var set = new HashSet<KeyCode>(down);
            return k => set.Contains(k);
        }

        [Test]
        public void Tick_Reports_Key_Once_On_Press_Not_While_Held()
        {
            var e = new KeyDownEdge();
            Assert.AreEqual(KeyCode.D, e.Tick(Scan, Held(KeyCode.D), true));
            Assert.IsNull(e.Tick(Scan, Held(KeyCode.D), true));   // 一直按著 → 不再報（不然會連綁四格）
            Assert.IsNull(e.Tick(Scan, Held(), true));            // 放開
            Assert.AreEqual(KeyCode.D, e.Tick(Scan, Held(KeyCode.D), true));   // 再按 → 又是一次新按下
        }

        [Test]
        public void Tick_Returns_First_Key_In_Scan_Order_When_Several_Go_Down()
        {
            var e = new KeyDownEdge();
            Assert.AreEqual(KeyCode.D, e.Tick(Scan, Held(KeyCode.F, KeyCode.D), true));
        }

        /// report=false（視窗沒焦點／沒在等按鍵）：不回報，但狀態照樣更新 ——
        /// 否則按著鍵切回遊戲時，那顆「早就按著」的鍵會被誤判成剛按下、直接綁進去。
        [Test]
        public void Tick_Without_Report_Still_Tracks_Held_So_No_Phantom_Press_Later()
        {
            var e = new KeyDownEdge();
            Assert.IsNull(e.Tick(Scan, Held(KeyCode.D), false));
            Assert.IsNull(e.Tick(Scan, Held(KeyCode.D), true));   // 還按著（不是新按下）→ 不報
            Assert.IsNull(e.Tick(Scan, Held(), true));
            Assert.AreEqual(KeyCode.D, e.Tick(Scan, Held(KeyCode.D), true));
        }

        [Test]
        public void Escape_Comes_Through_The_Same_Edge()
        {
            var e = new KeyDownEdge();
            Assert.AreEqual(KeyCode.Escape, e.Tick(Scan, Held(KeyCode.Escape), true));
        }
    }
}
