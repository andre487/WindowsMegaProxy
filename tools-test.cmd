@echo off
call "%~dp0scripts\tools.cmd" "%~dp0scripts\test_tools.py"
exit /b %errorlevel%
