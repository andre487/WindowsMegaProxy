# Installation

[Русский](../ru/installation.md) · [Documentation](index.md)

## Download

**[Download MegaProxy Setup](https://github.com/andre487/WindowsMegaProxy/releases/latest/download/WindowsMegaProxy-setup.exe)**

Requires Windows 10/11 x64 and an administrator account. Windows may request UAC
approval during installation and configuration of privileged startup modes.
.NET and both sing-box cores are bundled; Python, Go, uv and the .NET SDK are not
required on the user's computer. Setup installs Windows OpenSSH Client if missing.

The installer has a persistent self-signed signature, not a Windows-trusted publisher
certificate. SmartScreen or an antivirus may warn about the installer or proxy cores.
See [signatures and antivirus notes](../ANTIVIRUS.md); inspect unexpected warnings.

## Installation scope and startup

| Scope | Configuration | Available modes |
| --- | --- | --- |
| System-wide | Shared, administrator-managed | Manual, sign-in, Windows service |
| Current user | Personal, encrypted settings | Manual or sign-in; no Windows service |

The system application is installed in Program Files; personal installations live
under the user's application directory. Select the same scope when upgrading.
In service mode, administrator accounts can edit the shared configuration; other
users see a read-only interface. The tray client opens at sign-in while the service
can keep the connection active independently of the window.

Manual mode starts interception only after the application connects. Sign-in mode
starts the application automatically. The installer prepares protected elevated
launching for the selected mode. Changing startup mode later is supported in settings.

## First connection

1. Add a profile or use the configuration import section in Settings.
2. Select a default route. System mode is the default; rules add exceptions.
3. Select the active profile and connect.
4. Open Diagnostics to verify the exit address and optionally measure speed.

Closing the window normally keeps MegaProxy in the tray. Use the tray menu or
the Exit action in settings to close the client. A separately running service
continues working until disconnected or stopped.

For upgrades, see [Application updates](updates.md). Standard removal is available
in Windows **Settings → Apps** and the Control Panel's installed-program list.
