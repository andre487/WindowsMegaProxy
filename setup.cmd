@echo off
setlocal
cd /d "%~dp0"
call "%~dp0scripts\tools.cmd" "%~dp0scripts\setup.py" %*
exit /b %errorlevel%
