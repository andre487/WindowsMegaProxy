@echo off
setlocal
cd /d "%~dp0"
call scripts\tools.cmd scripts\core_smoke.py %*
exit /b %errorlevel%
