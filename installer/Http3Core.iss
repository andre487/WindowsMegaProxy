#include "package-config.iss"

[Setup]
AppName=MegaProxy HTTP/3 core
AppVersion={#AppVersion}
DefaultDirName={commonpf}\WindowsMegaProxy.Dependencies\sing-box-h3\{#H3CoreVersion}
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
Uninstallable=no
CreateUninstallRegKey=no
OutputDir={#PayloadDirectory}
OutputBaseFilename=WindowsMegaProxy.Http3Core
Compression=lzma2
SetupLogging=yes
ArchiveExtraction=full
SetupIconFile={#RepositoryRoot}\src\WindowsMegaProxy\Assets\app.ico

[Files]
Source: "{tmp}\h3-core\{#H3CoreDirectory}\sing-box.exe"; DestDir: "{app}"; Flags: external ignoreversion
Source: "{tmp}\h3-core\{#H3CoreDirectory}\libcronet.dll"; DestDir: "{app}"; Flags: external ignoreversion
Source: "{tmp}\h3-core\{#H3CoreDirectory}\LICENSE"; DestDir: "{app}"; Flags: external ignoreversion

[Code]
#include "Downloads.iss"

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Download: TDownloadWizardPage;
begin
  Result := '';
  try
    Download := CreateDownloadPage('HTTP/3', 'sing-box {#H3CoreVersion}', nil);
    try
      Download.Show;
      DownloadCore(Download, '{#H3CoreURL}', '{#H3CoreApiURL}', '{#H3CoreMirrors}', 'sing-box-h3.zip', '{#H3CoreSHA256}');
    finally
      Download.Hide;
      Download.Free;
    end;
    ExtractArchive(ExpandConstant('{tmp}\sing-box-h3.zip'), ExpandConstant('{tmp}\h3-core'), '', True, nil);
    if (GetSHA256OfFile(ExpandConstant('{tmp}\h3-core\{#H3CoreDirectory}\sing-box.exe')) <> '{#H3CoreExecutableSHA256}') or
       (GetSHA256OfFile(ExpandConstant('{tmp}\h3-core\{#H3CoreDirectory}\libcronet.dll')) <> '{#H3CoreCronetSHA256}') then
      RaiseException('The HTTP/3 core checksum is invalid.');
  except
    Result := GetExceptionMessage;
  end;
end;
