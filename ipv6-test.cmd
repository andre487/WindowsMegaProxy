@echo off
setlocal
cd /d "%~dp0"
set "UV_CACHE_DIR=%CD%\.tools\uv-cache"
set "UV_PYTHON_INSTALL_DIR=%CD%\.tools\python"
uv run --locked --project scripts --group ssh-test python scripts\ssh_test.py --ipv6
exit /b %errorlevel%
