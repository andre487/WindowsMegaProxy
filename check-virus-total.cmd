@echo off
call "%~dp0scripts\tools.cmd" "%~dp0scripts\virus_total.py" %*
exit /b %errorlevel%
