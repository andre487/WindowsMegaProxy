# Local development secrets (Windows)

Keep local credentials outside the checkout, in a private per-user directory:

```text
%LOCALAPPDATA%\MegaProxy\DeveloperSecrets\WindowsMegaProxy\
    release-bot-token.txt
    openai-api-key.txt
    update-signing-key.txt
    virus-total.txt
    code-signing\
        code-signing.pfx
        code-signing-password.txt
        code-signing-certificate.cer
        code-signing-thumbprint.txt
```

Each file contains only its credential, on one line, without quotes or an
environment-variable name. These are developer credentials, not proxy profile
settings: do not put them in MegaProxy.json or the service's ProgramData folder.
The application does not read this directory. Installer builds automatically
read the two files in `code-signing` when signing credentials are not supplied
through the environment.

Create a **new** directory and restrict access before writing credentials.
Run these commands in a normal PowerShell session as your own user:

```powershell
$secretDir = Join-Path $env:LOCALAPPDATA 'MegaProxy\DeveloperSecrets\WindowsMegaProxy'
New-Item -ItemType Directory -Path $secretDir -ErrorAction Stop | Out-Null
$userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
icacls $secretDir /inheritance:r /grant:r "*${userSid}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F'
if ($LASTEXITCODE -ne 0) { throw 'Could not protect the secrets directory' }
```

Do not continue if directory creation or permission changes fail. Use a password
manager as the durable backup; these files are plaintext protected by Windows
permissions, not encrypted. Administrators and programs running as your account
can still access them.

## GitHub release bot token

Create a [fine-grained personal access token](https://github.com/settings/personal-access-tokens/new):

- Resource owner: the repository owner.
- Repository access: only `andre487/WindowsMegaProxy`.
- Repository permissions: **Contents: Read and write**, **Pull requests: Read
  and write**, **Actions: Read** to inspect release CI. Metadata read access is included automatically.
- Choose an expiration and record a renewal reminder.

Save the token in `release-bot-token.txt`. The preparation action uses the
repository secret `RELEASE_BOT_TOKEN`; local preparation uses `GH_TOKEN`.
It creates a branch and PR, waits for full CI, squash merges the checked PR and
creates its release tag. It does not edit workflows or manage repository secrets.
An organization may require approval of the token.

For ordinary local GitHub CLI work, prefer `gh auth login` with Windows secure
credential storage. You do not need to export or duplicate that login's token.
The publishing action uses GitHub's automatic `GITHUB_TOKEN`; do not create a
separate file for it.

## OpenAI API key

Create a dedicated project in the [OpenAI API platform](https://platform.openai.com/),
then create a project key on its [API keys page](https://platform.openai.com/api-keys).
Save it in `openai-api-key.txt`. Enable API billing/credits and configure project
usage controls. Use a user-owned project key for local development; a dedicated
project service account is preferable for CI. An organization Admin API key or
a ChatGPT browser-session token is not needed.

`Prepare release` uses `OPENAI_API_KEY` to generate English/Russian notes
from changes since the last complete published release. With no release it
generates first-release notes. The optional Actions variable `OPENAI_RELEASE_MODEL`
selects the model (default: `gpt-5-mini`); this setting is not a secret.

See [official OpenAI guidance on API keys and service accounts](https://developers.openai.com/api/docs/guides/api-keys).

## Update signing key

This key is generated locally, not issued by GitHub, Microsoft or OpenAI.
The required format is base64 of a raw 32-byte Ed25519 private key.
After `setup.cmd`, run this from the repository root. It creates the file
exclusively and refuses to overwrite an existing key; it does not print the key:

```powershell
uv run --locked --project scripts python -c "import base64, os; from pathlib import Path; from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey; target = Path(os.environ['LOCALAPPDATA']) / 'MegaProxy/DeveloperSecrets/WindowsMegaProxy/update-signing-key.txt'; key = Ed25519PrivateKey.generate(); stream = target.open('x', encoding='ascii'); stream.write(base64.b64encode(key.private_bytes_raw()).decode()); stream.close()"
```

Back it up in a password manager before using it for production. Keep the same
production key between releases. Use a separate key for local signing experiments;
ordinary `build-installer.cmd --preview` already uses a disposable key and needs
none of these files. Manifest signing is separate from Windows Authenticode.

## Windows Authenticode certificate

Back up `code-signing/code-signing.pfx` as a Bitwarden attachment and store
`code-signing/code-signing-password.txt` as its password. The PFX contains the
private key and is encrypted with that password. Keep both: GitHub cannot
export stored secret values for recovery. The certificate expires in November
2036. The CER and SHA-256 fingerprint files contain public information only.

GitHub release builds use `WINDOWS_SIGNING_PFX` (base64 of the PFX bytes) and
`WINDOWS_SIGNING_PASSWORD`. The build verifies the certificate against the public
copy in `assets/authenticode-certificate.pem` and refuses unsigned releases or
an unexpected signing identity. Do not regenerate this identity for each build.

The certificate is self-signed and does not establish Windows publisher trust.
The build does not install it into a Windows certificate store. Packaged
MegaProxy EXE/DLL files, Setup, the uninstaller and temporary Setup copies are
signed; existing upstream dependencies retain their original signatures/hashes.
Release signatures require a SHA-256 RFC 3161 timestamp. The temporary signing
key file is restricted to the current user and SYSTEM, removed after packaging,
and never passed to GitHub artifact uploads.

Preview builds use the permanent certificate when available locally. On CI or
machines without the private key, previews use a disposable certificate, without
timestamping. Pull-request CI never receives the permanent signing secrets.

## Use files locally

The VirusTotal API key in `virus-total.txt` is used by the release workflow as
`VIRUSTOTAL_API_KEY`. It scans the native installer and `*-app.zip`, publishes
Markdown/JSON reports and blocks publication on detections or incomplete scans.
Reports are bound to SHA-256 hashes and reused only for identical files on retry.
The build scripts do not read this credential automatically.

Load only what a command needs into the current PowerShell process:

```powershell
$secretDir = Join-Path $env:LOCALAPPDATA 'MegaProxy\DeveloperSecrets\WindowsMegaProxy'
$env:UPDATE_SIGNING_KEY = (Get-Content -LiteralPath "$secretDir\update-signing-key.txt" -Raw).Trim()
try {
    .\build-installer.cmd
} finally {
    Remove-Item Env:UPDATE_SIGNING_KEY -ErrorAction SilentlyContinue
}
```

For local release preparation, load `release-bot-token.txt` into `GH_TOKEN` in
the same way; `prepare_release.py` also requires `RELEASE_VERSION` and
`OPENAI_API_KEY` in the environment. Notes are generated automatically.
Do not use `setx` to persist credentials for all future processes.

## Upload to GitHub Actions

Use an existing `gh auth login` session with permission to manage repository
secrets. Do this **before** exporting the limited bot token as `GH_TOKEN`, since
that token deliberately cannot manage secrets. The commands read values from
stdin without placing their contents in command history or arguments:

```powershell
$secretDir = Join-Path $env:LOCALAPPDATA 'MegaProxy\DeveloperSecrets\WindowsMegaProxy'
Get-Content -LiteralPath "$secretDir\release-bot-token.txt" -Raw | gh secret set RELEASE_BOT_TOKEN --repo andre487/WindowsMegaProxy
Get-Content -LiteralPath "$secretDir\update-signing-key.txt" -Raw | gh secret set UPDATE_SIGNING_KEY --repo andre487/WindowsMegaProxy
Get-Content -LiteralPath "$secretDir\virus-total.txt" -Raw | gh secret set VIRUSTOTAL_API_KEY --repo andre487/WindowsMegaProxy
```

Upload the CI service-account key for note generation as
`OPENAI_API_KEY` using the same stdin pattern. Never add secret files to a
release, Git commit, build artifact, issue, screenshot or log.
