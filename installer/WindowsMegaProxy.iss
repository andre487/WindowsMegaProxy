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
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousPrivileges=no
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
OutputDir={#ReleaseDirectory}
OutputBaseFilename=WindowsMegaProxy-{#AppVersion}-win-x64-setup
SetupIconFile={#RepositoryRoot}\src\WindowsMegaProxy\Assets\app.ico
UninstallDisplayIcon={code:LaunchPath}
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
Name: "ssh"; Description: "{cm:InstallSSH}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[CustomMessages]
english.InstallSSH=Install SSH support (Windows OpenSSH Client), if missing
russian.InstallSSH=Установить поддержку SSH (клиент OpenSSH Windows), если она отсутствует
english.Prerequisites=Preparing required Windows components…
russian.Prerequisites=Подготовка необходимых компонентов Windows…
english.OperatingMode=Operating mode
russian.OperatingMode=Режим работы
english.ModeDescription=Choose when MegaProxy runs. Manual and sign-in modes require administrator privileges and stop intercepting traffic when the application exits.
russian.ModeDescription=Выберите способ запуска. Ручной запуск и запуск при входе требуют прав администратора; при выходе из приложения перехват трафика прекращается.
english.ManualMode=Manual launch (no autostart or Windows service)
russian.ManualMode=Ручной запуск (без автозапуска и службы Windows)
english.LoginMode=At Windows sign-in (administrator)
russian.LoginMode=При входе в Windows (администратор)
english.ServiceMode=Windows service (continues after closing the interface)
russian.ServiceMode=Служба Windows (работает после закрытия интерфейса)

[Dirs]
Name: "{app}"; Permissions: admins-full system-full users-readexec; Check: IsAdminInstallMode
Name: "{app}\versions"; Permissions: admins-full system-full users-readexec; Check: IsAdminInstallMode
Name: "{code:ProtectedUserBase}"; Permissions: admins-full system-full users-readexec; Check: IsUserInstall
Name: "{code:ProtectedUserRoot}"; Permissions: admins-full system-full users-readexec; Check: IsUserInstall
Name: "{code:ProtectedUserRoot}\versions"; Permissions: admins-full system-full users-readexec; Check: IsUserInstall

[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{code:VersionPath}"; Excludes: "launcher\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDirectory}\launcher\*"; DestDir: "{app}"; Flags: ignoreversion; Check: IsUserInstall

[Icons]
Name: "{autoprograms}\MegaProxy"; Filename: "{code:LaunchPath}"
Name: "{autodesktop}\MegaProxy"; Filename: "{code:LaunchPath}"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueName: "WindowsMegaProxy"; Flags: uninsdeletevalue; Check: IsAdminInstallMode
Root: HKLM; Subkey: "SOFTWARE\WindowsMegaProxy"; Flags: uninsdeletekey; Check: IsAdminInstallMode
Root: HKCU; Subkey: "SOFTWARE\WindowsMegaProxy\UserInstallation"; Flags: uninsdeletekey; Check: IsUserInstall

[UninstallDelete]
Type: files; Name: "{code:VersionPath}\user-installation.sid"; Check: IsUserInstall
Type: dirifempty; Name: "{code:VersionPath}"; Check: IsUserInstall

[Run]
Filename: "{code:LaunchPath}"; Description: "{cm:LaunchProgram,MegaProxy}"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: CanLaunch

[Code]
var TargetVersion: String;
    OwnerSid: String;
    ModePage: TInputOptionWizardPage;
    ModeConfigured, ConfigurationFailed: Boolean;

function IsUserInstall: Boolean;
begin
  Result := not IsAdminInstallMode;
end;

function CurrentSid: String;
var Code, Start, I: Integer; Output: TExecOutput; Line: String;
begin
  if OwnerSid = '' then begin
    if not ExecAndCaptureOutput(ExpandConstant('{sys}\whoami.exe'), '/user /fo csv /nh', '', SW_HIDE, ewWaitUntilTerminated, Code, Output) or
      (Code <> 0) or Output.Error or (GetArrayLength(Output.StdOut) <> 1) then RaiseException('Could not identify the Windows user.');
    Line := Output.StdOut[0];
    Start := Pos('S-1-', Line);
    if Start = 0 then RaiseException('Could not identify the Windows user SID.');
    I := Start;
    while (I <= Length(Line)) and (((Line[I] >= '0') and (Line[I] <= '9')) or (Line[I] = '-') or (Line[I] = 'S')) do I := I + 1;
    OwnerSid := Copy(Line, Start, I - Start);
  end;
  Result := OwnerSid;
end;

function InitializeSetup: Boolean;
var Code, I: Integer; Arguments, ExpectedSid: String;
begin
  Result := True;
  if IsAdminInstallMode then Exit;
  ExpectedSid := ExpandConstant('{param:OWNER|}');
  if (ExpectedSid <> '') and (ExpectedSid <> CurrentSid) then
    RaiseException('Current-user installation requires elevation by the same administrator account. Choose installation for all users instead.');
  if not IsAdmin then begin
    Arguments := '/CURRENTUSER /OWNER=' + CurrentSid;
    for I := 1 to ParamCount do
      if (CompareText(ParamStr(I), '/CURRENTUSER') <> 0) and (CompareText(ParamStr(I), '/ALLUSERS') <> 0) then
        Arguments := Arguments + ' "' + ParamStr(I) + '"';
    if not ShellExec('runas', ExpandConstant('{srcexe}'), Arguments, '', SW_SHOWNORMAL, ewNoWait, Code) then
      RaiseException('Administrator approval is required to install the protected network runtime.');
    Result := False;
  end;
end;

function ProtectedUserBase(Param: String): String;
begin
  Result := ExpandConstant('{commonpf}\WindowsMegaProxy.Users');
end;

function ProtectedUserRoot(Param: String): String;
begin
  Result := ProtectedUserBase('') + '\' + CurrentSid;
end;

function LaunchPath(Param: String): String; forward;

function CanLaunch: Boolean;
begin
  Result := ModeConfigured;
end;

function GetCustomSetupExitCode: Integer;
begin
  if ConfigurationFailed then Result := 1 else Result := 0;
end;

function SelectedMode: String;
begin
  Result := ExpandConstant('{param:MODE|}');
  if Result = '' then begin
    case ModePage.SelectedValueIndex of
      0: Result := 'manual';
      1: Result := 'login';
      2: Result := 'service';
    end;
  end;
  if (Result <> 'manual') and (Result <> 'login') and (Result <> 'service') then
    RaiseException('Unknown operating mode. Expected manual, login or service.');
  if IsUserInstall and (Result = 'service') then RaiseException('Windows service mode is not available in a current-user installation.');
end;

procedure InitializeWizard;
var Mode: String;
begin
  ModePage := CreateInputOptionPage(wpSelectTasks, CustomMessage('OperatingMode'),
    CustomMessage('OperatingMode'), CustomMessage('ModeDescription'), True, False);
  ModePage.Add(CustomMessage('ManualMode'));
  ModePage.Add(CustomMessage('LoginMode'));
  if IsAdminInstallMode then ModePage.Add(CustomMessage('ServiceMode'));
  ModePage.SelectedValueIndex := 0;
  if IsUserInstall then begin
    if RegQueryStringValue(HKCU64, 'SOFTWARE\WindowsMegaProxy\UserInstallation', 'Mode', Mode) and (Mode = 'login') then
      ModePage.SelectedValueIndex := 1;
  end else if RegQueryStringValue(HKLM64, 'SOFTWARE\WindowsMegaProxy', 'Mode', Mode) then begin
    if Mode = 'login' then ModePage.SelectedValueIndex := 1;
    if Mode = 'service' then ModePage.SelectedValueIndex := 2;
  end else if RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\WindowsMegaProxy') then
    ModePage.SelectedValueIndex := 2;
end;

function VersionPath(Param: String): String;
begin
  if TargetVersion = '' then
    if IsAdminInstallMode then
      TargetVersion := ExpandConstant('{app}\versions\{#VersionDirectory}-') + GetDateTimeString('yyyymmddhhnnss', '-', ':')
    else
      TargetVersion := ProtectedUserRoot('') + '\versions\{#VersionDirectory}-' + GetDateTimeString('yyyymmddhhnnss', '-', ':');
  Result := TargetVersion;
end;

function LaunchPath(Param: String): String;
begin
  if IsAdminInstallMode then Result := VersionPath('') + '\WindowsMegaProxy.exe'
  else Result := ExpandConstant('{app}\WindowsMegaProxy.Launcher.exe');
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
  Directory := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');
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
    SelectedMode;
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
var Code: Integer; Command: String;
begin
  if Step = ssPostInstall then begin
    ConfigurationFailed := True;
    if IsAdminInstallMode then Command := '--configure-mode ' else Command := '--configure-user ';
    if not ExecAndLogOutput(VersionPath('') + '\WindowsMegaProxy.Service.exe', Command + SelectedMode, '', SW_HIDE, ewWaitUntilTerminated, Code, nil) or (Code <> 0) then begin
      RaiseException('Could not configure the MegaProxy operating mode. Close MegaProxy in all Windows sessions and retry. See the setup log.');
    end;
    ModeConfigured := True;
    ConfigurationFailed := False;
  end;
end;

function InitializeUninstall: Boolean;
var Manager, Service: THandle; Status: TServiceStatus; Attempt, Code: Integer; ImagePath, Executable: String;
begin
  Result := False;
  if IsUserInstall then begin
    if not RegQueryStringValue(HKCU64, 'SOFTWARE\WindowsMegaProxy\UserInstallation', 'HelperPath', Executable) then Exit;
    { Never execute a user-writable helper path with the uninstaller's elevated token. }
    if Pos(Lowercase(ProtectedUserRoot('') + '\versions\'), Lowercase(Executable)) <> 1 then Exit;
    if Pos('\..', Executable) <> 0 then Exit;
    Result := ExecAndLogOutput(Executable, '--prepare-uninstall', '', SW_HIDE, ewWaitUntilTerminated, Code, nil) and (Code = 0);
    Exit;
  end;
  if not RegQueryStringValue(HKLM64, 'SOFTWARE\WindowsMegaProxy', 'HelperPath', ImagePath) then
    RegQueryStringValue(HKLM64, 'SYSTEM\CurrentControlSet\Services\WindowsMegaProxy', 'ImagePath', ImagePath);
  if ImagePath <> '' then begin
    Executable := RemoveQuotes(ImagePath);
    if not ExecAndLogOutput(Executable, '--prepare-uninstall', '', SW_HIDE, ewWaitUntilTerminated, Code, nil) or (Code <> 0) then begin
      MsgBox('Close MegaProxy in all Windows sessions and retry. The .NET Desktop Runtime must remain installed until MegaProxy is removed.', mbError, MB_OK);
      Exit;
    end;
  end;
  Manager := OpenSCManager('', 'ServicesActive', 1);
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
