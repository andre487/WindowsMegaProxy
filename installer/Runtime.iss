type
  TRuntimePackage = record
    Url, Hash: String;
    Extracted: Boolean;
  end;
  TRuntimeFile = record
    PackageIndex: Integer;
    RelativePath, ArchivePath, Hash: String;
  end;
var
  RuntimePackages: array of TRuntimePackage;
  RuntimeFiles: array of TRuntimeFile;
  RuntimeReady: Boolean;

procedure AddRuntimePackage(Url, Hash: String);
var I: Integer;
begin
  I := GetArrayLength(RuntimePackages);
  SetArrayLength(RuntimePackages, I + 1);
  RuntimePackages[I].Url := Url;
  RuntimePackages[I].Hash := Hash;
end;

procedure AddRuntimeFile(PackageIndex: Integer; RelativePath, ArchivePath, Hash: String);
var I: Integer;
begin
  I := GetArrayLength(RuntimeFiles);
  SetArrayLength(RuntimeFiles, I + 1);
  RuntimeFiles[I].PackageIndex := PackageIndex;
  RuntimeFiles[I].RelativePath := RelativePath;
  RuntimeFiles[I].ArchivePath := ArchivePath;
  RuntimeFiles[I].Hash := Hash;
end;

#include "runtime-files.iss"

function RuntimeFileMatches(Path, Hash: String): Boolean;
begin
  Result := False;
  if not FileExists(Path) then Exit;
  try Result := CompareText(GetSHA256OfFile(Path), Hash) = 0;
  except Log('Could not verify cached runtime file: ' + Path); end;
end;

procedure PrepareRuntime(InstalledRoot, LauncherRoot: String);
var I, P: Integer; Source, Target, RelativePath, ArchiveName, Expanded: String;
    Page: TDownloadWizardPage;
begin
  if RuntimeReady then Exit;
  if GetArrayLength(RuntimeFiles) = 0 then InitializeRuntimeFiles;
  Page := nil;
  try
    for I := 0 to GetArrayLength(RuntimeFiles) - 1 do begin
      RelativePath := RuntimeFiles[I].RelativePath;
      if Pos('launcher\', RelativePath) = 1 then begin
        if LauncherRoot = '' then Continue;
        Delete(RelativePath, 1, 9);
        Source := LauncherRoot + '\' + RelativePath;
      end else Source := InstalledRoot + '\' + RelativePath;
      if not RuntimeFileMatches(Source, RuntimeFiles[I].Hash) then begin
        P := RuntimeFiles[I].PackageIndex;
        ArchiveName := 'dotnet-' + IntToStr(P) + '.zip';
        Expanded := ExpandConstant('{tmp}\dotnet-package-') + IntToStr(P);
        if not RuntimePackages[P].Extracted then begin
          Log('Downloading pinned Microsoft .NET runtime package: ' + RuntimePackages[P].Url);
          if Page = nil then Page := CreateDownloadPage('Microsoft .NET', 'Preparing the app-local .NET runtime...', nil);
          Page.Show;
          DownloadCore(Page, RuntimePackages[P].Url, '', '', ArchiveName, RuntimePackages[P].Hash);
          ExtractArchive(ExpandConstant('{tmp}\') + ArchiveName, Expanded, '', True, nil);
          RuntimePackages[P].Extracted := True;
          DeleteFile(ExpandConstant('{tmp}\') + ArchiveName);
        end;
        Source := Expanded + '\' + RuntimeFiles[I].ArchivePath;
        if not RuntimeFileMatches(Source, RuntimeFiles[I].Hash) then
          RaiseException('Microsoft .NET runtime file verification failed.');
      end;
      Target := ExpandConstant('{tmp}\dotnet\') + RuntimeFiles[I].RelativePath;
      if not ForceDirectories(ExtractFileDir(Target)) or not FileCopy(Source, Target, False) then
        RaiseException('Could not stage the app-local .NET runtime.');
    end;
    RuntimeReady := True;
    Log('App-local Microsoft .NET runtime verified and ready.');
  finally if Page <> nil then Page.Hide; end;
end;

procedure CopyRuntime(Destination: String);
var I: Integer; RelativePath, Target: String;
begin
  for I := 0 to GetArrayLength(RuntimeFiles) - 1 do begin
    RelativePath := RuntimeFiles[I].RelativePath;
    if Pos('launcher\', RelativePath) <> 1 then begin
      Target := Destination + '\' + RelativePath;
      if not ForceDirectories(ExtractFileDir(Target)) or
        not FileCopy(ExpandConstant('{tmp}\dotnet\') + RelativePath, Target, False) then
        RaiseException('Could not prepare the installation helper runtime.');
    end;
  end;
end;
