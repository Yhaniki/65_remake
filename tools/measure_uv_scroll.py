"""
SDO UV scroll speed measurement — runtime float scan, version-agnostic.

Works on any SDO build (online or offline) without knowing function addresses.
Finds the scroll accumulator by scanning writable memory for a float that
cycles 0→1 repeatedly, then measures UV units per second.

Usage:
  1. Run SDO, enter SCN0011 (舞林大會)
  2. python measure_uv_scroll.py
"""

import frida, sys, time

# Phase 1: snapshot writable floats in [0,1), wait 200ms, snapshot again,
#          keep candidates that rose by 0.001..0.6 (consistent with UV scroll).
# Phase 2: monitor survivors over 3s, measure UV/s.

FRIDA_JS = r"""
"use strict";

function snapFloats() {
    const snap = new Map();  // addr_str -> float
    Process.enumerateRanges({protection:'rw-', coalesce:true}).forEach(function(r) {
        // Skip very large regions (stack, heap bulk) — BSS/data are small
        if (r.size > 4 * 1024 * 1024) return;
        if (r.size < 4) return;
        const base = r.base;
        const n = Math.floor(r.size / 4);
        for (let i = 0; i < n; i++) {
            try {
                const p = base.add(i * 4);
                const f = p.readFloat();
                if (f >= 0.0 && f < 1.0) {
                    snap.set(p.toString(), f);
                }
            } catch(_) {}
        }
    });
    return snap;
}

send({type:'status', msg:'Phase 1: scanning memory...'});

const snap1 = snapFloats();
send({type:'status', msg:'Snapshot 1 done (' + snap1.size + ' candidates). Waiting 300ms...'});

setTimeout(function() {
    const snap2 = snapFloats();
    send({type:'status', msg:'Snapshot 2 done. Filtering...'});

    // Keep addresses where value rose by 0.001..0.5
    const survivors = [];
    snap1.forEach(function(v1, addrStr) {
        const v2 = snap2.get(addrStr);
        if (v2 === undefined) return;
        let d = v2 - v1;
        if (d < -0.5) d += 1.0;   // wrap case
        if (d >= 0.001 && d <= 0.5) {
            survivors.push({addr: ptr(addrStr), v1: v1, v2: v2, d: d});
        }
    });
    send({type:'status', msg:'Survivors: ' + survivors.length + '. Monitoring 5s...'});

    if (survivors.length === 0) {
        send({type:'error', msg:'No candidates found. Are you in SCN0011?'});
        return;
    }

    // Phase 2: monitor survivors for 5 seconds
    const t0 = Date.now();
    const trackers = survivors.map(function(s) {
        return {addr: s.addr, prev: s.v2, total: 0, calls: 0, lastPrint: 0};
    });

    const interval = setInterval(function() {
        const nowMs = Date.now();
        const elapsed = (nowMs - t0) / 1000.0;

        trackers.forEach(function(t) {
            try {
                const f = t.addr.readFloat();
                let d = f - t.prev;
                if (d < -0.5) d += 1.0;
                if (d > 0.5)  d -= 1.0;
                if (d > 0.0001) {
                    t.total += d;
                    t.calls++;
                }
                t.prev = f;
            } catch(_) {}
        });

        if (elapsed >= 5.0) {
            clearInterval(interval);
            // Report all survivors with meaningful movement
            const results = trackers
                .filter(function(t) { return t.calls > 10; })
                .map(function(t) {
                    return {addr: t.addr.toString(),
                            uvs: Math.round(t.total / (elapsed) * 1000) / 1000,
                            calls: t.calls};
                })
                .sort(function(a,b) { return b.calls - a.calls; });
            send({type:'results', data: results, elapsed: elapsed});
        }
    }, 16);   // ~60fps polling

}, 300);
"""

TARGET = "sdodx.exe"

def on_message(msg, _data):
    if msg.get("type") == "send":
        p = msg["payload"]
        t = p.get("type","")
        if t == "status":
            print("[*]", p["msg"])
        elif t == "error":
            print("[!]", p["msg"])
        elif t == "results":
            data = p["data"]
            print(f"\n=== Results (over {p['elapsed']:.1f}s) ===")
            if not data:
                print("  No stable candidates found.")
            else:
                print(f"  {'Address':<14}  {'UV/s':>8}  {'samples':>8}")
                print("  " + "-"*36)
                for r in data[:10]:
                    print(f"  {r['addr']:<14}  {r['uvs']:>8.4f}  {r['calls']:>8}")
                print()
                best = data[0]["uvs"] if data else 0
                print(f">>> Best estimate: {best:.4f} UV/s")
                print(f"    → use this value in SceneMapobjUvScrollCatalog.cs")
    elif msg.get("type") == "error":
        print(f"[JS] {msg.get('description','')}")

print(f"Attaching to {TARGET} ...")
try:
    session = frida.attach(TARGET)
except frida.ProcessNotFoundError:
    print(f"Process '{TARGET}' not found.")
    print("Run sdodx.exe first and enter SCN0011 (舞林大會).")
    sys.exit(1)

script = session.create_script(FRIDA_JS)
script.on("message", on_message)
script.load()

print("Scanning... (takes ~6 seconds)\n")
try:
    time.sleep(10)
except KeyboardInterrupt:
    pass
session.detach()
print("Done.")
