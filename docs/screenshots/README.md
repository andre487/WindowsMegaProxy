# Application screenshots

The English interface in light and dark themes. All profiles, paths, connections,
diagnostic results and log entries are synthetic examples. No personal configuration
is loaded, and no proxy connection or network test is performed.

| Section | Light | Dark |
| --- | --- | --- |
| Connections | [![Connections, light](connections-light.png)](connections-light.png) | [![Connections, dark](connections-dark.png)](connections-dark.png) |
| Profiles | [![Profiles, light](profiles-light.png)](profiles-light.png) | [![Profiles, dark](profiles-dark.png)](profiles-dark.png) |
| Rules | [![Rules, light](rules-light.png)](rules-light.png) | [![Rules, dark](rules-dark.png)](rules-dark.png) |
| Settings | [![Settings, light](settings-light.png)](settings-light.png) | [![Settings, dark](settings-dark.png)](settings-dark.png) |
| Diagnostics | [![Diagnostics, light](diagnostics-light.png)](diagnostics-light.png) | [![Diagnostics, dark](diagnostics-dark.png)](diagnostics-dark.png) |
| Log | [![Log, light](log-light.png)](log-light.png) | [![Log, dark](log-dark.png)](log-dark.png) |

To regenerate on Windows with the .NET SDK, from the repository root:

```cmd
scripts\dotnet.cmd run --project tests\WindowsMegaProxy.UiChecks -c Release -- --documentation-screenshots
```

Use a production build (without `MegaProxyDev=true`). The screenshot fixture uses
an in-memory configuration and a fake engine, with startup loading and timers
disabled. Its temporary logs go into `artifacts/documentation-screenshots`.
