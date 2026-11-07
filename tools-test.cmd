@echo off
call "%~dp0scripts\tools.cmd" -m unittest discover -s "%~dp0scripts" -p "test_*.py"
exit /b %errorlevel%
