@echo off
REM ===========================================================================
REM  StockPicker - one-click setup and health check.
REM
REM  Just double-click this file. It will:
REM    1. Make sure the free Microsoft ".NET 8" toolkit is installed
REM       (and install it for you if it is missing).
REM    2. Build the app.
REM    3. Run a quick self-test to prove the app actually works.
REM
REM  It is completely safe to run this as many times as you like.
REM ===========================================================================
title StockPicker Setup

REM Run the real script with PowerShell. -ExecutionPolicy Bypass lets it run
REM even on a locked-down PC; it only affects this one launch, nothing else.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1"

echo.
echo Press any key to close this window...
pause >nul
