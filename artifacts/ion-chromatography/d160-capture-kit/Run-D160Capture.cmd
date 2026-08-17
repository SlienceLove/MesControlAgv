@echo off
setlocal
if /I "%~1"=="ELEVATED" goto run
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -ArgumentList 'ELEVATED' -Verb RunAs"
exit /b

:run
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Capture-D160Usb.ps1" -DurationSeconds 60
echo.
if errorlevel 1 (
  echo Capture failed. Keep this window open and record the error above.
) else (
  echo Capture finished. Copy only the ZIP from the results folder back to the laptop.
)
pause
