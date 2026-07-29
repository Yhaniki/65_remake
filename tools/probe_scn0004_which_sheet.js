/**
 * SCN0004 決定性實驗:兩個正弦相位,到底哪一個在動「岸浪」、哪一個在動「海面」?
 *
 * 用法(要 admin):
 *   遊戲進到海灘場景 SCN0004 → 以系統管理員身分:
 *      frida -n sdo.bin -l tools/probe_scn0004_which_sheet.js
 *   或雙擊(admin) tools/run_scn0004_which_sheet.bat
 *
 * ── 原理 ────────────────────────────────────────────────────────────────────
 * 官方每次呼叫場景更新函式 FUN_00969c70 都會:
 *      快相位 += 0.004 → 寫某一片的 V = -sin(快) * 0.25
 *      慢相位 += 0.001 → 寫另一片的 U = +sin(慢) * 0.5
 * 靜態反編譯推出的對應是「快=岸浪 LANG、慢=海面 SEA」,但那是從 mapobj 陣列索引推的。
 * 這支直接做對照實驗:**在函式執行完之後把其中一個相位寫回 0**。下一次呼叫它只會變成 0.004,
 * sin(0.004)≈0 → 那一片的 UV 偏移永遠停在 0 = **看起來凍住**;另一片照常擺盪。
 * 於是「畫面上哪一片停了」就直接回答了歸屬,不需要任何推論。
 *
 * 每 6 秒自動換一種狀態,console 會同步印出現在凍的是誰,對著畫面看即可:
 *      [1] 兩片都正常  → 基準
 *      [2] 凍結快相位  → 停下來的那一片 = 快相位(0.004 / V / 振幅 0.25)的目標
 *      [3] 凍結慢相位  → 停下來的那一片 = 慢相位(0.001 / U / 振幅 0.5)的目標
 *
 * ── 看哪裡 ──────────────────────────────────────────────────────────────────
 *   「岸浪 LANG」= 打在沙灘上的那條白色浪沫帶(靠岸、窄長一條,貼 wave_.dds)
 *   「海面 SEA」 = 整片大洋的細波紋表層(範圍最大、半透明,貼 haishuei2_.dds)
 *   兩片海床(SEA_UP / SEA_DOWN,亮青色焦散)是 100ms 換幀動畫,**不受這支影響**,
 *   它們會全程繼續閃動 —— 別把它們誤認成「沒凍到」。
 *
 * ── 安全性 ──────────────────────────────────────────────────────────────────
 * 只改兩個動畫用的 float 全域變數,不改程式碼、不改結構。停掉腳本後下一次呼叫就恢復正常;
 * 離開場景或重開遊戲也一定恢復(相位是純顯示用的累加器)。
 *
 * 位址與 tools/hook_scn0004_wave.js 同一份(線上版 sdo.bin,ImageBase 0x400000、無 ASLR)。
 */

"use strict";

var LAYOUTS = {
    "sdo.bin": {
        label: "線上版 sdo.bin",
        fast: 0x00ca7ad8,   // 岸浪? 每幀 +0.004,寫 V = -sin*0.25
        slow: 0x00ca7adc,   // 海面? 每幀 +0.001,寫 U = +sin*0.5
        update: 0x00969c70,
        sig: [0xd9, 0x05, 0xdc, 0x7a, 0xca, 0x00],   // fld dword [0xca7adc]
    },
    "sdo_stand_alone.exe": {
        label: "單機版 sdo_stand_alone.exe",
        fast: 0x006784ec, slow: 0x006784f0, update: 0x004afd30, sig: null,
    },
};
var IMAGE_BASE = 0x400000;
var PHASE_MS = 6000;

function pick() {
    var names = Object.keys(LAYOUTS);
    for (var i = 0; i < names.length; i++) {
        try { var m = Process.findModuleByName(names[i]); if (m) return { name: names[i], base: m.base }; } catch (e) {}
    }
    return null;
}

var mod = pick();
if (!mod) {
    console.log("[-] 找不到 sdo.bin / sdo_stand_alone.exe。遊戲開了嗎?(要 admin 才看得到提權程序)");
} else {
    var L = LAYOUTS[mod.name];
    var A = function (va) { return mod.base.add(va - IMAGE_BASE); };
    var pFast = A(L.fast), pSlow = A(L.slow);

    // 簽章閘:確認實機這顆 binary 就是我們分析的那顆(patcher 換過就會對不上)
    if (L.sig) {
        var got = new Uint8Array(A(L.update).readByteArray(L.sig.length));
        var okSig = L.sig.every(function (b, i) { return got[i] === b; });
        if (!okSig) {
            console.log("[!] 函式入口位元組對不上 —— 這顆 sdo.bin 可能被 patcher 更新過,位址不可信,中止。");
            console.log("    期待 " + L.sig.map(function (b) { return ("0" + b.toString(16)).slice(-2); }).join(" "));
            throw new Error("signature mismatch");
        }
    }

    var MODES = [
        { name: "兩片都正常(基準)", freeze: null },
        { name: "凍結【快相位 0.004 / V / 振幅0.25】", freeze: pFast },
        { name: "凍結【慢相位 0.001 / U / 振幅0.5 】", freeze: pSlow },
    ];
    var mi = 0;

    Interceptor.attach(A(L.update), {
        onLeave: function () {
            var f = MODES[mi].freeze;
            if (f !== null) { try { f.writeFloat(0.0); } catch (e) {} }
        }
    });

    console.log("[+] " + L.label + " base=" + mod.base + "  已掛 " + A(L.update));
    console.log("[+] 每 " + (PHASE_MS / 1000) + " 秒換一種狀態。**盯著畫面看哪一片水停下來**:");
    console.log("      岸浪 LANG = 打在沙灘上那條白色浪沫帶");
    console.log("      海面 SEA  = 整片大洋的細波紋表層");
    console.log("      (亮青色的焦散海床是換幀動畫,全程都會閃 —— 不是它)\n");

    function tick() {
        var m = MODES[mi];
        console.log("[" + (mi + 1) + "/3] " + m.name +
                    "   fast=" + pFast.readFloat().toFixed(3) + " slow=" + pSlow.readFloat().toFixed(3));
        mi = (mi + 1) % MODES.length;
    }
    tick();
    setInterval(tick, PHASE_MS);
}
