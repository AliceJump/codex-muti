@echo off
pwsh -NoProfile -File "%~dp0scripts\start-local.ps1"
set "pool_exit=%ERRORLEVEL%"
if not %pool_exit%==0 pause
exit /b %pool_exit%
