@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-win-x64.ps1" -Runtime win-x64
if errorlevel 1 pause
