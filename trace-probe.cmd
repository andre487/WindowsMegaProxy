@echo off
setlocal
cd /d "%~dp0"
call scripts\dotnet.cmd publish src\MegaProxy.TraceProbe\MegaProxy.TraceProbe.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -o artifacts\trace-probe --nologo
if errorlevel 1 exit /b %errorlevel%
artifacts\trace-probe\MegaProxy.TraceProbe.exe
exit /b %errorlevel%
