@echo off
chcp 65001 >nul
set "deploy_target=%~1"
if not "%deploy_target%"=="" goto deploy
echo 1. 全部部署（核心 + 桌面组件）
echo 2. 仅部署核心（代理和 Dashboard）
echo 3. 仅部署桌面组件（托盘和悬浮窗）
choice /C 123 /N /M "请选择 [1-3]: "
set "deploy_target=All"
if errorlevel 2 set "deploy_target=Core"
if errorlevel 3 set "deploy_target=Components"
:deploy
pwsh -NoProfile -File "%~dp0scripts\deploy-local.ps1" -Target "%deploy_target%" -Dashboard
set "deploy_exit=%ERRORLEVEL%"
pause
exit /b %deploy_exit%
