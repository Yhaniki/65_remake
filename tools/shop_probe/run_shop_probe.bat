@echo off
REM =====================================================================
REM  Record which avatar files the ONLINE shop preview loads (sdo.bin).
REM  RUN THIS AS ADMINISTRATOR (right-click -> Run as administrator),
REM  because sdo.bin runs elevated and a non-admin attach is denied.
REM
REM  1. Start the game and get past the launcher (be at the lobby/room).
REM  2. Double-click this .bat (as admin). It attaches to sdo.bin.
REM  3. In the game: open the 商城, click 发型 / 表情 / 项链 / 下装 tabs,
REM     hover a card and press try-on. Watch the window + the log file.
REM  4. Close with Ctrl+C in this window when done.
REM
REM  Output log: shop_avatar_online_log.txt (this folder).
REM =====================================================================
cd /d "%~dp0"

REM Prefer the frida CLI if it is on PATH; otherwise use the Python module.
where frida >nul 2>nul
if %errorlevel%==0 (
    echo [i] Using frida CLI...
    frida -n sdo.bin -l hook_online_avatar_files.js
) else (
    echo [i] frida CLI not on PATH - using python probe.py ...
    python probe.py
)

pause
