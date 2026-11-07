@echo off
rem SPT co-op mod layer - double-click to install Fika + all mods into THIS folder.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-mods.ps1" %*
echo.
pause
