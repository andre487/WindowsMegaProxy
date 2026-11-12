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

Interactive setup starts with offline license texts and explicit acceptance.
New installations default to the current user; upgrades retain their existing scope.
Silent updates and uninstallation do not show the acceptance page.

Only one installation scope is registered for the current user at a time. Uninstall
before changing between system and personal installation; settings are retained.
Burn's cached bundle provides Repair and Uninstall in Windows Settings.
The one-time Inno transition is described in the [installation guide](../docs/en/installation.md).
Setup refuses existing Inno registrations or uninstaller files. Legacy switches
are not accepted; in-place updates and repair are supported between Burn releases.

Silent setup supports `-quiet Scope=PerMachine Mode=service` or
`-quiet Scope=PerUser Mode=login AutomaticUpdates=false`. Omitting mode or update
preferences preserves existing choices. `-uninstall -quiet` removes the selected
installed bundle. Installation engine logs are in `%ProgramData%/MegaProxy.Setup`.
The engine and outer bundle are signed separately; all owned payload binaries are signed.

## Deployment and dependencies

Windows 10/11 x64 is required. Scope, startup modes, data retention and removal
are described in the [installation guide](../docs/en/installation.md).
System files use `Program Files/WindowsMegaProxy`; personal protected runtimes
use `Program Files/WindowsMegaProxy.Users/<SID>`. The personal launcher lives in
the user's application directory. DEV has separate files and registrations.

Both [native core DLLs](../native/core/README.md) are bundled. There is no global
H3 toggle or on-demand core download. Legacy `http3Enabled` is ignored on import.
Windows OpenSSH Client is mandatory; failure to install it stops setup. Wintun
comes from the upstream core dependencies. No WinGet or system-wide .NET is needed.

UI, service and launcher use folder deployment. The pinned .NET version is in
[Directory.Build.props](../Directory.Build.props). DEV output includes runtime
files, but release installers and component ZIPs omit them. Setup downloads exact
Microsoft runtime packages from NuGet.org, checks archive and file hashes, and
reuses matching installed files. UI and service share the desktop runtime; the
personal launcher has its own runtime. Downloads finish before stopping the app.

Boot-time service updates stage verified files and runtime packages before restart.
Windows Session Manager replaces them before services start; the new service
verifies the payload. A protected boot task recovers incomplete replacement using
the verified installer. Queued replacements require a restart before another
update, mode change, repair or removal. Restoring the tray client does not wait
for temporary-file cleanup or Burn registration. Other updates use the installer.

Global settings use machine-scope DPAPI and SYSTEM/administrator-only storage.
Personal settings are separate. IPC v3 updates UI and service together; legacy
configuration migration can read v2. `cleanup-dev.cmd` removes DEV registrations
and binaries while retaining settings and leaving production untouched.

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
   and SHA256SUMS. VirusTotal scans the signed installer and application ZIP. Up to
   ten malicious/suspicious verdicts per file are allowed; more detections, API
   errors or incomplete results block publication.
   A manual dispatch with an existing tag resumes the release. Published assets
   are never replaced; repeated publication verifies the original files and
   updates release notes without changing package bytes.

The source archive also includes the linked Go module sources, Go runtime/library
sources and checksum-pinned WiX source. `third-party/README.txt` explains their layout
and local module replacement; each component retains its own license.

Original packages are checkpointed for 14 days so retries can resume without
rebuilding files or changing hashes. Complete draft/published releases can also
restore their original packages; only unpublished draft files may be replaced.
Release notes include detection counts, report links and a false-positive caveat
when needed. Raw reports are not release assets or repository files. See
[antivirus policy and manual checks](../docs/ANTIVIRUS.md).

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
User-facing checking, download and installation choices are documented in
[Application updates](../docs/en/updates.md). DEV never installs production updates.

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

Development downloads retry HTTP 5xx with backoff. Official GitHub assets have
API and checksum-verified mirror fallbacks; upstream core executables downloaded
by development tools are test servers, not installed application components.
See `scripts/dependencies.py` and `scripts/common.py` for current sources and download handling.
