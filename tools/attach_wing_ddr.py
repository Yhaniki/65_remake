#!/usr/bin/env python3
"""Attach Frida to the running DDROnline_D.exe and load hook_wing_eft_ddr.js.
Run this AFTER the game is up and the avatar is on screen. It streams the hook's
console output and (the hook also writes) H:\\65_remake\\wing_eft_ddr_log.txt.

Usage:  python tools/attach_wing_ddr.py [seconds]   (default: run until Ctrl-C)
"""
import sys, time, frida

# any SDO game CLIENT (not the NXPatch launcher). DDROnline_D is the debug build with the item.
CLIENTS = ("ddronline_d.exe", "ddronline.exe", "sdo_fanti.exe", "sdo_jianti.exe", "sdo_english.exe")
HOOK = r"H:\65_remake\assets\閉撰敃氪\hook_wing_eft_ddr.js"

def on_message(msg, data):
    if msg.get("type") == "send":
        print(msg["payload"], flush=True)
    elif msg.get("type") == "error":
        print("[frida-error]", msg.get("stack") or msg.get("description"), flush=True)

def main():
    dev = frida.get_local_device()
    procs = [p for p in dev.enumerate_processes() if p.name.lower() in CLIENTS]
    if not procs:
        running = ", ".join(sorted({p.name for p in dev.enumerate_processes()
                                    if any(k in p.name.lower() for k in ("sdo", "ddr", "nxpatch", "dance"))})) or "none"
        print(f"[attach] no SDO game CLIENT running (saw: {running}).")
        print("[attach] NXPatch.exe is only the patcher — let it launch the game (or run DDROnline_D.exe),")
        print("[attach] log in, get your avatar on screen, then re-run this.")
        return 1
    # prefer the debug build (has the item + our EFT-table VA), else whatever client is up
    procs.sort(key=lambda p: 0 if p.name.lower() == "ddronline_d.exe" else 1)
    pid = procs[0].pid
    print(f"[attach] target client: {procs[0].name}", flush=True)
    print(f"[attach] attaching to {procs[0].name} pid={pid} ...", flush=True)
    session = dev.attach(pid)
    with open(HOOK, "r", encoding="utf-8") as f:
        src = f.read()
    script = session.create_script(src)
    script.on("message", on_message)
    script.load()
    print("[attach] hook loaded. Now EQUIP the Crystal White Wing in-game and watch for a (NEW) EFT-OPEN line.", flush=True)
    dur = float(sys.argv[1]) if len(sys.argv) > 1 else None
    try:
        if dur:
            time.sleep(dur)
        else:
            while True:
                time.sleep(1)
    except KeyboardInterrupt:
        pass
    finally:
        try: session.detach()
        except Exception: pass
    return 0

if __name__ == "__main__":
    sys.exit(main())
