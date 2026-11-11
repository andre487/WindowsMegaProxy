# Antivirus and code signing

Individual antivirus engines can produce false positives for new installers,
self-signed applications and proxy software. A single heuristic warning is not
proof of malware; it is also not proof that a file is safe. Compare the source,
signature and hash rather than assuming every warning is a false positive.

## sing-box

Antivirus products may classify sing-box as a proxy/tunnelling tool, riskware or
suspicious software. These classifications can also affect unmodified upstream
builds, including classifications reported by Kaspersky and DeepInstinct.
Detections vary by core and engine version. Installing through a package manager
does not remove antivirus checks.
MegaProxy builds and signs its own `MegaProxy.Core.Stable.dll` and
`MegaProxy.Core.Experimental.dll` from pinned upstream Go modules:

- [Stable sing-box 1.14.4](https://github.com/SagerNet/sing-box/tree/v1.14.4).
- [Experimental sing-box 1.15.0-alpha.13](https://github.com/SagerNet/sing-box/tree/v1.15.0-alpha.13).

Both DLLs are bundled with the application and installer. Module versions and
checksums are recorded in `native/core`; these are MegaProxy builds, not official
sing-box release binaries. Custom packaging does not guarantee fewer detections.
No WinGet installation or runtime core download is involved. The .NET runtime is
downloaded from official Microsoft packages on NuGet.org at a pinned version and installed locally. Wintun is supplied by the upstream cores;
MegaProxy does not build its own kernel driver. OpenSSH Client is a mandatory
Windows capability installed by Setup when missing.

## Signatures

MegaProxy EXE files, Setup, its temporary copies and uninstaller are Authenticode-signed with the persistent self-signed certificate
in [`assets/authenticode-certificate.pem`](../assets/authenticode-certificate.pem).
This does not establish Windows publisher trust. Production signing uses SHA-256
and an RFC 3161 timestamp. Ed25519 manifest signatures and package hashes remain
mandatory for application updates and release publication.

Before loading a production core DLL, its host verifies the Authenticode signature
and pinned publisher key. The DLL verifies the host executable in the same way.
No certificate is installed in Windows trust stores. DEV binaries use a separate
compile-time policy. These checks do not defend against an administrator or code
already injected into an authorized process.

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
