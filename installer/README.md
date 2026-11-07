# Installer and release builds

Credential setup and local file locations: [local development secrets](LOCAL-SECRETS.md).

Run `setup.cmd`, then `build-installer.cmd --preview`. Outputs are in
`artifacts/release-preview`. Preview manifests use disposable keys and have
`preview: true`; do not distribute them as trusted updates.

The installer targets Windows 10/11 x64, requires one administrator approval,
and installs the shared service and UI into Program Files. Both pinned official
sing-box packages, including libcronet and embedded signed Wintun, are bundled.
The installer downloads a checksum-verified .NET 10 Desktop Runtime from
Microsoft only when the required version is missing. SSH support uses the
Windows OpenSSH Client capability and is selected by default; users can opt out.
Python, uv and the .NET SDK are build
tools only. Prerequisites are prepared before changing an existing service.

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
IPC before stopping/removing the old service. DEV uses independent user-scope
DPAPI settings. `uninstall-dev.cmd` migrates settings from a running legacy DEV
service and removes its registered service and installed binaries only.

## Release

The `Prepare release` action accepts a version and English/Russian notes and
opens a PR. Set `RELEASE_BOT_TOKEN` to a fine-grained token with repository
contents and pull-request write access; a bot token lets the new PR trigger CI.
Preparation does not merge, tag or publish automatically.

1. Update both versions in `Directory.Build.props` (production and `-dev`) and
   commit English/Russian notes as `release-notes/vX.Y.Z.md` in a reviewed PR.
2. Merge after CI succeeds. Tag that main commit `vX.Y.Z` and push the tag.
3. The Release action builds and publishes the installer, application archive,
   separate stable/H3 core archives, application source, signed update manifest
   and SHA256SUMS. A manual dispatch with an existing tag retries an unpublished
   release. Published assets are never replaced.

Configure the repository secret `UPDATE_SIGNING_KEY` before releasing: base64
of a 32-byte Ed25519 private key. Generate it with a trusted tool, store it in a
password manager and upload with `gh secret set UPDATE_SIGNING_KEY`; never put
it in the repository or build logs. Keep the same key between releases.
The release build refuses to run without it or from a dirty checkout.

`update-manifest.sig` signs the exact UTF-8 bytes of `update-manifest.json`.
`update-public-key.txt` exports the verification key, and `signingKeyId` is its
SHA-256. A future updater **must pin that public key** in its trusted installed
code; it must not trust a key downloaded alongside the manifest. The manifest
records version, commit, architecture, runtime requirement, component URLs,
archive sizes/hashes and individual archive entry hashes. App and service update
together; each core is a separate component with installation-relative paths.
This adds update artifacts, not an automatic updater.

The Windows EXE installer is currently not Authenticode-signed. Manifest signing
does not replace a Windows code-signing certificate. CI compiles the installer
and tests silent installation, repair and uninstall on a disposable runner,
without activating TUN. Interactive installation, failure rollback and driver
activation still need testing in a disposable Windows VM before public release.
