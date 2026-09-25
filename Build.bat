@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0BuildTools\Build.ps1" %*
exit /b %errorlevel%
