@echo off
rem SPT + Fika co-op installer - DOUBLE-CLICK THIS ONE.
rem Runs setup-spt.ps1 with the execution policy bypassed; the script
rem self-elevates through the UAC prompt (click Yes).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-spt.ps1" %*
echo.
pause
