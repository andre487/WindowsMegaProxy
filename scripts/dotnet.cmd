@echo off
call "%~dp0tools.cmd" "%~dp0dotnet.py" %*
exit /b %errorlevel%
