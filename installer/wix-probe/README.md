# WiX + Burn diagnostic prototype

This is a text-only control for comparing antivirus results with the Inno Setup
controls. It is not a MegaProxy installer and is not used by release packaging.

Build from the repository root after development setup:

```cmd
scripts\.venv\Scripts\python.exe scripts\wix_probe.py
```

The script downloads WiX 5.0.2 and its matching official bootstrapper extension
from NuGet into the project. WiX 5 is used for this experiment to avoid assuming
acceptance of the maintenance-fee terms introduced in WiX 6. The version for a
production migration needs a separate decision.

Outputs in `artifacts/wix-probe`:

- `control.msi`: per-user package containing only `readme.txt`, one HKCU marker,
  and standard Windows Installer removal entries.
- `control-burn.exe`: native C++ WiX standard bootstrapper with the MSI embedded.

The build runs WiX's default MSI validation and checks output headers. It does
not run either installer, elevate, create a service, change startup settings,
or submit files to VirusTotal. The prototype uses separate diagnostic identities
and must never be published as an application update.

Before replacing Inno Setup, a real prototype must preserve both installation
scopes, protected runtime directories, service/task startup modes, verified
dependency downloads, .NET prerequisites, OpenSSH opt-out, update preferences,
upgrade/repair/uninstall behavior, and migration from existing Inno installs.

## Initial VirusTotal results

The unsigned control files built on 2026-11-08 completed analysis with no
malicious or suspicious verdicts, errors, or timeouts:

| File | Size | Undetected engines | Report |
| --- | ---: | ---: | --- |
| MSI | 32,768 bytes | 61 | [VirusTotal](https://www.virustotal.com/gui/file/df450d414f6e00d9c8947aed9f99b201a22a343c3c5874afb0071dfc8a61ecdb) |
| Burn EXE | 1,075,229 bytes | 71 | [VirusTotal](https://www.virustotal.com/gui/file/8a8b46556674c9ab09fc1fd93341e62589c6cc0b5a75b2ce452b13fd59cb45d7) |

These results apply to these exact text-only controls, not a future application
installer. Rebuilding may change file hashes. The MSI extracted from the Burn
container matched the standalone MSI SHA256. Neither package was installed.
