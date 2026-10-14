@echo off
pwsh -NoProfile -File "%~dp0scripts\deploy-menu.ps1" %*
set "deploy_exit=%ERRORLEVEL%"
pause
exit /b %deploy_exit%
