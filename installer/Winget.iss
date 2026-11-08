procedure InstallWingetDependencies;
var Winget, Arguments: String; Code: Integer;
begin
  Winget := ExpandConstant('{localappdata}\Microsoft\WindowsApps\winget.exe');
  if not FileExists(Winget) then
    RaiseException('Windows Package Manager (App Installer) is required. Install or update App Installer, then retry setup.');
  Arguments := 'install SagerNet.sing-box Microsoft.DotNet.DesktopRuntime.10 --exact --source winget ' +
    '--scope machine --architecture x64 --silent --accept-source-agreements --accept-package-agreements --disable-interactivity';
  Log('Installing machine-wide sing-box and .NET Desktop Runtime in one WinGet invocation.');
  if not ExecAndLogOutput(Winget, Arguments, '', SW_HIDE, ewWaitUntilTerminated, Code, nil) then
    RaiseException('Could not start WinGet. Setup must run in a Windows user session, not as SYSTEM.');
  Log(Format('WinGet dependency installation exit code: %d', [Code]));
  if (Code <> 0) and (Code <> $8A15002B) then
    RaiseException('WinGet could not prepare the required dependencies. See the setup log and retry.');
end;
