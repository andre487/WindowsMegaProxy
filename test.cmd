@echo off
setlocal
cd /d "%~dp0"
if not exist artifacts mkdir artifacts
call scripts\dotnet.cmd run --project tests\WindowsMegaProxy.Checks -c Release -- artifacts\test-config.json
if errorlevel 1 exit /b %errorlevel%
call scripts\tools.cmd scripts\check_core_configs.py
if errorlevel 1 exit /b %errorlevel%
call scripts\tools.cmd scripts\build_diagnostics.py --test
exit /b %errorlevel%
