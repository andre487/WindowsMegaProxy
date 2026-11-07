@echo off
setlocal
set "UV_CACHE_DIR=%~dp0..\.tools\uv-cache"
set "UV_PYTHON_INSTALL_DIR=%~dp0..\.tools\python"
where uv >nul 2>nul
if errorlevel 1 (
  echo uv is required. Install it from https://docs.astral.sh/uv/getting-started/installation/
  exit /b 1
)
uv run --locked --project "%~dp0." python %*
exit /b %errorlevel%
