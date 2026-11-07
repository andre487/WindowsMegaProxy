# WindowsMegaProxy

A native Windows 10/11 proxy client for the MegaProxy family. Route all applications or selected traffic through your active proxy profile.

## Features

- **Proxy profiles:** HTTPS, SOCKS5, SSH, HTTPS Jump and experimental MASQUE (HTTP/3). Create, edit, duplicate and switch profiles, with colors and country flags.
- **Flexible routing:** ordered rules for EXE files, folders including subfolders, domains, wildcard domains, IP addresses and subnets. Use the active profile, system mode or blocking; system mode is the default.
- **HTTP/3:** optional preference for HTTPS and HTTPS Jump, with TCP fallback when available. HTTP/3 checks appear only for supported transports.
- **Connection monitoring:** grouped connections, traffic rates, sortable columns and copyable application paths.
- **Diagnostics:** HTTPS checks, exit IP and country, HTTP/3 checks and download/upload speed tests.
- **Security options:** certificate verification, SSH accept-new with confirmation of changed server keys, password or private-key SSH authentication, TCP TLS and SSH fingerprint presets, and knock-host checks for masked HTTPS proxies.
- **Portable configuration:** import and export `MegaProxy.json`, preserving other clients' settings. Passwords and private keys are excluded from exports by default. See the [example configuration](examples/MegaProxy.json).
- **Windows integration:** system tray, startup at sign-in, optional automatic connection, automatic settings saving and reconnect indicators for pending changes.
- **Shared service:** one protected configuration for the machine; administrator accounts can edit without repeated UAC prompts, other users have a read-only interface.
- **Appearance:** English and Russian, automatic/light/dark themes and Windows display scaling.
- **Logs:** local rotating logs, configurable size and export. Log entries are always in English.

## Quick start

Native installer and release packages: see [installer builds](installer/README.md).

Requires Windows 10/11 x64 and [uv](https://docs.astral.sh/uv/getting-started/installation/).

```cmd
setup.cmd
build.cmd
install.cmd
```

Installation requires administrator approval and downloads .NET and network cores from official sources. The interface runs without elevation. Add or import a profile, configure routing rules and connect. VS Code tasks are included; Visual Studio is not required.

The service keeps the shared connection running when the interface closes. Run `install.cmd` again after updating service code.

For development, run `stop-prod.cmd`, then `run.cmd`. DEV has separate processes, icons, settings and a manually started service. `run.cmd` refuses to start while production is running, builds DEV and updates its service with administrator approval. Production builds use `build.cmd` and `install.cmd`; `build.cmd --dev` only builds DEV.

SSH key verification requires the Windows OpenSSH Client optional feature.

`run-trace-probe.cmd` builds and runs the standalone console test: Cloudflare trace followed by repeated 50 MB download/upload measurements with HTTP latency and a one-second pause. It shows the latest three results; press Enter to stop. The existing `trace-probe.cmd` command remains available.

## Status

Early preview. HTTP/3 is experimental. SSH Jump, passphrase-protected SSH keys, subscription updates and Podkop lists are not yet supported.

## License

Application code is licensed under [MIT](LICENSE). Third-party components and [artwork](assets/branding/README.md) retain their respective licenses.
