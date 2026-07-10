@echo off
REM Build SdoImeHook.dll with VS2019 x64 toolchain, copy into Unity Assets/Plugins/x86_64/
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 (echo vcvars64 failed & exit /b 1)
cd /d "%~dp0"
cl /nologo /utf-8 /LD /O2 /EHsc SdoImeHook.cpp /link imm32.lib user32.lib
if errorlevel 1 (echo compile failed & exit /b 1)
if not exist "..\..\Assets\Plugins\x86_64" mkdir "..\..\Assets\Plugins\x86_64"
copy /Y SdoImeHook.dll "..\..\Assets\Plugins\x86_64\SdoImeHook.dll"
echo BUILD_OK
