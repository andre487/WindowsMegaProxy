@echo off
setlocal
cd /d "%~dp0"
call scripts\dotnet.cmd publish src\MegaProxy.TraceProbe\MegaProxy.TraceProbe.csproj -c Release -r win-x64 --self-contained false -o artifacts\trace-probe --nologo
if errorlevel 1 exit /b %errorlevel%
set "DOTNET_ROOT=%~dp0.tools\dotnet"
artifacts\trace-probe\MegaProxy.TraceProbe.exe
exit /b %errorlevel%
