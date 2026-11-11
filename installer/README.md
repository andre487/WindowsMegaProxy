# Installer and release builds

User downloads and setup instructions: [English](../docs/en/installation.md) /
[Русский](../docs/ru/installation.md).

Releases from 0.1.1 include
[`WindowsMegaProxy-setup.exe`](https://github.com/andre487/WindowsMegaProxy/releases/latest/download/WindowsMegaProxy-setup.exe).
Publication copies this permanent-name asset from the installer already verified
against the signed manifest; it has the same signature and SHA-256 as the versioned
Setup. Existing manifests, checksums and auto-update URLs keep versioned names.
The alias is uploaded while the release is still a draft, before publication.
Published-release retries do not replace it or other published assets.

Credential setup and local file locations: [local development secrets](LOCAL-SECRETS.md).

Run `setup.cmd`, then `build-installer.cmd --preview`. Outputs are in
`artifacts/release-preview`. Preview manifests use disposable keys and have
`preview: true`; do not distribute them as trusted updates.

The installer uses WiX 7 and Burn with a C# interface that follows the Windows
light/dark setting and display scaling. Bootstrap code runs on the .NET Framework
already included in supported Windows versions; it does not need .NET 10 installed
before opening. `src/WindowsMegaProxy.SetupUI` owns the interface and Burn lifecycle;
`src/WindowsMegaProxy.Setup` prepares dependencies and performs protected file replacement.
WiX tools are downloaded into `.tools/wix`; the build accepts the project's agreed
WiX 7 OSMF terms. No Visual Studio IDE is required.

Only one installation scope is registered for the current user at a time. Uninstall
before changing between system and personal installation; settings are retained.
Burn's cached bundle provides Repair and Uninstall in Windows Settings. Existing
Inno installations are migrated without calling their destructive uninstaller.
The new bundle accepts released clients' Inno-style update switches, including
`/BOOTUPDATE=1`; this path stages files without installing or registering the bundle.
The normal post-reboot recovery step completes registration.

Silent setup supports `-quiet Scope=PerMachine Mode=service` or
`-quiet Scope=PerUser Mode=login AutomaticUpdates=false`. Omitting mode or update
preferences preserves existing choices. `-uninstall -quiet` removes the selected
installed bundle. Installation engine logs are in `%ProgramData%/MegaProxy.Setup`.
The engine and outer bundle are signed separately; all owned payload binaries are signed.

The installer targets Windows 10/11 x64. UI, service and current-user launcher
use self-contained folder deployment with .NET 10.0.14 pinned in
Directory.Build.props. No system-wide .NET installation or WinGet is required.
Both native core DLLs are bundled: stable 1.14.4 and experimental
1.15.0-alpha.13. Their dependencies are pinned at build time;
the application selects the core from the profile transport automatically.
There is no global H3 toggle or on-demand core download. Legacy http3Enabled
options are ignored and omitted on export. Other absent import options keep
existing values.

Windows OpenSSH Client is a mandatory Windows capability. Setup installs it
when missing; failure stops setup with an error. Wintun is provided by the
upstream cores. Python, uv and the .NET SDK remain development-only downloads
inside the checkout. DEV publish outputs retain the runtime as regular files.
Release installers and update ZIPs omit those files. Setup downloads the exact
Microsoft runtime packages from NuGet.org, verifies the pinned archive SHA-256 and
each file hash, and installs them locally; matching installed files are reused
without a download. Downloads complete before stopping the installed application.
No single-file extraction is used. UI and service share the same desktop
runtime files. The unprivileged personal-install launcher has its own runtime
in the user's application directory. Updating the pinned local .NET version requires an
application update. Reboot updates stage and verify the runtime before restarting;
boot-time replacement does not require a network connection.

The installer defaults to manual launch on a new installation and offers three
operating modes: manual, elevated sign-in startup, or a Windows service. It
preserves the chosen mode on upgrade. Silent installations can select
`/MODE=manual`, `/MODE=login` or `/MODE=service`. Manual and login modes register
no Windows service; login mode uses a highest-privilege interactive logon task
for the administrator who selected it, without storing a password. The settings
page can change the mode with administrator approval and restarts the interface.
Exiting a standalone host stops its core; service-mode clients can close while
the shared core continues running.

System installations always use `Program Files/WindowsMegaProxy`; protected
personal runtimes use `Program Files/WindowsMegaProxy.Users/<SID>`. Setup stops
the service and closes clients before replacing files. Interrupted preparation
restores the previous operating mode. Successful upgrades remove legacy
`versions` directories after the old update helper releases them. Global and
personal configuration and the shared Wintun driver survive uninstall. DEV
uses its separate fixed directory and registrations.

Service-mode updates scheduled for a restart are extracted and verified against
the signed file manifest before restarting. Windows Session Manager replaces the
files through `MoveFileEx` before services start; the service stays automatic.
The new service verifies the payload before serving clients. A protected boot
task checks completion and uses the verified installer to recover an incomplete
replacement. Queued replacements require a restart before another update,
mode change, repair or uninstall. Normal and current-user updates retain the
installer-based workflow. A client waiting for an active installer displays
“Update in progress” and reconnects automatically.
The first upgrade from a client without this mechanism still uses its legacy
updater; subsequent restart updates use early-boot replacement.

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
stable release with the complete package and signed manifest asset set; empty
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
   and SHA256SUMS. VirusTotal is a separate manual diagnostic, not a release gate.
   A manual dispatch with an existing tag resumes the release. Published assets
   are never replaced; repeated publication verifies the original files and
   updates release notes without changing package bytes.

Original packages are checkpointed for 14 days so retries can resume without
rebuilding files or changing hashes. Complete draft/published releases can also
restore their original packages; only unpublished draft files may be replaced.
Antivirus reports are never required, restored, uploaded or published by release
automation. See [antivirus notes and manual checks](../docs/ANTIVIRUS.md).

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
archive sizes/hashes and installed-file hashes. Runtime file entries carry
`downloaded: true`: they are absent from ZIPs but included in the signed inventory
so existing boot-update workers also verify and replace them. App and service update
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
automatic-update selector; a new installation enables checks by default and an upgrade preserves the existing preference.

Packaged MegaProxy EXE/DLL files, the registration MSI, and both the Burn engine
and final installer bundle are Authenticode-signed. Release builds require the
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

OpenSSH uses the Windows capability installer; Wintun is provided by the bundled
cores. Setup does not install dependencies through a package manager.

Development/release downloads retry HTTP 5xx four times with 1, 2 and 4 second
backoff delays, logging a bounded error body and diagnostic headers. After
exhausted retries on a GitHub release URL, build tools can retrieve the same
asset through the official GitHub REST API with the original pinned hash.
Download-rule checks are a separate test executable and are not bundled.

Core downloads prefer GitHub, then dl.sing-box.org for stable builds, then ghfast.top,
with the official GitHub asset API as the final fallback. Mirrors are third-party
services; every archive must match the original pinned SHA-256 before extraction.
Alpha builds skip dl.sing-box.org because it only carries stable archives.
