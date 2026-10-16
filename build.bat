@echo off
rem Rebuilds app\Chop.exe from src\ using the C# compiler built into Windows.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
pause
