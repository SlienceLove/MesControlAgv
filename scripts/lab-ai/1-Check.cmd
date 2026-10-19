@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-Pilot.ps1" -Mode Check
pause
