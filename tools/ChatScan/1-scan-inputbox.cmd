@echo off
chcp 65001 >nul
cd /d "%~dp0"
set "PID="
for /f "delims=" %%i in ('powershell -NoProfile -Command "Get-Process AION2 | Sort-Object WorkingSet64 -Descending | Select-Object -First 1 -ExpandProperty Id"') do set "PID=%%i"
if "%PID%"=="" (
  echo no AION2 process found
  pause
  exit /b 1
)
echo ==== target pid %PID%, scanning chat input text ====
"%~dp0bin\Release\net10.0-windows\ChatScan.exe" %PID% scan "where the fishing vendor is" "%~dp0out\before.txt"
echo.
echo ==== done, see out\before.txt ====
echo.
pause
