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
ArchiveExtraction=full
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
Name: "automaticupdates"; Description: "{cm:AutomaticUpdates}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[CustomMessages]
english.AutomaticUpdates=Automatically check for and download application updates
russian.AutomaticUpdates=Автоматически проверять и скачивать обновления приложения
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
Name: "{code:ProtectedUserBase}"; Permissions: admins-full system-full users-readexec; Check: IsUserInstall
Name: "{code:ProtectedUserRoot}"; Permissions: admins-full system-full users-readexec; Check: IsUserInstall

[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{code:RuntimePath}"; Excludes: "launcher\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDirectory}\launcher\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsUserInstall

[Icons]
Name: "{autoprograms}\MegaProxy"; Filename: "{code:LaunchPath}"
Name: "{autodesktop}\MegaProxy"; Filename: "{code:LaunchPath}"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueName: "WindowsMegaProxy"; Flags: uninsdeletevalue; Check: IsAdminInstallMode
Root: HKLM; Subkey: "SOFTWARE\WindowsMegaProxy"; Flags: uninsdeletekey; Check: IsAdminInstallMode
Root: HKCU; Subkey: "SOFTWARE\WindowsMegaProxy\UserInstallation"; Flags: uninsdeletekey; Check: IsUserInstall

[UninstallDelete]
Type: filesandordirs; Name: "{code:RuntimePath}\update"
Type: files; Name: "{code:RuntimePath}\user-installation.sid"; Check: IsUserInstall
Type: dirifempty; Name: "{code:RuntimePath}"; Check: IsUserInstall

[Run]
Filename: "{code:LaunchPath}"; Description: "{cm:LaunchProgram,MegaProxy}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
var OwnerSid, PreviousHelper, PreviousMode: String;
    Prepared: Boolean;
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

function IsUpdateInstall: Boolean;
begin
  Result := ModeConfigured and (ExpandConstant('{param:UPDATE|0}') = '1');
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

function RuntimePath(Param: String): String;
begin
  if IsAdminInstallMode then Result := ExpandConstant('{app}')
  else Result := ProtectedUserRoot('');
end;

function LaunchPath(Param: String): String;
begin
  if IsAdminInstallMode then Result := RuntimePath('') + '\WindowsMegaProxy.exe'
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

procedure StopInstalledService;
var Manager, Service: THandle; Status: TServiceStatus; Attempt: Integer;
begin
  Manager := OpenSCManager('', 'ServicesActive', 1);
  if Manager = 0 then RaiseException('Could not open Service Control Manager.');
  try
    Service := OpenService(Manager, 'WindowsMegaProxy', $24);
    if Service = 0 then begin
      if DLLGetLastError <> 1060 then RaiseException('Could not open the MegaProxy service.');
      Exit;
    end;
    try
      if not QueryServiceStatus(Service, Status) then RaiseException('Could not query the MegaProxy service.');
      if Status.CurrentState = 1 then Exit;
      if (Status.CurrentState <> 3) and not ControlService(Service, 1, Status) then RaiseException('Could not stop the MegaProxy service.');
      for Attempt := 1 to 60 do begin
        if not QueryServiceStatus(Service, Status) then RaiseException('Could not query the MegaProxy service.');
        if Status.CurrentState = 1 then Exit;
        Sleep(500);
      end;
      RaiseException('The MegaProxy service did not stop. Close clients and retry.');
    finally CloseServiceHandle(Service); end;
  finally CloseServiceHandle(Manager); end;
end;

procedure PrepareExistingInstallation;
var Code: Integer; Root: String;
begin
  if Prepared then Exit;
  PreviousMode := 'manual';
  if IsUserInstall then begin
    RegQueryStringValue(HKCU64, 'SOFTWARE\WindowsMegaProxy\UserInstallation', 'HelperPath', PreviousHelper);
    RegQueryStringValue(HKCU64, 'SOFTWARE\WindowsMegaProxy\UserInstallation', 'Mode', PreviousMode);
  end else begin
    RegQueryStringValue(HKLM64, 'SOFTWARE\WindowsMegaProxy', 'HelperPath', PreviousHelper);
    RegQueryStringValue(HKLM64, 'SOFTWARE\WindowsMegaProxy', 'Mode', PreviousMode);
    if (PreviousHelper = '') and RegQueryStringValue(HKLM64, 'SYSTEM\CurrentControlSet\Services\WindowsMegaProxy', 'ImagePath', PreviousHelper) then begin
      PreviousHelper := RemoveQuotes(PreviousHelper);
      PreviousMode := 'service';
    end;
  end;
  Root := RuntimePath('');
  if PreviousHelper <> '' then begin
    if (Pos(Lowercase(Root + '\'), Lowercase(PreviousHelper)) <> 1) or
      (Pos('\..', PreviousHelper) <> 0) or (CompareText(ExtractFileName(PreviousHelper), 'WindowsMegaProxy.Service.exe') <> 0) then
      RaiseException('The previous installation helper is outside the protected installation directory.');
    if (ExpandConstant('{param:STAGEDUPDATE|0}') <> '1') and not FileExists(PreviousHelper) then
      RaiseException('The previous installation helper is missing. Repair the previous installation first.');
  end;
  if IsAdminInstallMode then StopInstalledService;
  Prepared := True;
  if (ExpandConstant('{param:STAGEDUPDATE|0}') <> '1') and (PreviousHelper <> '') and
    (not ExecAndLogOutput(PreviousHelper, '--prepare-uninstall', '', SW_HIDE, ewWaitUntilTerminated, Code, nil) or (Code <> 0)) then
    RaiseException('Could not close the previous installation. Close MegaProxy in all Windows sessions and retry.');
end;

procedure DeinitializeSetup;
var Code: Integer; Command: String;
begin
  if Prepared and not ModeConfigured and (PreviousHelper <> '') and FileExists(PreviousHelper) then begin
    if IsUserInstall then Command := '--configure-user ' else Command := '--configure-mode ';
    if not ExecAndLogOutput(PreviousHelper, Command + PreviousMode, '', SW_HIDE, ewWaitUntilTerminated, Code, nil) or (Code <> 0) then
      Log('Could not restore the previous operating mode after installation was interrupted.');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer;
begin
  Result := '';
  try
    SelectedMode;
    if not FileExists(ExpandConstant('{sys}\OpenSSH\ssh-keyscan.exe')) then begin
      if not Exec(ExpandConstant('{sys}\dism.exe'), '/Online /Add-Capability /CapabilityName:OpenSSH.Client~~~~0.0.1.0 /NoRestart', '', SW_HIDE, ewWaitUntilTerminated, Code) then
        RaiseException('Could not start Windows OpenSSH Client installation.');
      if Code = 3010 then begin NeedsRestart := True; RaiseException('Restart Windows before continuing installation.'); end;
      if (Code <> 0) or not FileExists(ExpandConstant('{sys}\OpenSSH\ssh-keyscan.exe')) then
        RaiseException('OpenSSH Client installation failed. Retry after checking Windows Update access.');
    end;
    PrepareExistingInstallation;
  except
    Result := GetExceptionMessage;
  end;
end;

procedure CurStepChanged(Step: TSetupStep);
var Code: Integer; Command: String;
begin
  if Step = ssPostInstall then begin
    ConfigurationFailed := True;
    if not ExecAndLogOutput(RuntimePath('') + '\WindowsMegaProxy.Service.exe', '--check-dependencies', '', SW_HIDE, ewWaitUntilTerminated, Code, nil) or (Code <> 0) then
      RaiseException('A bundled core is missing or invalid. See the setup log.');
    if IsAdminInstallMode then Command := '--configure-mode ' else Command := '--configure-user ';
    Command := Command + SelectedMode + ' --automatic-updates ';
    if WizardIsTaskSelected('automaticupdates') then Command := Command + 'true' else Command := Command + 'false';
    if not ExecAndLogOutput(RuntimePath('') + '\WindowsMegaProxy.Service.exe', Command, '', SW_HIDE, ewWaitUntilTerminated, Code, nil) or (Code <> 0) then begin
      RaiseException('Could not configure the MegaProxy operating mode. Close MegaProxy in all Windows sessions and retry. See the setup log.');
    end;
    ModeConfigured := True;
    ConfigurationFailed := False;
  end;
  if (Step = ssDone) and ModeConfigured then begin
    { Regular Run entries execute before ssPostInstall has configured the mode. }
    if not Exec(RuntimePath('') + '\WindowsMegaProxy.Service.exe', '--cleanup-installation', '', SW_HIDE, ewNoWait, Code) then
      Log('Could not start installation cleanup.');
    if IsUpdateInstall and WizardSilent and (ExpandConstant('{param:REOPEN|1}') <> '0') then
      if not ExecAsOriginalUser(LaunchPath(''), '', '', SW_SHOWNORMAL, ewNoWait, Code) then
        Log('Could not reopen MegaProxy after updating.');
  end;
end;

function InitializeUninstall: Boolean;
var Manager, Service: THandle; Status: TServiceStatus; Attempt, Code: Integer; ImagePath, Executable: String;
begin
  Result := False;
  if IsUserInstall then begin
    if not RegQueryStringValue(HKCU64, 'SOFTWARE\WindowsMegaProxy\UserInstallation', 'HelperPath', Executable) then Exit;
    { Never execute a user-writable helper path with the uninstaller's elevated token. }
    if Pos(Lowercase(ProtectedUserRoot('') + '\'), Lowercase(Executable)) <> 1 then Exit;
    if Pos('\..', Executable) <> 0 then Exit;
    Result := ExecAndLogOutput(Executable, '--prepare-uninstall', '', SW_HIDE, ewWaitUntilTerminated, Code, nil) and (Code = 0);
    Exit;
  end;
  if not RegQueryStringValue(HKLM64, 'SOFTWARE\WindowsMegaProxy', 'HelperPath', ImagePath) then
    RegQueryStringValue(HKLM64, 'SYSTEM\CurrentControlSet\Services\WindowsMegaProxy', 'ImagePath', ImagePath);
  if ImagePath <> '' then begin
    Executable := RemoveQuotes(ImagePath);
    if not ExecAndLogOutput(Executable, '--prepare-uninstall', '', SW_HIDE, ewWaitUntilTerminated, Code, nil) or (Code <> 0) then begin
      MsgBox('Close MegaProxy in all Windows sessions and retry.', mbError, MB_OK);
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
