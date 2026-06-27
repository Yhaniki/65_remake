/**
 * SDO UV scroll speed measurement — hook StageScene_UpdateScrollLights_004aff40
 * Usage: frida -l hook_uv_scroll_speed.js -p <PID>   (or --attach-name sdo.exe)
 *
 * Measures:
 *   - Actual game-loop FPS (= how often the function is called per second)
 *   - UV units scrolled per second = fps × 0.003
 *   - Raw float at DAT_00678508 (scroll accumulator) sampled each call
 *
 * Addresses are relative to exe base (assumes PE base = 0x400000, no ASLR).
 * If the module name differs, change MODULE below.
 */

"use strict";

const MODULE      = null;   // null = scan all modules for the exe base
const FUNC_VA     = 0x004aff40;   // StageScene_UpdateScrollLights
const SCROLL_VA   = 0x00678508;   // DAT_00678508 = current V accumulator
const INCR_VA     = 0x0058902c;   // _DAT_0058902c = per-frame increment (expect 0.003)

// ── find exe base ─────────────────────────────────────────────────────────────
function findExeBase() {
    // Old Win32 games without ASLR load at 0x400000
    const guess = ptr(0x400000);
    try {
        const mbi = Process.findModuleByAddress(guess);
        if (mbi) {
            console.log("[+] Module at 0x400000: " + mbi.name + " size=" + mbi.size);
            return guess;
        }
    } catch(_) {}

    // Fallback: find first non-system module
    const mods = Process.enumerateModules();
    for (const m of mods) {
        if (!m.path.toLowerCase().includes("windows") &&
            !m.path.toLowerCase().includes("system32")) {
            console.log("[+] Fallback module: " + m.name + " @ " + m.base);
            return m.base;
        }
    }
    console.log("[-] Could not find exe base!");
    return null;
}

// ── main ──────────────────────────────────────────────────────────────────────
const base = findExeBase();
if (!base) {
    console.log("Aborting.");
} else {
    const funcAddr   = base.add(FUNC_VA   - 0x400000);
    const scrollAddr = base.add(SCROLL_VA - 0x400000);
    const incrAddr   = base.add(INCR_VA   - 0x400000);

    console.log("[+] Hooking UpdateScrollLights @ " + funcAddr);
    console.log("[+] Scroll accumulator        @ " + scrollAddr);
    console.log("[+] Per-frame increment value  = " + incrAddr.readFloat().toFixed(6));

    let callCount  = 0;
    let startMs    = Date.now();
    let prevScroll = null;
    let totalUv    = 0;

    Interceptor.attach(funcAddr, {
        onEnter: function(_args) {
            const nowMs = Date.now();
            const scroll = scrollAddr.readFloat();

            // Track signed UV delta, handle wrap-around at 1.0
            if (prevScroll !== null) {
                let delta = scroll - prevScroll;
                if (delta < -0.5) delta += 1.0;   // wrapped
                if (delta > 0.5)  delta -= 1.0;   // shouldn't happen, safety
                totalUv += Math.abs(delta);
            }
            prevScroll = scroll;
            callCount++;

            // Print summary every 120 calls (~2s at 60fps)
            if (callCount % 120 === 0) {
                const elapsedSec = (nowMs - startMs) / 1000.0;
                const fps        = callCount / elapsedSec;
                const uvPerSec   = totalUv  / elapsedSec;
                console.log(
                    "calls=" + callCount +
                    "  elapsed=" + elapsedSec.toFixed(2) + "s" +
                    "  FPS=" + fps.toFixed(1) +
                    "  UV/s=" + uvPerSec.toFixed(4) +
                    "  scroll=" + scroll.toFixed(4)
                );
            }
        }
    });

    console.log("[+] Hook active. Enter SCN0011 and start scrolling lights.");
    console.log("    Output prints every 120 game-loop calls.");
}
