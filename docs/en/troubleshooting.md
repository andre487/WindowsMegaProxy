# Troubleshooting

[Русский](../ru/troubleshooting.md) · [Documentation](index.md)

## No tray icon

Check the hidden-icons area first. Open MegaProxy from its installed shortcut;
an existing client is activated without creating another connection. The client
checks its icon registration every ten seconds and restores it if Explorer loses
it. If the icon stays missing, export the log and include the app version.
In service mode the service can remain connected even without a visible client.

## Service running, client disconnected

Windows service state and client communication are separate. The client retries
the connection automatically and restores controls when communication returns.
During an update, wait for setup to finish. If the problem persists, export the
client log and use the installed bundle's Repair action or the latest compatible
installer with the same installation scope. Do not delete settings to diagnose it.

## Update does not finish

Open Settings → Application updates for the status and GitHub release link.
If proxy downloads are enabled, connect first; otherwise checks and downloads
cannot use that route. When a restart is required, restart Windows before another
update, repair or removal. If in-app updating fails, use the official installer
with the same scope; see [Application updates](updates.md).

## Export diagnostics

Use the Log tab to select/copy text or export the journal. Client logs normally
live under `%LOCALAPPDATA%\WindowsMegaProxy\logs`; personal-host logs are under
`%LOCALAPPDATA%\WindowsMegaProxy\Personal\logs`. Service logs are under
`%PROGRAMDATA%\WindowsMegaProxy\logs`; use the app to export protected logs.
Setup engine logs are under `%PROGRAMDATA%\MegaProxy.Setup`.

Include the version, operating mode and steps that reproduce the issue. Review
logs before sharing: paths, destinations and network metadata can be sensitive.
Configuration exports include passwords and private keys by default; clear these
options when preparing an example and review the remaining fields too.
