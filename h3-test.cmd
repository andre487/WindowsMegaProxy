@echo off
setlocal
cd /d "%~dp0"
call scripts\dotnet.cmd run --project tests\WindowsMegaProxy.UiChecks -c Release -- --http3-check
if errorlevel 1 exit /b %errorlevel%
type artifacts\http3-checks\result.txt
exit /b %errorlevel%
