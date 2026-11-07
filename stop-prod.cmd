@echo off
setlocal
cd /d "%~dp0"
call "%~dp0scripts\tools.cmd" "%~dp0scripts\stop_prod.py" %*
exit /b %errorlevel%
