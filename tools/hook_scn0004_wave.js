/**
 * SCN0004(海灘)水面動畫實測 —— 官方**線上版** sdo.bin
 *
 * 用法(要 admin,sdo.bin 是提權程序):
 *   1. 開遊戲、過 launcher、進海灘場景 SCN0004(選一首歌、場景選海灘)
 *   2. 以系統管理員身分:  frida -n sdo.bin -l tools/hook_scn0004_wave.js
 *      或直接跑 tools/run_scn0004_wave.bat(右鍵 → 以系統管理員身分執行)
 *      或 python tools/measure_scn0004_wave.py
 *
 * ── 量什麼、為什麼 ────────────────────────────────────────────────────────────
 * 官方把 SCN0004 的水面動畫放在同一個場景更新函式裡,但用的是**兩套時鐘**:
 *   • 正弦擺盪:**每幀累加、沒有計時器** —— 速度完全取決於實際幀率
 *       岸浪相位 += 0.004 → 寫 U=0、V = -sin(phase)*0.25   (LANG / wave_.dds)
 *       海面相位 += 0.001 → 寫 V=0、U = +sin(phase)*0.5    (SEA  / haishuei2_.dds)
 *       兩個相位都在 > 2π 時歸零。
 *   • 換幀動畫:**100 ms 計時器**、32 幀,而且 SEA_UP(B001..B032) 與 SEA_DOWN(A001..A032)
 *       **共用同一個索引**,所以官方這兩片永遠同步。
 * remake 目前的 rad/s 是拿 SCN0011 量到的 593 fps 外推的(SCN0004 從沒實測過),於是岸浪週期 2.65 s、
 * 海面 10.6 s,而換幀一輪固定 3.2 s —— 對不上。這支就是把那個外推換成實測。
 *
 * ── 兩種量法,互相對帳 ───────────────────────────────────────────────────────
 * (a) 輪詢相位:相位每次呼叫 += 固定值,累積變化量就得到 **rad/s**(這就是 remake 要的東西)。
 *     取樣 4 ms(250 Hz),即使呼叫率高到 2000/s,單次取樣間也只變化 0.032 rad,遠不到會誤判環繞的 π。
 * (b) hook 場景更新函式,數**呼叫次數**。
 * ★ 注意:(a)÷0.004 與 (b) 量的是**同一件事** —— 場景更新函式的呼叫率。兩者一致只證明
 *   「相位確實每次呼叫 +0.004、而且我們的取樣沒漏」,**不能**證明那個數字等於畫面幀率。
 *   外層分派器 FUN_00973d40 全 binary 有 15 個呼叫點,理論上同一畫面幀可能跑不只一次
 *   (雙 viewport / 鏡面 / UI 預覽)。要驗真實幀率得另外 hook D3D9 的 Present/EndScene。
 *   所幸 remake 要的本來就是 rad/s,那是直接量到的;下面印的 fps 只是「呼叫率」的別名,
 *   用來跟舊註解裡「593 fps」那個外推值對照而已。
 *
 * ── 位址 ────────────────────────────────────────────────────────────────────
 * 線上版 sdo.bin(md5 fd5032938e42a056365444a0b2833707,PE32 ImageBase 0x400000、
 * DllCharacteristics=0 無 ASLR、無 .reloc → VA 即實機位址,與 tools/shop_probe 同慣例)。
 * 反編譯 H:\sdo_cn\sdo.bin.c:651933 起的 case 4,其本體是獨立函式:
 *     FUN_00969c70   case 4 本體(由 0x00973e0e tail-jmp 進入,全檔唯一參照點)
 *                    → **只有 SCN0004 會走到這裡**,hook 它不必再判場景 id
 *     _DAT_00ca7ad8  岸浪相位      _DAT_00b926f8  岸浪每幀增量 = 0.004(file off 0x7926f8 `6f 12 83 3b`)
 *     _DAT_00ca7adc  海面相位      _DAT_00b926fc  海面每幀增量 = 0.001(file off 0x7926fc `6f 12 83 3a`)
 *     DAT_00b926f4   換幀索引(初值 -1),100 ms timer 閘住 → **10 Hz,不能拿來量幀率**
 * 振幅常數也對上了:岸浪 0.25 @0x00b4b888、海面 0.5 @0x00b4b808。
 * 三個變數在全檔的讀寫點各只有 4 處、全在 FUN_00969c70 內,**沒有任何場景載入重置** —— 所以
 * 輪詢相位是安全的,但也代表不能靠「相位歸零」判斷剛進場景(真正歸零只在 process 啟動與 2π wrap)。
 * 對照單機版 sdo_stand_alone.exe(FUN_004afd30):0x6784ec / 0x6784f0 / 0x589020 / 0x589024 / 0x58901c。
 */

"use strict";

var MODULE      = "sdo.bin";
var IMAGE_BASE  = 0x400000;

// ── 線上版(sdo.bin)與單機版(sdo_stand_alone.exe)兩組位址,依 attach 到哪支自動選 ──
var LAYOUTS = {
    "sdo.bin": {
        label: "線上版 sdo.bin",
        langPhase: 0x00ca7ad8, seaPhase: 0x00ca7adc,
        langInc:   0x00b926f8, seaInc:   0x00b926fc,
        frameIdx:  0x00b926f4,
        update:    0x00969c70,   // FUN_00969c70 = case 4 本體(只有 SCN0004 走到)
    },
    "sdo_stand_alone.exe": {
        label: "單機版 sdo_stand_alone.exe",
        langPhase: 0x006784ec, seaPhase: 0x006784f0,
        langInc:   0x00589020, seaInc:   0x00589024,
        frameIdx:  0x0058901c,
        update:    0x004afd30,   // FUN_004afd30
    },
};

var TWO_PI = 6.283185307179586;
var SAMPLE_MS = 4;        // 250 Hz
var REPORT_MS = 2000;

function pickModule() {
    var names = Object.keys(LAYOUTS);
    for (var i = 0; i < names.length; i++) {
        try {
            var m = Process.findModuleByName(names[i]);
            if (m) return { name: names[i], base: m.base };
        } catch (e) {}
    }
    // 名字對不上就退回主模組(有些 loader 會改 image name)
    try {
        var mods = Process.enumerateModules();
        if (mods.length > 0) return { name: mods[0].name, base: mods[0].base };
    } catch (e) {}
    return null;
}

var mod = pickModule();
if (!mod) {
    console.log("[-] 找不到模組,放棄。");
} else {
    var L = LAYOUTS[mod.name] || LAYOUTS["sdo.bin"];
    var A = function (va) { return mod.base.add(va - IMAGE_BASE); };
    var pLang = A(L.langPhase), pSea = A(L.seaPhase), pIdx = A(L.frameIdx);

    var incLang = A(L.langInc).readFloat();
    var incSea  = A(L.seaInc).readFloat();

    console.log("[+] " + (LAYOUTS[mod.name] ? L.label : mod.name + "(未知版本,用線上版位址試)") +
                "  base=" + mod.base);
    console.log("[+] 每幀增量:岸浪=" + incLang.toFixed(6) + "  海面=" + incSea.toFixed(6) +
                (Math.abs(incLang - 0.004) < 1e-6 && Math.abs(incSea - 0.001) < 1e-6
                    ? "   ✓ 與反編譯一致" : "   ✗ 與預期(0.004/0.001)不符 —— 位址可能不對,先別信下面的數據"));
    console.log("[+] 取樣 " + (1000 / SAMPLE_MS) + " Hz。進到海灘場景(SCN0004)才會有數據,每 2 秒印一次。\n");

    var prevL = null, prevS = null, sumL = 0, sumS = 0;
    var t0 = null, samples = 0;
    var prevIdx = null, idxChanges = 0, idxFirst = null, idxLast = null;
    var lastReport = 0, idleTicks = 0;
    // 累積平均會被「開場低幀率 / 中途卡頓」永久拖住,單看它會低估穩定後的速度。
    // 所以每個回報區間另外算一次**瞬時**值,並收集起來取中位數 —— 中位數對卡頓天生免疫。
    var markT = null, markL = 0, markS = 0, markCalls = 0;
    var instRates = [];

    // (b) 直接數場景更新函式的呼叫次數。這支只有 SCN0004 走得到(全檔唯一參照是 tail-jmp),
    // 所以不必判場景 id。入口第一條是 6-byte 的 `fld dword [phase]`,放得下 5-byte trampoline。
    var hookCalls = 0, hookT0 = null;
    if (L.update) {
        try {
            Interceptor.attach(A(L.update), {
                onEnter: function () { if (hookT0 === null) hookT0 = Date.now(); hookCalls++; }
            });
            console.log("[+] 已掛場景更新函式 @ " + A(L.update) + "(用來跟相位推算的 fps 對帳)");
        } catch (e) {
            console.log("[!] 掛不上場景更新函式(" + e + ") —— 只用輪詢,rad/s 仍然準確");
        }
    }

    // 相位「累加到 > 2π 才歸零」:delta 為負就是繞了一圈,補回 2π。
    // 場景切換會把相位歸零/亂跳,用 0.5 rad 當門檻剔掉(單次取樣不可能推進這麼多)。
    function advance(cur, prev) {
        var d = cur - prev;
        if (d < 0) d += TWO_PI;
        return (d >= 0 && d < 0.5) ? d : 0;
    }

    setInterval(function () {
        var now = Date.now();
        var l, s, ix;
        try { l = pLang.readFloat(); s = pSea.readFloat(); ix = pIdx.readU32(); }
        catch (e) { return; }

        if (prevL !== null) {
            var dl = advance(l, prevL), ds = advance(s, prevS);
            if (dl > 0 || ds > 0) {
                if (t0 === null) t0 = now;      // 相位真的開始動了才起算(= 進到 SCN0004)
                sumL += dl; sumS += ds; samples++;
                idleTicks = 0;
            } else {
                idleTicks++;
            }
        }
        prevL = l; prevS = s;

        if (prevIdx !== null && ix !== prevIdx && ix < 64) {
            if (idxFirst === null) idxFirst = now; else idxChanges++;
            idxLast = now;
        }
        prevIdx = ix;

        if (t0 === null || now - lastReport < REPORT_MS) return;
        lastReport = now;

        var sec = (now - t0) / 1000;
        if (sec < 1) return;
        var frameMs = idxChanges > 0 ? (idxLast - idxFirst) / idxChanges : 0;

        // 本區間的瞬時值(這才是「現在的官方速度」)
        if (markT === null) { markT = t0; }
        var win = (now - markT) / 1000;
        var iLang = win > 0 ? (sumL - markL) / win : 0;
        var iSea  = win > 0 ? (sumS - markS) / win : 0;
        var iFps  = incLang > 0 ? iLang / incLang : 0;
        var iHookFps = win > 0 ? (hookCalls - markCalls) / win : 0;
        markT = now; markL = sumL; markS = sumS; markCalls = hookCalls;

        // 卡頓段(幀率崩到平常的一半以下)不進中位數 —— 它反映的是量測當下的干擾,不是官方速度
        if (iFps > 50) instRates.push(iFps);
        var sorted = instRates.slice().sort(function (a, b) { return a - b; });
        var med = sorted.length ? sorted[sorted.length >> 1] : 0;
        var agree = iHookFps > 0 ? Math.abs(iHookFps - iFps) / Math.max(iHookFps, iFps) : 1;

        console.log(
            "t=" + sec.toFixed(1) + "s" +
            // 「呼叫率」不是「畫面幀率」—— 見檔頭。兩個數字一致只代表取樣沒漏,不代表等於 FPS。
            "  瞬時呼叫率=" + iFps.toFixed(0) + "/s" + (iHookFps > 0 ? "|hook " + iHookFps.toFixed(0) + (agree < 0.05 ? "✓" : "✗") : "") +
            "  (中位數 " + med.toFixed(0) + ", 累積均 " + (sumL / sec / incLang).toFixed(0) + ")" +
            "  岸浪=" + iLang.toFixed(3) + " rad/s (週期 " + (iLang > 0 ? (TWO_PI / iLang).toFixed(2) : "?") + "s)" +
            "  海面=" + iSea.toFixed(3) + " rad/s (週期 " + (iSea > 0 ? (TWO_PI / iSea).toFixed(2) : "?") + "s)" +
            "  換幀=" + frameMs.toFixed(1) + "ms (一輪 " + (frameMs * 32 / 1000).toFixed(2) + "s)" +
            (idleTicks > 250 ? "   [相位停了 —— 離開海灘場景了?]" : ""));
        // 建議值一律用**中位數**,不用累積平均(累積平均被開場/卡頓永久拖低)
        console.log("    → 寫回 SceneMapobjUvScrollCatalog(SCN0004):" +
                    " SEA " + (incSea * med).toFixed(3) + " rad/s、LANG " + (incLang * med).toFixed(3) +
                    " rad/s   (n=" + instRates.length + " 個區間的中位數)");
    }, SAMPLE_MS);
}
