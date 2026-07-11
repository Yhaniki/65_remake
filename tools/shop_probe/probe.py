#!/usr/bin/env python3
"""
Attach Frida to the running online client (sdo.bin) and inject hook_online_avatar_files.js.
Non-interactive: it attaches, loads the script, and stays running until you press Ctrl+C.
While it runs, click around the shop in the game — the script logs which avatar files load.

Usage:  python probe.py            (attach to process name "sdo.bin")
        python probe.py <pid>      (attach to a specific PID)

Requires: pip install frida frida-tools   (Frida 17.x). Run the terminal AS ADMINISTRATOR
(sdo.bin runs with raised privileges; a non-admin attach is denied).
"""
import sys
import os
import frida

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(HERE, "hook_online_avatar_files.js")
TARGET = "sdo.bin"


def on_message(message, data):
    # The JS logs via console.log -> arrives here as {'type':'log','payload': ...}
    if message.get("type") == "log":
        print(message.get("payload"))
    elif message.get("type") == "error":
        print("[frida-error]", message.get("stack") or message.get("description"))


def main():
    target = sys.argv[1] if len(sys.argv) > 1 else TARGET
    if target.isdigit():
        target = int(target)
    try:
        session = frida.attach(target)
    except frida.ProcessNotFoundError:
        print(f"[!] Process '{target}' not found. Is the game running? (image name must be 'sdo.bin')")
        return 1
    except frida.PermissionDeniedError:
        print("[!] Permission denied. Run this terminal AS ADMINISTRATOR (sdo.bin is elevated).")
        return 1

    with open(SCRIPT, "r", encoding="utf-8") as f:
        code = f.read()
    script = session.create_script(code)
    script.on("message", on_message)
    script.load()

    print("=" * 70)
    print("Attached to", target, "— probe running.")
    print("Now in the game: open 商城, click 发型/表情/项链/下装 tabs, hover + try-on items.")
    print("Watch the lines below (also written to shop_avatar_online_log.txt).")
    print("Press Ctrl+C here to stop.")
    print("=" * 70)
    try:
        sys.stdin.read()
    except KeyboardInterrupt:
        pass
    print("\n[probe stopped]")
    return 0


if __name__ == "__main__":
    sys.exit(main())
