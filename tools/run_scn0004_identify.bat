@echo off
REM =====================================================================
REM  SCN0004: auto-identify which sine phase drives which water sheet.
REM  No eyeballing required -- it resolves the render objects to mapobj
REM  array slots and digs the resource names out of memory.
REM
REM  ** RUN AS ADMINISTRATOR ** (sdo.bin runs elevated).
REM
REM  1. Enter the BEACH stage (SCN0004) in the game.
REM  2. Run this as admin, stay in the stage a few seconds.
REM  3. Press Ctrl+C / close. Then send me:
REM        tools\scn0004_identify_log.txt
REM
REM  Read-only: nothing is written into the game's memory.
REM
REM  NOTE: pure ASCII on purpose (cmd.exe parses .bat with the OEM
REM  codepage). Chinese docs live in probe_scn0004_identify.js.
REM =====================================================================
setlocal
set "HERE=%~dp0"
set "JSFILE=%HERE%probe_scn0004_identify.js"
cd /d "%HERE%"

if not exist "%JSFILE%" (
    echo [!] Not found: "%JSFILE%"
    goto :done
)

where frida >nul 2>nul
if not errorlevel 1 (
    echo [i] Using frida CLI ...
    frida -n sdo.bin -l "%JSFILE%"
    goto :done
)

python -c "import frida" >nul 2>nul
if not errorlevel 1 (
    echo [i] Using python -m frida_tools.repl ...
    python -m frida_tools.repl -n sdo.bin -l "%JSFILE%"
    goto :done
)

py -c "import frida" >nul 2>nul
if not errorlevel 1 (
    echo [i] Using py -m frida_tools.repl ...
    py -m frida_tools.repl -n sdo.bin -l "%JSFILE%"
    goto :done
)

echo [!] No usable python+frida in this shell.
echo     pip install frida frida-tools
echo     then, in an ADMIN PowerShell:
echo         python -m frida_tools.repl -n sdo.bin -l "%JSFILE%"

:done
echo.
pause
