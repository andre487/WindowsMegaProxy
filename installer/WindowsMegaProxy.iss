#include "package-config.iss"

[Setup]
AppId={{1D673B75-6671-42B4-97AC-99A85041D6C3}
AppName=MegaProxy
AppVersion={#AppVersion}
AppPublisher=MegaProxy
AppPublisherURL=https://github.com/andre487/WindowsMegaProxy
DefaultDirName={autopf}\WindowsMegaProxy
DisableDirPage=yes
UsePreviousAppDir=no
DefaultGroupName=MegaProxy
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
OutputDir={#ReleaseDirectory}
OutputBaseFilename=WindowsMegaProxy-{#AppVersion}-win-x64-setup
SetupIconFile={#RepositoryRoot}\src\WindowsMegaProxy\Assets\app.ico
UninstallDisplayIcon={code:VersionPath}\WindowsMegaProxy.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
SetupLogging=yes
LicenseFile={#RepositoryRoot}\LICENSE
VersionInfoVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "ssh"; Description: "{cm:InstallSSH}"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[CustomMessages]
english.InstallSSH=Install SSH support (Windows OpenSSH Client), if missing
russian.InstallSSH=Установить поддержку SSH (клиент OpenSSH Windows), если она отсутствует
english.Prerequisites=Preparing required Windows components…
russian.Prerequisites=Подготовка необходимых компонентов Windows…

[Dirs]
Name: "{app}"; Permissions: admins-full system-full users-readexec
Name: "{app}\versions"; Permissions: admins-full system-full users-readexec

[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{code:VersionPath}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MegaProxy"; Filename: "{code:VersionPath}\WindowsMegaProxy.exe"
Name: "{autodesktop}\MegaProxy"; Filename: "{code:VersionPath}\WindowsMegaProxy.exe"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueName: "WindowsMegaProxy"; Flags: uninsdeletevalue

[Run]
Filename: "{code:VersionPath}\WindowsMegaProxy.exe"; Description: "{cm:LaunchProgram,MegaProxy}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
var TargetVersion: String;

function VersionPath(Param: String): String;
begin
  if TargetVersion = '' then
    TargetVersion := ExpandConstant('{app}\versions\{#VersionDirectory}-') + GetDateTimeString('yyyymmddhhnnss', '-', ':');
  Result := TargetVersion;
end;

type
  TServiceStatus = record
    ServiceType, CurrentState, ControlsAccepted, Win32ExitCode: Cardinal;
    ServiceSpecificExitCode, CheckPoint, WaitHint: Cardinal;
  end;

function OpenSCManager(Machine, Database: String; Access: Cardinal): THandle;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function OpenService(Manager: THandle; Name: String; Access: Cardinal): THandle;
  external 'OpenServiceW@advapi32.dll stdcall';
function ControlService(Service: THandle; Control: Cardinal; var Status: TServiceStatus): Boolean;
  external 'ControlService@advapi32.dll stdcall';
function QueryServiceStatus(Service: THandle; var Status: TServiceStatus): Boolean;
  external 'QueryServiceStatus@advapi32.dll stdcall';
function DeleteService(Service: THandle): Boolean;
  external 'DeleteService@advapi32.dll stdcall';
function CloseServiceHandle(Handle: THandle): Boolean;
  external 'CloseServiceHandle@advapi32.dll stdcall';

function RuntimeInstalled: Boolean;
var Names: TArrayOfString; I: Integer; Version, Minimum: Int64; Directory: String; Entry: TFindRec;
begin
  Result := False;
  if not StrToVersion('{#RuntimeVersion}', Minimum) then Exit;
  if RegGetValueNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if (Pos('10.', Names[I]) = 1) and StrToVersion(Names[I], Version) and
        (ComparePackedVersion(Version, Minimum) >= 0) then Result := True;
  if Result then Exit;
  Directory := ExpandConstant('{autopf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if FindFirst(Directory + '\*', Entry) then begin
    try
      repeat
        if (Pos('10.', Entry.Name) = 1) and StrToVersion(Entry.Name, Version) and
          (ComparePackedVersion(Version, Minimum) >= 0) and
          FileExists(Directory + '\' + Entry.Name + '\PresentationFramework.dll') then Result := True;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer; Download: TDownloadWizardPage;
begin
  Result := '';
  try
    if not RuntimeInstalled then begin
      Download := CreateDownloadPage(CustomMessage('Prerequisites'), '.NET Desktop Runtime', nil);
      try
        Download.Add('{#RuntimeURL}', 'dotnet-desktop.exe', '{#RuntimeSHA256}');
        Download.Show;
        Download.Download;
      finally
        Download.Hide;
        Download.Free;
      end;
      if not Exec(ExpandConstant('{tmp}\dotnet-desktop.exe'), '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code) then
        RaiseException('Could not start the .NET Desktop Runtime installer.');
      Log(Format('.NET Desktop Runtime installer exit code: %d', [Code]));
      if Code = 3010 then begin
        NeedsRestart := True;
        RaiseException('Restart Windows before continuing installation. Existing MegaProxy files have not been changed.');
      end;
      if (Code <> 0) or not RuntimeInstalled then RaiseException('.NET Desktop Runtime installation failed. Existing MegaProxy files have not been changed.');
    end;
    if WizardIsTaskSelected('ssh') and not FileExists(ExpandConstant('{sys}\OpenSSH\ssh-keyscan.exe')) then begin
      if not Exec(ExpandConstant('{sys}\dism.exe'), '/Online /Add-Capability /CapabilityName:OpenSSH.Client~~~~0.0.1.0 /NoRestart', '', SW_HIDE, ewWaitUntilTerminated, Code) then
        RaiseException('Could not start Windows OpenSSH Client installation.');
      if Code = 3010 then begin NeedsRestart := True; RaiseException('Restart Windows before continuing installation.'); end;
      if (Code <> 0) or not FileExists(ExpandConstant('{sys}\OpenSSH\ssh-keyscan.exe')) then
        RaiseException('OpenSSH Client installation failed. Retry or deselect SSH support.');
    end;
  except
    Result := GetExceptionMessage;
  end;
end;

procedure CurStepChanged(Step: TSetupStep);
var Code: Integer;
begin
  if Step = ssPostInstall then
    if not ExecAndLogOutput(VersionPath('') + '\WindowsMegaProxy.Service.exe', '--install-service', '', SW_HIDE, ewWaitUntilTerminated, Code, nil) or (Code <> 0) then
      RaiseException('Could not configure the MegaProxy service. Close MegaProxy in all Windows sessions and retry. See the setup log.');
end;

function InitializeUninstall: Boolean;
var Manager, Service: THandle; Status: TServiceStatus; Attempt, Code: Integer; ImagePath, Executable: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM64, 'SYSTEM\CurrentControlSet\Services\WindowsMegaProxy', 'ImagePath', ImagePath) then begin
    Executable := RemoveQuotes(ImagePath);
    if not ExecAndLogOutput(Executable, '--prepare-uninstall', '', SW_HIDE, ewWaitUntilTerminated, Code, nil) or (Code <> 0) then begin
      MsgBox('Close MegaProxy in all Windows sessions and retry. The .NET Desktop Runtime must remain installed until MegaProxy is removed.', mbError, MB_OK);
      Exit;
    end;
  end;
  Manager := OpenSCManager('', '', 1);
  if Manager = 0 then Exit;
  try
    Service := OpenService(Manager, 'WindowsMegaProxy', $10024);
    if Service = 0 then begin Result := DLLGetLastError = 1060; Exit; end;
    try
      if not QueryServiceStatus(Service, Status) then Exit;
      if Status.CurrentState <> 1 then begin
        ControlService(Service, 1, Status);
        for Attempt := 1 to 60 do begin
          if not QueryServiceStatus(Service, Status) then Exit;
          if Status.CurrentState = 1 then Break;
          Sleep(500);
        end;
        if Status.CurrentState <> 1 then begin MsgBox('Could not stop MegaProxy. Retry after closing all clients.', mbError, MB_OK); Exit; end;
      end;
      Result := DeleteService(Service);
    finally
      CloseServiceHandle(Service);
    end;
  finally
    CloseServiceHandle(Manager);
  end;
  { ProgramData, personal settings and the shared Wintun driver are deliberately retained. }
end;
