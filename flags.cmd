@echo off
call "%~dp0scripts\tools.cmd" "%~dp0scripts\render_flags.py" %*
exit /b %errorlevel%
