@echo off
setlocal
cd /d "%~dp0"
if not exist artifacts mkdir artifacts
call scripts\dotnet.cmd run --project tests\WindowsMegaProxy.Checks -c Release -- artifacts\test-config.json
if errorlevel 1 exit /b %errorlevel%
if not exist .tools\sing-box\sing-box.exe (
  echo sing-box missing. Run setup.cmd to validate the generated config against the real core.
  exit /b 1
)
.tools\sing-box\sing-box.exe check -c artifacts\test-config.json
if errorlevel 1 exit /b %errorlevel%
.tools\sing-box\sing-box.exe check -c artifacts\system-test-config.json
if errorlevel 1 exit /b %errorlevel%
.tools\sing-box\sing-box.exe check -c artifacts\system-exceptions-test-config.json
if errorlevel 1 exit /b %errorlevel%
for %%F in (artifacts\fingerprint-*.json) do (
  .tools\sing-box\sing-box.exe check -c "%%F"
  if errorlevel 1 exit /b 1
)
if not exist .tools\sing-box-h3\sing-box.exe (
  echo HTTP/3 core missing. Run setup.cmd.
  exit /b 1
)
call scripts\tools.cmd scripts\build_diagnostics.py --test
if errorlevel 1 exit /b %errorlevel%
.tools\sing-box-h3\sing-box.exe check -c artifacts\http3-test-config.json
if errorlevel 1 exit /b %errorlevel%
for %%F in (artifacts\ssh-*.json artifacts\jump-test-config.json) do (
  .tools\sing-box\sing-box.exe check -c "%%F"
  if errorlevel 1 exit /b 1
)
.tools\sing-box-h3\sing-box.exe check -c artifacts\jump-http3-test-config.json
if errorlevel 1 exit /b %errorlevel%
for %%C in (sing-box sing-box-h3) do (
  for %%F in (artifacts\dns-checks\*.json) do (
    .tools\%%C\sing-box.exe check -c "%%F"
    if errorlevel 1 exit /b 1
  )
)
exit /b %errorlevel%
