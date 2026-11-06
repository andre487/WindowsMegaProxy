@echo off
setlocal
cd /d "%~dp0"
call scripts\dotnet.cmd run --project tests\WindowsMegaProxy.UiChecks -c Release
exit /b %errorlevel%
