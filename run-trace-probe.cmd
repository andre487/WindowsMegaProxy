@echo off
setlocal
call "%~dp0trace-probe.cmd" %*
exit /b %errorlevel%
