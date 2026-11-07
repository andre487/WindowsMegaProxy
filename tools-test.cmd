@echo off
call "%~dp0scripts\tools.cmd" "%~dp0scripts\test_tools.py"
if errorlevel 1 exit /b %errorlevel%
call "%~dp0scripts\tools.cmd" "%~dp0scripts\test_package.py"
exit /b %errorlevel%
