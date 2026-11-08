function IsTemporaryHttpError(ErrorText: String): Boolean;
var Status: Integer;
begin
  Result := False;
  for Status := 500 to 599 do
    if Pos(': ' + IntToStr(Status) + ' ', ErrorText + ' ') > 0 then begin
      Result := True;
      Exit;
    end;
end;

function IsGitHubAssetLocation(Url: String): Boolean;
begin
  Url := Lowercase(Url);
  Result := (Pos('https://release-assets.githubusercontent.com/', Url) = 1) or
    (Pos('https://objects.githubusercontent.com/', Url) = 1);
end;

function GitHubAssetLocation(ApiUrl: String): String;
var Request: Variant; Status: Integer;
begin
  if Pos('https://api.github.com/repos/SagerNet/sing-box/releases/assets/', ApiUrl) <> 1 then
    RaiseException('Untrusted GitHub asset API endpoint.');
  Request := CreateOleObject('WinHttp.WinHttpRequest.5.1');
  Request.SetTimeouts(10000, 10000, 30000, 30000);
  Request.Open('HEAD', ApiUrl, False);
  Request.Option[6] := False;
  Request.SetRequestHeader('Accept', 'application/octet-stream');
  Request.Send;
  Status := Request.Status;
  if (Status <> 301) and (Status <> 302) and (Status <> 303) and (Status <> 307) and (Status <> 308) then
    RaiseException('GitHub API did not return a download redirect (HTTP ' + IntToStr(Status) + ').');
  Result := Request.GetResponseHeader('Location');
  Result := Trim(Result);
  if not IsGitHubAssetLocation(Result) then
    RaiseException('GitHub returned an untrusted download redirect.');
end;

procedure DownloadCore(Page: TDownloadWizardPage; Url, ApiUrl, BaseName, Hash: String);
var Attempt, Delay: Integer; ErrorText: String;
begin
  Delay := 1000;
  for Attempt := 1 to 4 do begin
    Page.Clear;
    Page.Add(Url, BaseName, Hash);
    try
      Page.Download;
      Exit;
    except
      ErrorText := GetExceptionMessage;
      Log(BaseName + ': ' + ErrorText);
      if not IsTemporaryHttpError(ErrorText) then RaiseException(ErrorText);
      if Attempt < 4 then begin
        Log('Retrying ' + BaseName + ' in ' + IntToStr(Delay div 1000) + ' second(s).');
        Sleep(Delay);
        Delay := Delay * 2;
      end;
    end;
  end;
  Log('Using the official GitHub API for ' + BaseName + '; the pinned hash is unchanged.');
  Page.Clear;
  Page.Add(GitHubAssetLocation(ApiUrl), BaseName, Hash);
  Page.Download;
end;
