@echo off
setlocal
cd /d "%~dp0"
call "%~dp0scripts\tools.cmd" "%~dp0scripts\package.py" %*
exit /b %errorlevel%
