@echo off
setlocal
cd /d "%~dp0"
call "%~dp0scripts\tools.cmd" "%~dp0scripts\install.py" %*
exit /b %errorlevel%
