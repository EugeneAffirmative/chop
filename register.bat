@echo off
rem Makes Chop show up in Settings > Default apps (current user only). Run with -Uninstall to undo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0register.ps1" %*
