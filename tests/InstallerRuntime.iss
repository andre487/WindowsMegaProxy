#include "check-config.iss"

[Setup]
AppName=MegaProxy runtime checks
AppVersion=1.0
DefaultDirName={tmp}\MegaProxyRuntimeChecks
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
ArchiveExtraction=full
OutputDir={#CheckOutputDirectory}
OutputBaseFilename=installer-runtime-checks

[Files]
Source: "runtime-fixture.zip"; Flags: dontcopy

[Code]
#include "Downloads.iss"
#include "Runtime.iss"

procedure Require(Condition: Boolean; Message: String);
begin
  if not Condition then RaiseException(Message);
end;

function InitializeSetup: Boolean;
var Root, Launcher, Expanded, Hash: String; Failed: Boolean;
begin
  Root := ExpandConstant('{tmp}\existing');
  Launcher := ExpandConstant('{tmp}\launcher');
  Expanded := ExpandConstant('{tmp}\dotnet-package-0');
  ForceDirectories(Root); ForceDirectories(Launcher);
  ExtractTemporaryFile('runtime-fixture.zip');
  ExtractArchive(ExpandConstant('{tmp}\runtime-fixture.zip'), Expanded, '', True, nil);
  Hash := GetSHA256OfFile(Expanded + '\runtimes\win-x64\native\coreclr.dll');
  AddRuntimePackage('https://invalid.example/not-used', '');
  RuntimePackages[0].Extracted := True;
  AddRuntimeFile(0, 'coreclr.dll', 'runtimes\win-x64\native\coreclr.dll', Hash);
  AddRuntimeFile(0, 'launcher\coreclr.dll', 'runtimes\win-x64\native\coreclr.dll', Hash);

  { Missing runtime is materialized from the verified package with preserved archive paths. }
  PrepareRuntime(Root, Launcher);
  Require(RuntimeFileMatches(ExpandConstant('{tmp}\dotnet\coreclr.dll'), Hash), 'Missing runtime was not staged.');
  Require(RuntimeFileMatches(ExpandConstant('{tmp}\dotnet\launcher\coreclr.dll'), Hash), 'Personal launcher runtime was not staged.');
  CopyRuntime(Root);
  Require(RuntimeFileMatches(Root + '\coreclr.dll', Hash), 'Repair/boot helper runtime was not copied.');
  FileCopy(Root + '\coreclr.dll', Launcher + '\coreclr.dll', False);

  { Matching local files must work without any package or network access. }
  DeleteFile(Expanded + '\runtimes\win-x64\native\coreclr.dll');
  RuntimeReady := False;
  PrepareRuntime(Root, Launcher);
  Require(RuntimeReady, 'Matching runtime was not reused.');

  { A different/corrupt runtime cannot be accepted as the pinned version. }
  SaveStringToFile(Root + '\coreclr.dll', 'wrong version', False);
  RuntimeReady := False;
  Failed := False;
  try PrepareRuntime(Root, ''); except Failed := True; end;
  Require(Failed and not RuntimeReady, 'Mismatched runtime was accepted.');
  Require(not RuntimeFileMatches(Root + '\coreclr.dll', Hash), 'Failed preparation replaced the existing runtime.');
  Require(not RuntimeFileMatches(Root + '\missing.dll', Hash), 'Missing files must not match.');
  Require(SaveStringToFile('{#CheckOutputDirectory}\runtime-result.txt', 'passed', False), 'Could not write runtime check result.');
  Result := False;
end;
