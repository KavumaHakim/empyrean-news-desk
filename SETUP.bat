@echo off
rem One-time setup on a new computer: installs .NET 8, WebView2 and FFmpeg, builds NEWS DESK
rem and adds a desktop shortcut. See setup.ps1 for the details.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1"
pause
