@echo off
rem Double-click launcher for the failure watchdog. Keeps the window open so the
rem poll log is visible; close the window to stop it.
setlocal
title LeBot failure watchdog
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0watchdog.ps1" %*
echo.
echo Watchdog exited.
pause
endlocal
