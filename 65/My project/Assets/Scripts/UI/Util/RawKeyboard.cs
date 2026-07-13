using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 讀「實體按鍵」當下狀態（Win32 GetAsyncKeyState），完全不經過視窗訊息迴圈。
    ///
    /// 為什麼要這條路：中文輸入法（新注音/拼音…）在組字態會把 WM_KEYDOWN 吃掉換成 IME 訊息，Unity 的
    /// Input.GetKeyDown 就永遠收不到那個鍵 —— 房間為了聊天把 Input.imeCompositionMode 開成 On（見
    /// RoomScreen.OnShow），疊在房間上的設定視窗綁鍵因此得先切成英數輸入法才按得動。GetAsyncKeyState 讀的是
    /// 鍵盤實體狀態，IME 攔不到，任何輸入法下都拿得到 D/F/J/K 這種鍵。
    ///
    /// 只給「即時是否按著」；「這一幀剛按下」的邊緣偵測交給 <see cref="KeyDownEdge"/>。
    /// 非 Windows / 找不到 user32 → Supported=false，呼叫端退回 Unity Input。
    /// </summary>
    public static class RawKeyboard
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static bool _broken;   // 呼叫過但沒有 user32 → 之後不再嘗試（避免每幀丟例外）

        /// <summary>這台機器能不能走 raw 讀取（Windows 且 user32 可用）。</summary>
        public static bool Supported
        {
            get
            {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                return !_broken;
#else
                return false;
#endif
            }
        }

        /// <summary>該鍵此刻是否按著（實體狀態，不受輸入法影響）。不支援 / 沒對應虛擬鍵碼 → false。</summary>
        public static bool IsHeld(KeyCode key)
        {
            if (!Supported) return false;
            int vk = VirtualKey(key);
            if (vk == 0) return false;
            try { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
            catch (DllNotFoundException) { _broken = true; return false; }
            catch (EntryPointNotFoundException) { _broken = true; return false; }
        }

        /// <summary>
        /// Unity KeyCode → Win32 virtual-key code（0 = 沒對應，呼叫端就退回 Unity Input）。純函式。
        /// 涵蓋設定頁可綁的所有鍵（字母/數字/小鍵盤/方向/標點/編輯鍵）加 Esc。
        /// </summary>
        public static int VirtualKey(KeyCode key)
        {
            if (key >= KeyCode.A && key <= KeyCode.Z) return 0x41 + (key - KeyCode.A);                     // VK_A..VK_Z
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) return 0x30 + (key - KeyCode.Alpha0);      // VK_0..VK_9
            if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9) return 0x60 + (key - KeyCode.Keypad0);   // VK_NUMPAD0..9
            if (key >= KeyCode.F1 && key <= KeyCode.F12) return 0x70 + (key - KeyCode.F1);                 // VK_F1..VK_F12

            switch (key)
            {
                case KeyCode.LeftArrow: return 0x25;
                case KeyCode.UpArrow: return 0x26;
                case KeyCode.RightArrow: return 0x27;
                case KeyCode.DownArrow: return 0x28;
                case KeyCode.Space: return 0x20;
                case KeyCode.Escape: return 0x1B;
                case KeyCode.Return: return 0x0D;
                case KeyCode.Tab: return 0x09;
                case KeyCode.Backspace: return 0x08;
                case KeyCode.Home: return 0x24;
                case KeyCode.End: return 0x23;
                case KeyCode.PageUp: return 0x21;
                case KeyCode.PageDown: return 0x22;
                case KeyCode.Insert: return 0x2D;
                case KeyCode.Delete: return 0x2E;
                // OEM 鍵：位置固定在 US 版面上（IME 也管不到實體鍵位）
                case KeyCode.Semicolon: return 0xBA;      // VK_OEM_1  ;:
                case KeyCode.Equals: return 0xBB;         // VK_OEM_PLUS  =+
                case KeyCode.Comma: return 0xBC;          // VK_OEM_COMMA  ,<
                case KeyCode.Minus: return 0xBD;          // VK_OEM_MINUS  -_
                case KeyCode.Period: return 0xBE;         // VK_OEM_PERIOD  .>
                case KeyCode.Slash: return 0xBF;          // VK_OEM_2  /?
                case KeyCode.BackQuote: return 0xC0;      // VK_OEM_3  `~
                case KeyCode.LeftBracket: return 0xDB;    // VK_OEM_4  [{
                case KeyCode.Backslash: return 0xDC;      // VK_OEM_5  \|
                case KeyCode.RightBracket: return 0xDD;   // VK_OEM_6  ]}
                case KeyCode.Quote: return 0xDE;          // VK_OEM_7  '"
                case KeyCode.KeypadDivide: return 0x6F;
                case KeyCode.KeypadMultiply: return 0x6A;
                case KeyCode.KeypadMinus: return 0x6D;
                case KeyCode.KeypadPlus: return 0x6B;
                case KeyCode.KeypadPeriod: return 0x6E;
                case KeyCode.KeypadEnter: return 0x0D;    // VK_RETURN（extended，GetAsyncKeyState 不分左右）
                default: return 0;
            }
        }
    }

    /// <summary>
    /// 從「這一刻按著哪些鍵」推出「這一幀剛按下哪個鍵」。<see cref="RawKeyboard"/> 只給即時狀態（沒有 GetKeyDown），
    /// 由呼叫端逐幀 Tick 出邊緣。held 由委派注入 → 純邏輯，可單元測試。
    /// </summary>
    public sealed class KeyDownEdge
    {
        private readonly HashSet<KeyCode> _held = new HashSet<KeyCode>();

        /// <summary>忘掉上一幀狀態（下一次 Tick 時「已經按著」的鍵不會被當成剛按下 —— 它們會先進 _held 才報）。</summary>
        public void Clear() => _held.Clear();

        /// <summary>
        /// 掃 <paramref name="keys"/>，更新按住狀態，回傳這一幀「新按下」的第一個鍵（依 keys 順序；沒有 → null）。
        /// <paramref name="report"/>=false（視窗沒焦點 / 沒在等按鍵）時照樣更新狀態但一律回 null ——
        /// 狀態要繼續更新，否則切回來時那顆還按著的鍵會被誤判成「剛按下」。
        /// </summary>
        public KeyCode? Tick(IReadOnlyList<KeyCode> keys, Func<KeyCode, bool> held, bool report)
        {
            if (keys == null || held == null) return null;
            KeyCode? first = null;
            for (int i = 0; i < keys.Count; i++)
            {
                var k = keys[i];
                if (held(k))
                {
                    if (_held.Add(k) && report && first == null) first = k;   // Add=true → 上一幀還沒按著 = 這一幀剛按下
                }
                else _held.Remove(k);
            }
            return first;
        }
    }
}
