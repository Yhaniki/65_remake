// SdoImeHook.cpp
// 補足 Unity 內建 IME 缺的資訊：直接讀 Windows IMM32 目前組字的「內部游標位置」與「target(選字)反白字段」，
// 讓自製輸入框在新注音「往回選」時，游標/反白能跟著 IME 內部狀態走（Unity Input.compositionString 只給扁平字串）。
//
// 匯出 (cdecl，供 Unity P/Invoke)：
//   int  SdoImeCursorPos()                     組字內部游標(字元索引)；無組字/取不到 → -1
//   int  SdoImeTargetRange(int* outStart)      target 反白字段長度(字元)；start 由 out 帶出(-1=無)
//
// build: cl /LD /O2 /EHsc SdoImeHook.cpp /link imm32.lib user32.lib

#include <windows.h>
#include <imm.h>
#include <stdlib.h>
#pragma comment(lib, "imm32.lib")
#pragma comment(lib, "user32.lib")

static HWND ImeHwnd()
{
    HWND h = GetActiveWindow();       // 呼叫執行緒(Unity 主執行緒)目前作用中的視窗 = 遊戲視窗
    if (!h) h = GetForegroundWindow();
    return h;
}

extern "C" {

// 目前 IME 組字游標在組字串裡的字元索引。無組字或取不到 → 回 -1。
__declspec(dllexport) int SdoImeCursorPos()
{
    HWND h = ImeHwnd();
    if (!h) return -1;
    HIMC himc = ImmGetContext(h);
    if (!himc) return -1;
    // GCS_CURSORPOS：回傳值即游標位置(Unicode 版以「字元」計)。<0 視為無組字。
    LONG pos = ImmGetCompositionStringW(himc, GCS_CURSORPOS, NULL, 0);
    ImmReleaseContext(h, himc);
    return (int)pos;
}

// target(正在轉換/選字)反白字段：回傳字段長度(字元)，start 由 outStart 帶出(-1=沒有 target)。
__declspec(dllexport) int SdoImeTargetRange(int* outStart)
{
    if (outStart) *outStart = -1;
    HWND h = ImeHwnd();
    if (!h) return 0;
    HIMC himc = ImmGetContext(h);
    if (!himc) return 0;
    // GCS_COMPATTR：每個「字元」一個 byte 的屬性陣列(Unicode 版)。先取長度再取內容。
    LONG n = ImmGetCompositionStringW(himc, GCS_COMPATTR, NULL, 0);
    if (n <= 0) { ImmReleaseContext(h, himc); return 0; }
    BYTE* attr = (BYTE*)malloc((size_t)n);
    if (!attr) { ImmReleaseContext(h, himc); return 0; }
    ImmGetCompositionStringW(himc, GCS_COMPATTR, attr, (DWORD)n);
    // 找第一段連續的 target 字段(ATTR_TARGET_CONVERTED / ATTR_TARGET_NOTCONVERTED)。
    int start = -1, len = 0;
    for (int i = 0; i < (int)n; i++)
    {
        BYTE a = attr[i];
        BOOL tgt = (a == ATTR_TARGET_CONVERTED || a == ATTR_TARGET_NOTCONVERTED);
        if (tgt) { if (start < 0) start = i; len++; }
        else if (start >= 0) break;   // target 字段結束
    }
    free(attr);
    ImmReleaseContext(h, himc);
    if (outStart) *outStart = start;
    return len;
}

} // extern "C"

BOOL APIENTRY DllMain(HMODULE, DWORD, LPVOID) { return TRUE; }
