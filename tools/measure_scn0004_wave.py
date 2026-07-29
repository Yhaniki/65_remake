#!/usr/bin/env python3
"""
SCN0004(海灘)水面動畫實測 driver —— 官方**線上版** sdo.bin(單機版 sdo_stand_alone.exe 也吃)。

用法(要 admin,sdo.bin 是提權程序,非 admin 會被拒絕 attach):
  1. 開遊戲、過 launcher、進海灘場景 SCN0004
  2. 以系統管理員身分開終端機: python tools/measure_scn0004_wave.py [量測秒數,預設 20]

量什麼 / 為什麼:見 tools/hook_scn0004_wave.js 檔頭。摘要:官方正弦擺盪每幀累加(綁幀率)、
換幀動畫走 100 ms 計時器,兩套時鐘。remake 的 rad/s 目前是從 SCN0011 的 593 fps 外推,
這支把它換成 SCN0004 的實測值。
"""
import sys, time, os

# console 若不是 UTF-8(cmd 預設 cp950/cp437),中文輸出會炸 UnicodeEncodeError。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

try:
    import frida
except ImportError:
    print("need frida:  pip install frida frida-tools")
    sys.exit(1)

# 線上版優先;找不到才退單機版。名稱同時也是 hook 腳本挑位址表的依據。
TARGETS = ["sdo.bin", "sdo_stand_alone.exe"]
DURATION = float(sys.argv[1]) if len(sys.argv) > 1 else 20.0
JS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "hook_scn0004_wave.js")

lines = []


def on_message(msg, _data):
    if msg.get("type") == "error":
        print("[JS]", msg.get("description", ""))


def attach():
    last = None
    for name in TARGETS:
        try:
            s = frida.attach(name)
            print(f"attached to {name}")
            return s
        except frida.ProcessNotFoundError:
            last = f"找不到 {' / '.join(TARGETS)} —— 遊戲開了嗎?"
        except frida.PermissionDeniedError:
            last = f"attach {name} 被拒 —— sdo.bin 是提權程序,請用**系統管理員**身分重跑這支。"
            break
        except Exception as e:  # frida 版本間的例外型別不一致,兜底
            last = f"attach {name} 失敗:{e}"
    print(last or "attach 失敗")
    sys.exit(1)


def main():
    session = attach()
    script = session.create_script(open(JS, encoding="utf-8").read())
    script.on("message", on_message)
    script.set_log_handler(lambda level, text: (print(text), lines.append(text)))
    script.load()

    print(f"量測 {DURATION:.0f} 秒 —— 請讓遊戲留在海灘場景(不要暫停/切到背景,幀率會變)\n")
    try:
        time.sleep(DURATION)
    except KeyboardInterrupt:
        pass
    try:
        session.detach()
    except Exception:
        pass

    hits = [l for l in lines if "寫回" in l]
    if hits:
        print("\n=== 最後一筆實測(把這組貼回來,我寫進 SceneMapobjUvScrollCatalog.cs) ===")
        print(hits[-1].strip())
    else:
        print("\n沒收到數據 —— 相位一直沒動。確認遊戲真的在**海灘場景 SCN0004**(不是大廳/房間/選歌)。")


if __name__ == "__main__":
    main()
