@echo off
REM =====================================================================
REM  SCN0004: which sine phase drives WHICH water sheet?
REM  Decisive live experiment -- freezes one phase at a time so you can
REM  see with your own eyes which sheet stops moving.
REM
REM  ** RUN AS ADMINISTRATOR ** (sdo.bin runs elevated).
REM
REM  1. Enter the BEACH stage (SCN0004) in the game.
REM  2. Run this as admin. It cycles every 6 seconds:
REM       [1] both normal   [2] freeze FAST phase   [3] freeze SLOW phase
REM  3. Watch which sheet stops:
REM       LANG = the white surf band breaking on the sand
REM       SEA  = the big translucent ripple layer over the whole ocean
REM       (the bright cyan caustic seabed is a 100ms frame animation and
REM        keeps flickering the whole time -- that is not the one)
REM  4. Report which sheet froze in step [2] and which in step [3].
REM
REM  Safe: only two animation float globals are written; nothing is patched.
REM  Stopping the script restores normal motion on the next frame.
REM
REM  NOTE: pure ASCII on purpose -- cmd.exe parses .bat with the OEM
REM  codepage, so UTF-8 Chinese here would be shredded. Chinese docs are
REM  in probe_scn0004_which_sheet.js.
REM =====================================================================
setlocal
set "HERE=%~dp0"
set "JSFILE=%HERE%probe_scn0004_which_sheet.js"
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
