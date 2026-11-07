@echo off
setlocal
cd /d "%~dp0"
call scripts\dotnet.cmd run --project tests\WindowsMegaProxy.UiChecks -c Release -- --engine-check
if errorlevel 1 exit /b %errorlevel%
type artifacts\engine-check\result.txt
exit /b %errorlevel%
