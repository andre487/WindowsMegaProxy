@echo off
call "%~dp0scripts\tools.cmd" "%~dp0scripts\install_dependencies.py" %*
exit /b %errorlevel%
