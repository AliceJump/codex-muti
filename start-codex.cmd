@echo off
where pwsh.exe >nul 2>nul
if errorlevel 1 (
  echo PowerShell 7 is required. Install it, then run this launcher again.
  pause
  exit /b 1
)
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start-codex.ps1" %*
if errorlevel 1 pause
