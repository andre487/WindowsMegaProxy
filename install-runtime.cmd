@echo off
call "%~dp0scripts\tools.cmd" "%~dp0scripts\install_dependencies.py" --runtime-only %*
exit /b %errorlevel%
