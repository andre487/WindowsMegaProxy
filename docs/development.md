# Development and tests

[Home](../README.md) · [English guides](en/index.md) · [Русские руководства](ru/index.md)

Develop on Windows 10/11 x64 with VS Code and [uv](https://docs.astral.sh/uv/).
Visual Studio is not required. Dependencies are downloaded into the checkout;
the pinned Go SDK builds the standalone HTTP/3 diagnostic helper.

```cmd
setup.cmd
build.cmd
run.cmd
```

Approve UAC when requested. DEV has separate processes, icons, settings and startup
entries. Manual mode runs directly from the build output; sign-in/service modes
use the isolated DEV installation. `build.cmd --dev` builds without launching.

`stop-prod.cmd` stops the production channel. `cleanup-dev.cmd` (also
`uninstall-dev.cmd`) removes DEV services, startup entries and installed binaries
while preserving settings. `install.cmd --mode manual|login|service` selects a
mode explicitly.

| Command | Checks |
| --- | --- |
| `tools-test.cmd` | Python build/release tools |
| `test.cmd` | Configuration and core validation |
| `ui-test.cmd` | UI, service and lifecycle fixtures |
| `h3-test.cmd` | Local HTTP/3, HTTPS Jump and fallback |
| `ssh-test.cmd` | Local SSH authentication and Jump |
| `ipv6-test.cmd` | IPv6 routing across all proxy types using local real cores |
| `run-trace-probe.cmd` | Interactive Cloudflare trace and 50 MB speed measurements |

The IPv6 suite uses local SOCKS ingress instead of TUN and does not test external
IPv6 connectivity or Wintun. Logs and test reports remain under `artifacts`.
For release signing, packaging, CI gates and retries, see
[Installer and release automation](../installer/README.md).
