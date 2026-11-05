@echo off
chcp 65001 >nul
cd /d "%~dp0"
"%~dp0bin\Release\net10.0-windows\ChatProbe.exe" post
pause
