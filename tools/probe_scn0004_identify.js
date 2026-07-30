/**
 * SCN0004:自動辨識「哪個正弦相位在動哪一片水」—— 不需要肉眼判斷。
 *
 * 用法(要 admin):遊戲進海灘場景 SCN0004 後
 *      frida -n sdo.bin -l tools/probe_scn0004_identify.js
 *   或雙擊(admin) tools/run_scn0004_identify.bat
 * 輸出同時印在畫面與寫入 tools/scn0004_identify_log.txt。
 *
 * ── 要回答的問題 ────────────────────────────────────────────────────────────
 * 官方 FUN_00969c70(case 4 本體)每次呼叫做:
 *      快相位 += 0.004 → 某個 render object 的 [+0x58]=0(U)、[+0x5c] = -sin(快)*0.25(V)
 *      慢相位 += 0.001 → 另一個            的 [+0x5c]=0(V)、[+0x58] = +sin(慢)*0.5 (U)
 * 目標物件都是 FUN_0042d670(單機版 FUN_0041a100)回傳的,而反編譯顯示四次呼叫的參數都是 0 ——
 * 所以「誰是誰」在靜態上看不出來。這支直接在執行期把答案錄下來:
 *
 *   1. hook FUN_00969c70 拿到 scene 指標(ecx),讀 scene+0x2c 的 mapobj 指標陣列;
 *   2. hook FUN_0042d670,只在 FUN_00969c70 執行期間記錄它**每一次的回傳值**與呼叫序;
 *   3. 把回傳值比對 mapobj 陣列 → 得出「第 2 次呼叫(寫 V=-sin*0.25)拿到的是 mapobj[?]」;
 *   4. 對每個 mapobj 做**資源名指紋**:掃描物件本身與它持有的指標(深度 2)找 ASCII 字串,
 *      撈出 wave_ / haishuei2_ / a001 / b001 / .msh / .dds 這類名字 —— 這一步一槌定音,
 *      因為 SEA 貼 haishuei2_.dds、LANG 貼 wave_.dds、SEA_UP=b001..、SEA_DOWN=a001..。
 *   5. 順便錄下每次實際寫進 [+0x58]/[+0x5c] 的數值,確認軸向與振幅。
 *
 * 重掃描只做前幾輪(NAME_SCAN_ROUNDS),之後轉為輕量記錄,不會拖垮遊戲。
 * 全程唯讀,不寫遊戲記憶體。
 *
 * 位址:線上版 sdo.bin(ImageBase 0x400000、無 ASLR)。單機版位址也附上,attach 到哪支就用哪組。
 */

"use strict";

var LAYOUTS = {
    "sdo.bin": {
        label: "線上版 sdo.bin",
        update:  0x00969c70,   // case 4 本體
        getobj:  0x0042d670,   // 回傳 render object 的函式(Ghidra: FUN_0042d670)
        fast:    0x00ca7ad8,
        slow:    0x00ca7adc,
        // scene + 0x2c:反編譯是 `eax = [esi+0x2c]` 然後 `mov ecx,[eax+0x0c]`(objects[3])。
        // **實機實測發現這裡讀出來的陣列與 getobj 的 this 對不上**(this=0x5cb31e88/0x5cb31ec0,
        // 而 [scene+0x2c] 那組是 0x33d7xxx、U/V 全是 1.0 的另一種物件)—— 偏移或間接層數還沒對。
        // 不影響結論:**答案是靠下面 ret 的貼圖檔名指紋拿到的**(wave_.dds / haishuei2_.dds),
        // 比索引更硬。這欄留著只當輔助,對不上是預期內的。
        objsOff: 0x2c,
        sig: [0xd9, 0x05, 0xdc, 0x7a, 0xca, 0x00],
    },
    "sdo_stand_alone.exe": {
        label: "單機版 sdo_stand_alone.exe",
        update:  0x004afd30,
        getobj:  0x0041a100,
        fast:    0x006784ec,
        slow:    0x006784f0,
        objsOff: 0x2c,
        sig: null,
    },
};
var IMAGE_BASE = 0x400000;
var LOG_PATH = "H:\\65_remake\\tools\\scn0004_identify_log.txt";
var NAME_SCAN_ROUNDS = 3;     // 前幾輪做深度字串掃描
var REPORT_ROUNDS = 6;        // 總共詳細報告幾輪,之後只印摘要
var OBJ_SLOTS = 10;           // mapobj 陣列掃前幾格

var file = null;
try { file = new File(LOG_PATH, "w"); } catch (e) {}
function log(s) {
    console.log(s);
    if (file) { try { file.write(s + "\n"); file.flush(); } catch (e) {} }
}

function pick() {
    var names = Object.keys(LAYOUTS);
    for (var i = 0; i < names.length; i++) {
        try { var m = Process.findModuleByName(names[i]); if (m) return { name: names[i], base: m.base }; } catch (e) {}
    }
    return null;
}

var mod = pick();
if (!mod) {
    log("[-] 找不到 sdo.bin / sdo_stand_alone.exe(要 admin 才看得到提權程序)");
} else {
    var L = LAYOUTS[mod.name];
    var A = function (va) { return mod.base.add(va - IMAGE_BASE); };

    if (L.sig) {
        var got = new Uint8Array(A(L.update).readByteArray(L.sig.length));
        var okSig = L.sig.every(function (b, i) { return got[i] === b; });
        if (!okSig) { log("[!] 入口位元組對不上,binary 可能被 patcher 換過 —— 中止。"); throw new Error("sig"); }
    }

    log("=== SCN0004 相位↔水面 自動辨識 ===");
    log("module " + mod.name + " base=" + mod.base + "  (" + L.label + ")");
    log("update=" + A(L.update) + "  getobj=" + A(L.getobj));
    log("");

    // ── 字串指紋 ──────────────────────────────────────────────────────────
    var RX_NAME = /wave_|haishuei|sea|lang|beach|chuan|[ab]0\d\d|\.dds|\.msh|\.hrc|\.bin/i;

    function readableAscii(p, max) {
        try {
            var bytes = new Uint8Array(p.readByteArray(max));
            var s = "", run = "";
            for (var i = 0; i < bytes.length; i++) {
                var c = bytes[i];
                if (c >= 0x20 && c < 0x7f) { run += String.fromCharCode(c); }
                else { if (run.length >= 4) s += run + " | "; run = ""; }
            }
            if (run.length >= 4) s += run;
            return s;
        } catch (e) { return ""; }
    }

    function looksLikePtr(v) {
        try {
            var n = v.toUInt32 ? v.toUInt32() : parseInt(v.toString(), 16);
            return n > 0x10000 && n < 0x7ffe0000;
        } catch (e) { return false; }
    }

    // 物件本身 + 它持有的指標(深度 2)裡的字串,過濾出像資源名的
    function fingerprint(obj) {
        var hits = [];
        if (obj === null || obj.isNull()) return hits;
        var direct = readableAscii(obj, 0x100);
        direct.split(" | ").forEach(function (s) { if (RX_NAME.test(s)) hits.push("self:" + s); });
        for (var off = 0; off < 0x120; off += 4) {
            var v;
            try { v = obj.add(off).readPointer(); } catch (e) { continue; }
            if (!looksLikePtr(v)) continue;
            var s2 = readableAscii(v, 0x80);
            s2.split(" | ").forEach(function (s) {
                if (s.length >= 4 && RX_NAME.test(s)) hits.push("+0x" + off.toString(16) + "->" + s);
            });
        }
        return hits.slice(0, 8);
    }

    // ── 狀態 ─────────────────────────────────────────────────────────────
    var round = 0, inUpdate = false, callSeq = 0;
    var rets = [];          // 本輪 getobj 的回傳值
    var objs = [];          // 本輪 scene 的 mapobj 陣列
    var scenePtr = null;
    var fpCache = {};       // 指標 -> 指紋(只算一次)

    // FUN_0042d670 是個 getter:__thiscall,  return ((void**)this->[0x20])[idx]
    // Ghidra 顯示的 "(0)" 是 idx(堆疊參數),**真正區分目標的是 ecx(this)** —— 四次呼叫的 this 不同。
    // 所以這裡 onEnter 必須把 ecx 錄下來,那就是「哪一片水」的答案。
    Interceptor.attach(A(L.getobj), {
        onEnter: function (args) {
            if (!inUpdate) return;
            this._ecx = this.context.ecx;
            try { this._idx = args[0].toInt32(); } catch (e) { this._idx = -999; }
        },
        onLeave: function (ret) {
            if (!inUpdate) return;
            rets.push({ seq: callSeq++, ecx: this._ecx, idx: this._idx, ptr: ptr(ret.toString()) });
        }
    });

    Interceptor.attach(A(L.update), {
        onEnter: function () {
            inUpdate = true; callSeq = 0; rets = []; objs = [];
            scenePtr = this.context.ecx;
            try {
                var arr = scenePtr.add(L.objsOff).readPointer();
                for (var i = 0; i < OBJ_SLOTS; i++) {
                    var p = null;
                    try { p = arr.add(i * 4).readPointer(); } catch (e) {}
                    objs.push(p);
                }
            } catch (e) { objs = []; }
        },
        onLeave: function () {
            inUpdate = false;
            round++;
            if (round > REPORT_ROUNDS) return;

            log("──────── round " + round + "  scene=" + scenePtr + " ────────");
            log("  相位: fast(+0.004)=" + A(L.fast).readFloat().toFixed(4) +
                "  slow(+0.001)=" + A(L.slow).readFloat().toFixed(4));

            // mapobj 陣列 + 每格的 UV 欄位 + 指紋
            for (var i = 0; i < objs.length; i++) {
                var o = objs[i];
                if (o === null || o.isNull()) { log("  mapobj[" + i + "] = null"); continue; }
                var u = "?", v = "?", flag = "?";
                try { u = o.add(0x58).readFloat().toFixed(4); } catch (e) {}
                try { v = o.add(0x5c).readFloat().toFixed(4); } catch (e) {}
                try { flag = "0x" + (o.add(0x48).readU32() >>> 0).toString(16); } catch (e) {}
                var line = "  mapobj[" + i + "] = " + o + "   U(+0x58)=" + u + "  V(+0x5c)=" + v + "  flag(+0x48)=" + flag;
                log(line);
                if (round <= NAME_SCAN_ROUNDS) {
                    var key = o.toString();
                    if (!(key in fpCache)) fpCache[key] = fingerprint(o);
                    if (fpCache[key].length) log("        指紋: " + fpCache[key].join("   "));
                }
            }

            // 四次 getobj:this(ecx) 才是「哪一片水」,回傳值是它的 render node
            log("  getobj 呼叫序列(共 " + rets.length + " 次) —— this(ecx) 才是目標物件:");
            for (var k = 0; k < rets.length; k++) {
                var r = rets[k];
                var slotOfEcx = -1, slotOfRet = -1;
                for (var j = 0; j < objs.length; j++) {
                    if (objs[j] === null || objs[j].isNull()) continue;
                    if (objs[j].equals(r.ecx)) slotOfEcx = j;
                    if (objs[j].equals(r.ptr)) slotOfRet = j;
                }
                var role = (k === 1) ? "   ★寫 V=-sin(fast)*0.25 → 這是【快相位】的目標"
                         : (k === 3) ? "   ★寫 U=+sin(slow)*0.5  → 這是【慢相位】的目標"
                         : "";
                log("    call#" + r.seq + "  this=" + r.ecx +
                    (slotOfEcx >= 0 ? " == mapobj[" + slotOfEcx + "]" : " (不在陣列)") +
                    "  idx=" + r.idx + "  -> ret=" + r.ptr +
                    (slotOfRet >= 0 ? " == mapobj[" + slotOfRet + "]" : "") + role);
                // 資源名指紋。**看 ret 的第一個字串**(self:xxx) —— 那是這個材質自己的貼圖名,
                // 也就是答案(wave_=岸浪 / haishuei2_=海面 / b001=SEA_UP / a001=SEA_DOWN)。
                // this 的指紋與 ret 後面幾個字串多半是 heap 上鄰近物件的殘留(掃描讀 0x100 bytes
                // 會跨過物件邊界),別當證據用。
                if (round <= NAME_SCAN_ROUNDS && r.ecx && !r.ecx.isNull()) {
                    var ek = "e" + r.ecx.toString();
                    if (!(ek in fpCache)) fpCache[ek] = fingerprint(r.ecx);
                    if (fpCache[ek].length) log("            this 指紋: " + fpCache[ek].join("   "));
                    var rk = "r" + r.ptr.toString();
                    if (!(rk in fpCache)) fpCache[rk] = fingerprint(r.ptr);
                    if (fpCache[rk].length) log("            ret  指紋: " + fpCache[rk].join("   "));
                }
            }
            log("");
        }
    });

    log("[+] 已掛好。停留在海灘場景幾秒即可,前 " + REPORT_ROUNDS + " 輪會詳細記錄。");
    log("[+] log 檔:" + LOG_PATH);
    log("");
}
