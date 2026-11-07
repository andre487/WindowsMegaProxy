@echo off
call "%~dp0scripts\tools.cmd" "%~dp0scripts\render_icons.py" %*
exit /b %errorlevel%
