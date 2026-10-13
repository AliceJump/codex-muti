@echo off
pwsh -NoProfile -File "%~dp0scripts\deploy-local.ps1" -Dashboard
set "deploy_exit=%ERRORLEVEL%"
pause
exit /b %deploy_exit%
