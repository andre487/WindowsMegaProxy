#include "check-config.iss"
#include "package-config.iss"

[Setup]
AppName=MegaProxy installer checks
AppVersion=1.0
DefaultDirName={tmp}\MegaProxyInstallerChecks
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
OutputDir={#CheckOutputDirectory}
OutputBaseFilename=installer-download-checks

[Code]
#include "Downloads.iss"

procedure Require(Condition: Boolean; Message: String);
begin
  if not Condition then RaiseException(Message);
end;

function InitializeSetup: Boolean;
begin
  Require(IsTemporaryHttpError('Download failed: 504 Gateway Time-out'), '504 must be retryable.');
  Require(IsTemporaryHttpError('Download failed: 503 Service Unavailable'), '503 must be retryable.');
  Require(IsTemporaryHttpError('Download failed: 502'), 'Reasonless HTTP/2 failures must be retryable.');
  Require(not IsTemporaryHttpError('Download failed: 404 Not Found'), '404 must not be retried.');
  Require(not IsTemporaryHttpError('Checksum mismatch: 504abcdef'), 'Checksum failures must not be retried.');
  Require(IsGitHubAssetLocation('https://release-assets.githubusercontent.com/file'), 'Allow the official HTTPS asset host.');
  Require(not IsGitHubAssetLocation('http://release-assets.githubusercontent.com/file'), 'Reject HTTP redirects.');
  Require(not IsGitHubAssetLocation('https://release-assets.githubusercontent.com.evil.invalid/file'), 'Reject lookalike hosts.');
  Require(IsGitHubAssetLocation(GitHubAssetLocation('{#StableCoreApiURL}')), 'Resolve the official asset API without following or logging its redirect.');
  if not SaveStringToFile('{#CheckOutputDirectory}\result.txt', 'passed', False) then
    RaiseException('Could not write the installer checks result.');
  Result := False;
end;
