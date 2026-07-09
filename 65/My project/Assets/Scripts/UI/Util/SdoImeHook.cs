using System;
using System.Runtime.InteropServices;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 讀 Windows IMM32 目前 IME 組字狀態，補足 Unity 內建 IME 缺的「內部游標位置 / target(選字)反白字段」。
    /// Unity 的 Input.compositionString 只給扁平字串；新注音「往回選」要靠這個才拿得到游標移到哪。
    /// 由原生 SdoImeHook.dll（Assets/Plugins/x86_64）提供；DLL 不在（非 Windows / 未 build）時安全退化成「無資訊」。
    /// </summary>
    public static class SdoImeHook
    {
        const string Dll = "SdoImeHook";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int SdoImeCursorPos();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int SdoImeTargetRange(out int start);

        static bool _broken;   // 呼叫過但找不到 DLL/入口 → 之後不再嘗試（避免每幀丟例外）

        static bool Usable
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

        /// <summary>目前 IME 組字內部游標（字元索引）。無組字 / 取不到 / 不支援 → -1。</summary>
        public static int CursorPos()
        {
            if (!Usable) return -1;
            try { return SdoImeCursorPos(); }
            catch (DllNotFoundException) { _broken = true; return -1; }
            catch (EntryPointNotFoundException) { _broken = true; return -1; }
        }

        /// <summary>target(正在轉換/選字)反白字段：回傳長度(字元)，start out 帶出起點(-1=無)。不支援 → 0。</summary>
        public static int TargetRange(out int start)
        {
            start = -1;
            if (!Usable) return 0;
            try { return SdoImeTargetRange(out start); }
            catch (DllNotFoundException) { _broken = true; return 0; }
            catch (EntryPointNotFoundException) { _broken = true; return 0; }
        }
    }
}
