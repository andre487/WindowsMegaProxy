@echo off
setlocal
cd /d "%~dp0"
call scripts\tools.cmd scripts\build_core.py %*
exit /b %errorlevel%
