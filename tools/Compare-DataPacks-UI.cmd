@echo off
rem 雙擊啟動 DATA 包差異比對 GUI (以 STA 執行，拖曳/資料夾對話框才正常)
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0Compare-DataPacks-UI.ps1"
