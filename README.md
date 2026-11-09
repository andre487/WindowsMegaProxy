# WindowsMegaProxy

A native Windows 10/11 proxy client for the MegaProxy family. Route all applications or selected traffic through your active proxy profile.

## Features

- **Proxy profiles:** HTTPS, SOCKS5, SSH, HTTPS Jump and experimental MASQUE (HTTP/3). Create, edit, duplicate and switch profiles, with colors and country flags.
- **Installation scope:** system-wide with shared administrator-managed settings and optional Windows service, or current-user with personal encrypted settings and a protected scheduled launch. Current-user installation requires an administrator account; normal launches need no repeated UAC approval.
- **Flexible routing:** named, individually enabled rules for EXE files, recursive folders, domains, wildcard domains, IP addresses, subnets and Podkop community lists. Podkop rules can refresh their lists through the connected proxy, using cached lists before connection. Use the active profile, system mode or blocking; system mode is the default.
- **HTTP versions:** choose the stable core (HTTP/1.1), HTTP/2 or HTTP/2 + HTTP/3 for HTTPS and HTTPS Jump. H2/H3 use the bundled 1.15 alpha core, with fallback to lower versions when unsupported. HTTP/3 checks appear only for supported transports.
- **Connection monitoring:** grouped connections, traffic rates, sortable columns and copyable application paths.
- **Profile DNS:** Cloudflare, Google, Quad9, Yandex Basic/Safe/Family or a custom HTTPS DoH endpoint. Used through the active proxy when it is the default route; system mode retains Windows DNS. DNS settings use the shared MegaProxy format. Custom URL query strings and plain TCP/UDP DNS are not supported yet.
- **Bootstrap DNS:** independently choose Windows DNS or a built-in DoH provider to resolve proxy server names directly before connecting, including selective proxy routing. Automatic preserves the existing mode-dependent behavior.
- **Diagnostics:** HTTPS checks, exit IP and country, HTTP/3 checks and download/upload speed tests.
- **Security options:** certificate verification, SSH accept-new with confirmation of changed server keys, password or private-key SSH authentication, TCP TLS and SSH fingerprint presets, and knock-host checks for masked HTTPS proxies.
- **Configuration transfer:** import MegaProxy JSON v1–8, FoxyProxy JSON, ProxyList and SuperProxy. Export MegaProxy JSON or ProxyList; review changed settings and optionally remove missing profiles. Passwords and private keys are excluded by default. Other clients' settings are preserved.
- **Subscriptions:** HTTPS sources with backups, optional Basic authentication, automatic/manual updates and subscription definitions imported from JSON. Updates preserve local profiles and require reconnecting to apply network changes.
- **Failover:** optional selected/all-profile backups in profile order, triggered by proxy transport failures; retains certificate and SSH key checks.
- **Windows integration:** manual launch, elevated startup at sign-in, or a shared Windows service; the active profile and transport in the system tray and main window, optional automatic connection, automatic settings saving and reconnect indicators for pending changes.
- **Application updates:** daily checks on GitHub Releases, signed manifests, and separate download and installation actions. Checks and downloads can use the connected proxy profile. Update immediately or at the next application start; service installations can update during the next computer restart or restart now. Automatic checks can be disabled in settings or during installation.
- **Shared service:** one protected configuration for the machine; administrator accounts can edit without repeated UAC prompts, other users have a read-only interface.
- **Appearance:** English and Russian, automatic/light/dark themes and Windows display scaling.
- **Logs:** local rotating logs, configurable size and export. Log entries are always in English.

## Quick start

See [screenshots of the main sections](docs/screenshots/README.md) in light and dark themes.

Native installer and release packages: see [installer builds](installer/README.md).

Requires Windows 10/11 x64 and [uv](https://docs.astral.sh/uv/getting-started/installation/).

```cmd
setup.cmd
build.cmd
install.cmd
```

Production packages bundle a pinned .NET runtime and both proxy cores. Installation requires administrator approval and prepares Windows OpenSSH Client if missing. Add or import a profile, configure routing rules and connect. VS Code tasks are included; Visual Studio is not required.

Upgrading from 0.0.4: if service mode shows **Read-only mode**, open MegaProxy once as administrator and use **Settings → Application updates**. The updated installer fixes service access and removes the old version directories while preserving settings.

`install.cmd` defaults to manual launch; use `install.cmd --mode login` or
`install.cmd --mode service` for the other modes. Standalone hosts run elevated;
the interface in service mode can run without elevation.

Choose an operating mode in the native installer or application settings. Manual and sign-in modes require administrator privileges and stop interception when you exit the application. Service mode keeps the shared connection running when the interface closes. Changing mode restarts the application and preserves its configuration.

For development, run `run.cmd` from an administrator terminal (or approve UAC). DEV has separate processes, icons and settings. In manual mode, `run.cmd` rebuilds and starts directly from `artifacts/dev/app`; login/service modes rebuild the isolated protected DEV installation and restart it. The selected mode is preserved. When the other channel is running, either client offers to stop it or exit the current channel; `stop-prod.cmd` remains available for explicit cleanup. Repeated launches restore the existing window and preserve its connection. `cleanup-dev.cmd` (also `uninstall-dev.cmd`) explicitly removes DEV services, startup entries and installed binaries while preserving settings. `build.cmd --dev` only builds DEV.

Windows OpenSSH Client is a mandatory system component prepared by the installer.

`run-trace-probe.cmd` builds and runs the standalone console test: Cloudflare trace followed by repeated 50 MB download/upload measurements with HTTP latency and a one-second pause. It shows the latest three results; press Enter to stop. The existing `trace-probe.cmd` command remains available.

## Status

Early preview. HTTP/3 is experimental. SSH Jump and passphrase-protected SSH keys are not yet supported. Automatic failover to SSH requires a previously verified server key.

See [antivirus notes and official dependency sources](docs/ANTIVIRUS.md) for possible false positives and upstream sing-box classifications.

## License

See the [Privacy Policy](PRIVACY.md) for local storage, network requests and third-party services.

Application code is licensed under [MIT](LICENSE). Third-party components and [artwork](assets/branding/README.md) retain their respective licenses.
