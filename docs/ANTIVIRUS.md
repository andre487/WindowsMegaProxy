# Antivirus and code signing

Individual antivirus engines can produce false positives for new installers,
self-signed applications and proxy software. A single heuristic warning is not
proof of malware; it is also not proof that a file is safe. Compare the source,
signature and hash rather than assuming every warning is a false positive.

## sing-box

Antivirus products may classify sing-box as a proxy/tunnelling tool, riskware or
suspicious software. These classifications can also affect unmodified upstream
builds. Installing through a package manager does not remove antivirus checks.
MegaProxy uses upstream binaries without modifying or re-signing them:

- Stable: `SagerNet.sing-box` through WinGet, using the
  [WinGet manifest](https://github.com/microsoft/winget-pkgs/tree/master/manifests/s/SagerNet/sing-box)
  and its archive hash. The package downloads the official SagerNet release.
- Optional HTTP/3 alpha: the
  [official sing-box 1.15.0-alpha.13 Windows archive](https://github.com/SagerNet/sing-box/releases/download/v1.15.0-alpha.13/sing-box-1.15.0-alpha.13-windows-amd64.zip),
  downloaded only after confirmation. Archive and extracted executable/library
  SHA-256 hashes are pinned. Fallback mirrors must provide the same verified bytes.

Neither core is bundled in the application ZIP or installer. Wintun is supplied
by the upstream core; MegaProxy does not build its own kernel driver.

## Signatures

MegaProxy EXE/DLL files, Setup, its temporary copies, the on-demand H3 installer
and uninstaller are Authenticode-signed with the persistent self-signed certificate
in [`assets/authenticode-certificate.pem`](../assets/authenticode-certificate.pem).
This does not establish Windows publisher trust. Production signing uses SHA-256
and an RFC 3161 timestamp. Ed25519 manifest signatures and package hashes remain
mandatory for application updates and release publication.

## Optional manual VirusTotal check

After `build-installer.cmd --preview`, run:

```cmd
check-virus-total.cmd
check-virus-total.cmd --artifacts artifacts\release-0.0.3-verification
```

The tool reads `VIRUSTOTAL_API_KEY`, `--key-file`, or the local developer key at
`%LOCALAPPDATA%\MegaProxy\DeveloperSecrets\WindowsMegaProxy\virus-total.txt`.
If needed, run it through `proxy487 --no-config-sync check-virus-total.cmd`.
It verifies build hashes before uploading the installer and application ZIP.
Reports stay in `artifacts\virustotal`; `--output` selects another local folder.
Incomplete scans or detections return a nonzero exit code for manual review.
The tool never installs the build, publishes reports or controls releases.

CI and release workflows do not run VirusTotal or publish antivirus reports.
Documentation contains current behavior, not individual scan histories.
VirusTotal submissions are shared with its analysis ecosystem; submit only
distributable build files. Its engines can differ from installed products:
[VirusTotal's explanation](https://docs.virustotal.com/docs/antivirus-stats).
