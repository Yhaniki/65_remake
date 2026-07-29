@echo off
REM =====================================================================
REM  SCN0004 (beach) water-animation measurement -- ONLINE client sdo.bin
REM
REM  ** RUN AS ADMINISTRATOR ** (right-click -> Run as administrator).
REM  sdo.bin runs elevated; a non-admin attach is denied.
REM
REM  1. Start the game, get past the launcher, and enter the BEACH stage
REM     (SCN0004) -- the phase variables only move inside that stage.
REM  2. Run this .bat as admin.
REM  3. Stay in the beach stage for ~20 s. Do NOT alt-tab (frame rate
REM     changes in the background and the measurement would be wrong).
REM  4. Paste the last "-> SceneMapobjUvScrollCatalog ..." line back.
REM
REM  NOTE: this file is INTENTIONALLY pure ASCII. cmd.exe parses .bat with
REM  the OEM codepage (CP950 here), so UTF-8 Chinese in a .bat gets shredded
REM  into bogus commands. Chinese docs live in the .py / .js instead.
REM =====================================================================
setlocal
set "HERE=%~dp0"
set "PYFILE=%HERE%measure_scn0004_wave.py"
set "JSFILE=%HERE%hook_scn0004_wave.js"
cd /d "%HERE%"

if not exist "%PYFILE%" (
    echo [!] Not found: "%PYFILE%"
    echo     Keep this .bat next to measure_scn0004_wave.py and hook_scn0004_wave.js.
    goto :done
)

REM 1) frida CLI, if it happens to be on PATH
where frida >nul 2>nul
if not errorlevel 1 (
    echo [i] Using frida CLI ...
    frida -n sdo.bin -l "%JSFILE%"
    goto :done
)

REM 2) python + frida module
python -c "import frida" >nul 2>nul
if not errorlevel 1 (
    echo [i] Using python + frida module ...
    python "%PYFILE%"
    goto :done
)

REM 3) py launcher (python may be a Microsoft Store alias that fails when elevated)
py -c "import frida" >nul 2>nul
if not errorlevel 1 (
    echo [i] Using py + frida module ...
    py "%PYFILE%"
    goto :done
)

echo [!] No usable python+frida in this shell.
echo.
echo     If you use conda, the active env may not have frida. Try:
echo         pip install frida frida-tools
echo     Then run, in an ADMIN PowerShell:
echo         python "%PYFILE%"

:done
echo.
pause
