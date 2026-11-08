# Installer and release builds

Credential setup and local file locations: [local development secrets](LOCAL-SECRETS.md).

Run `setup.cmd`, then `build-installer.cmd --preview`. Outputs are in
`artifacts/release-preview`. Preview manifests use disposable keys and have
`preview: true`; do not distribute them as trusted updates.

The WiX 7 + Burn installer targets Windows 10/11 x64, requires administrator approval,
and installs the UI and mode-management helper into Program Files. Both pinned official
sing-box packages, including libcronet and embedded signed Wintun, are downloaded
from SagerNet's official GitHub releases during installation and application
updates. The installer pins and checks SHA-256 for each archive and verifies the
extracted executable and library before activating the new version. Downloads
finish before the existing installation is switched; failure leaves the old
runtime active. The installer and application ZIP do not embed either core.
The installer downloads a checksum-verified .NET 10 Desktop Runtime from
Microsoft only when the required version is missing. SSH support uses the
Windows OpenSSH Client capability and is selected by default; users can opt out.
Python, uv and the .NET SDK are build
tools only. Prerequisites are prepared before changing an existing service.

The installer defaults to manual launch on a new installation and offers three
operating modes: manual, elevated sign-in startup, or a Windows service. It
preserves the chosen mode on upgrade. Silent installations use `/quiet /norestart /permachine` (or
`/peruser`), with `Mode=manual`, `Mode=login` or `Mode=service`. Use `/repair`
for an existing installation and `/uninstall` to remove it. `InstallSSH=0`
opts out of OpenSSH, `AutomaticUpdates=false` opts out of automatic updates,
and `DesktopShortcut=1` creates a desktop shortcut. Mode and update preferences
are preserved when their variables are omitted. New installations default to
manual mode with automatic updates enabled. Manual and login modes register
no Windows service; login mode uses a highest-privilege interactive logon task
for the administrator who selected it, without storing a password. The settings
page can change the mode with administrator approval and restarts the interface.
Exiting a standalone host stops its core; service-mode clients can close while
the shared core continues running.

Only one installation scope is active at a time. To change scope, uninstall
and install again; global and personal configuration are retained separately.
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

## Installer implementation

The native Burn engine hosts a small WinForms bootstrapper application on the
.NET Framework 4.x already included in supported Windows versions. It does not
require .NET 10 to display setup. Burn downloads the checksum-verified Desktop
Runtime, installs a small dual-scope registration MSI,
and starts the managed setup engine elevated. The MSI gives Burn a native choice
of per-user or per-machine registration. The custom BA explicitly honors silent
scope selection; the stock WiX 7 BA does not. The engine deploys immutable version
folders and invokes the existing service helper. Core downloads and OpenSSH
preparation finish before that helper switches the active runtime. Failed
configuration keeps the previous runtime; Burn rollback can restore the previous
helper and settings. Setup logs are retained under the protected installation
root's `installer-logs` directory. Rollback snapshots are restricted to SYSTEM
and administrators, separately from the readable runtime files. User-writable launcher and shortcut operations
use the administrator's filtered token when UAC is enabled.

Existing Inno registrations are retired only after successful configuration, while
configuration and previous version directories are preserved. Approval must use
the same administrator account that started setup. The former Inno definition remains in Git history; release builds do not use
Inno Setup.

Build tools are downloaded into `.tools` and `artifacts/wix-cache`; Visual Studio
is not required. The build explicitly accepts the [WiX 7 OSMF EULA](https://docs.firegiant.com/wix/osmf/)
as authorized for this noncommercial project. Reassess the fee terms if the
project becomes revenue-generating. WiX runtime/localization license text is included
in `licenses/WiX-LICENSE.txt`.

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
automatic-update preference, enabled by default on a new installation.

The Windows EXE installer is currently not Authenticode-signed. Manifest signing
does not replace a Windows code-signing certificate. CI compiles the installer
and tests silent installation, repair and uninstall on a disposable runner,
without activating TUN. Interactive installation, failure rollback and driver
activation still need testing in a disposable Windows VM before public release.
