# Installer and release builds

Credential setup and local file locations: [local development secrets](LOCAL-SECRETS.md).

Run `setup.cmd`, then `build-installer.cmd --preview`. Outputs are in
`artifacts/release-preview`. Preview manifests use disposable keys and have
`preview: true`; do not distribute them as trusted updates.

The installer targets Windows 10/11 x64 and prepares machine-wide dependencies
with one command: `winget install SagerNet.sing-box Microsoft.DotNet.DesktopRuntime.10
--exact --source winget --scope machine --architecture x64 --silent
--accept-source-agreements --accept-package-agreements --disable-interactivity`.
App Installer/WinGet must be available in the installing user's session; the
WinGet CLI is not run as SYSTEM. Package hash and malware checks remain enabled.
The application and service resolve sing-box through its machine-wide WinGet
registration, without relying on a user's PATH or user-writable executable.

HTTP/3 is disabled by default. When enabled and requested by a profile, the
client first checks the WinGet core version. A compatible 1.15+ core is reused;
otherwise it asks before downloading the pinned `1.15.0-alpha.13` core. A signed
on-demand installer checks archive and extracted file hashes and installs it
under Program Files. Headless service starts never download it implicitly.
MegaProxy.json stores the setting as `windows.http3Enabled`. Import asks before
turning it on; declining still imports the other selected settings. Absent
options preserve local values. Background subscription refreshes cannot turn
HTTP/3 on without confirmation.
Neither core nor .NET is embedded in the installer/application ZIP. OpenSSH
Client remains a Windows capability, selected by default with an opt-out;
WinGet's OpenSSH Preview package is not substituted for the Windows client.
Python, uv and the .NET SDK remain development-only tools downloaded into the
checkout. Dependency preparation completes before switching the application mode.

The installer defaults to manual launch on a new installation and offers three
operating modes: manual, elevated sign-in startup, or a Windows service. It
preserves the chosen mode on upgrade. Silent installations can select
`/MODE=manual`, `/MODE=login` or `/MODE=service`. Manual and login modes register
no Windows service; login mode uses a highest-privilege interactive logon task
for the administrator who selected it, without storing a password. The settings
page can change the mode with administrator approval and restarts the interface.
Exiting a standalone host stops its core; service-mode clients can close while
the shared core continues running.

Each installation uses a separate version directory. Existing clients close
gracefully; the service switches to the new directory. If startup fails, its
previous executable is restored. Global configuration, personal configuration
and the shared Wintun driver survive uninstall. DEV is untouched.
Old version directories are retained for rollback.

Global settings use machine-scope DPAPI and a directory restricted to SYSTEM
and administrators so either elevated desktop hosts or the service can use
them. Legacy service-identity DPAPI settings are migrated through authenticated
IPC before stopping/removing the old service. DEV uses an independent machine-scope
store, migrating its legacy user settings on first launch. `cleanup-dev.cmd`
preserves settings and removes DEV services, scheduled tasks, Run entries,
operating mode registration and installed binaries. Production is untouched.

IPC v3 carries rule names and enabled state. Update the interface and service
together; the installation helper can still read a v2 service during legacy
configuration migration.

## Release

The `Prepare release` action accepts only a version. It generates English/Russian
notes through OpenAI and opens a reviewable PR. The baseline is the latest published
stable release with the complete package, manifest and VirusTotal asset set; empty
or partial releases are skipped. With no usable release, notes describe the first
release using the complete repository diff. Large diffs are summarized in bounded
parts before note generation, without truncating the evidence. Set `RELEASE_BOT_TOKEN` to a fine-grained token with repository
contents and pull-request write access; a bot token lets the new PR trigger CI.
Set `OPENAI_API_KEY`; optionally set the Actions variable `OPENAI_RELEASE_MODEL`
(default: `gpt-5-mini`, with a same-named secret also supported). Generated notes
remain visible in the PR. The explicitly requested workflow waits for full CI,
squash merges the unchanged release PR and tags the merged commit. Tag creation
starts publication. Failed, skipped or cancelled checks stop finalization.
If main advances, rebase the release PR and restart preparation to check the new
base. Retries reuse an existing release PR and never move an existing tag.

1. Run `Prepare release` on main with version `X.Y.Z`. It updates both versions
   in `Directory.Build.props` and commits generated English/Russian notes as
   `release-notes/vX.Y.Z.md` in the preparation PR.
2. The workflow waits for the release PR CI, squash merges it and creates
   `vX.Y.Z`. The tag points to the merged commit, which must also pass main CI.
3. The Release action builds and publishes the installer, application archive,
   separate stable/H3 core archives, application source, signed update manifest
   and SHA256SUMS, plus VirusTotal reports for the installer and application ZIP.
   A manual dispatch with an existing tag resumes the release. Published assets
   are never replaced; repeated publication verifies the original files and
   refreshes the VirusTotal section without duplicating it.

Configure `VIRUSTOTAL_API_KEY` as described in [local development secrets](LOCAL-SECRETS.md).
Incomplete scans and malicious/suspicious verdicts block publication. Markdown
and JSON reports are preserved as Actions artifacts even if scanning fails.
Original packages are checkpointed for 14 days so rerunning the same workflow
can finish a partial scan without rebuilding files or changing their hashes.
Complete draft/published releases can also restore their original packages;
an incomplete draft may replace its unpublished files. Reports for identical
SHA-256 hashes are reused if clean; detected files are rescanned on retry.
When recovering a previously published release that
has no report assets, reports stay in Actions and their links are added to the
release notes, since immutable assets cannot be added after publication.

Configure the repository secret `UPDATE_SIGNING_KEY` before releasing: base64
of a 32-byte Ed25519 private key. Generate it with a trusted tool, store it in a
password manager and upload with `gh secret set UPDATE_SIGNING_KEY`; never put
it in the repository or build logs. Keep the same key between releases.
The release build refuses to run without it or from a dirty checkout.

`update-manifest.sig` signs the exact UTF-8 bytes of `update-manifest.json`.
`update-public-key.txt` exports the verification key, and `signingKeyId` is its
SHA-256. The updater pins that public key in its trusted installed code and does
not trust a key downloaded alongside the manifest. The manifest
records version, commit, architecture, runtime requirement, component URLs,
archive sizes/hashes and individual archive entry hashes. App and service update
together; each core is a separate component with installation-relative paths.
The application checks GitHub Releases once a day while running (including in
the tray), verifies the manifest against its pinned Ed25519 key and downloads
one installer. Settings provide a manual check, an automatic-update opt-out and
an install action. Ignored notifications repeat weekly or for a newer release;
opening the update page acknowledges the ready version. The protected helper
verifies and stages the installer before elevation can execute it, preserves
installation scope/mode and restarts the client. Old, partial and installed
update downloads are removed. DEV checks can show releases but never install
production updates over a development build. The installer also offers an
automatic-update checkbox, selected by default.

Packaged MegaProxy EXE/DLL files and Inno Setup's installer, uninstaller and
temporary Setup copies are Authenticode-signed. Release builds require the
permanent certificate pinned in `assets/authenticode-certificate.pem` and a
SHA-256 timestamp; credentials are documented in [LOCAL-SECRETS.md](LOCAL-SECRETS.md).
This certificate is self-signed, so Windows does not automatically trust its
publisher. Ed25519 update-manifest signing remains separate. Preview CI uses
a disposable certificate without access to production signing secrets.
Current packaging automation also signs rebuilt older tags; an unsigned
unpublished checkpoint is rebuilt, while already-published assets remain intact.
CI compiles the installer
and tests silent installation, repair and uninstall on a disposable runner,
without activating TUN. Interactive installation, failure rollback and driver
activation still need testing in a disposable Windows VM before public release.

OpenSSH uses the Windows capability installer; Wintun is provided by the upstream
core. Dependency installation runs from user-session Setup, never the service.
See [WinGet's system-context limitation](https://learn.microsoft.com/windows/package-manager/winget/troubleshooting).

Development/release downloads retry HTTP 5xx four times with 1, 2 and 4 second
backoff delays, logging a bounded error body and diagnostic headers. After
exhausted retries on a GitHub release URL, build tools can retrieve the same
asset through the official GitHub REST API with the original pinned hash.
The on-demand HTTP/3 installer applies the same retry policy to core downloads. If the release URL and
mirrors keep failing, it resolves an API redirect using Windows WinHTTP, accepts
only the official HTTPS asset hosts, then downloads and hash-checks with Inno.
Download-rule checks are a separate test executable and are not bundled.

Core downloads prefer GitHub, then dl.sing-box.org for stable builds, then ghfast.top,
with the official GitHub asset API as the final fallback. Mirrors are third-party
services; every archive must match the original pinned SHA-256 before extraction.
Alpha builds skip dl.sing-box.org because it only carries stable archives.
