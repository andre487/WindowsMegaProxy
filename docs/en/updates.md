# Application updates

[Русский](../ru/updates.md) · [Documentation](index.md)

By default, MegaProxy checks GitHub Releases daily. Disable automatic checking or
use **Check now** in **Settings → Application updates**. Discovery and download
are separate actions; the release link opens the changelog on GitHub.

Enable **Download updates through proxy** to use the connected profile for both
checks and downloads. Connect first when using this option.
Only one downloaded update is retained. Installation verifies the signed update
manifest and the package hash.

| Running mode | Installation choices |
| --- | --- |
| Application | Now or at the next application launch |
| Windows service | Now without reboot, at the next computer boot, or reboot now |

Updating now interrupts proxy service temporarily; connections follow the system
route during the update. Deferred installation keeps the current version until
the chosen trigger. Boot-time service updates run before the updated service starts.

## Permanent download link

Share this link without embedding a release version:

**[WindowsMegaProxy-setup.exe](https://github.com/andre487/WindowsMegaProxy/releases/latest/download/WindowsMegaProxy-setup.exe)**

The link selects the latest published release. For a fixed version, use an asset
from a specific [release](https://github.com/andre487/WindowsMegaProxy/releases).

If in-app updating is unavailable, download Setup and install over an existing
WiX/Burn installation with the same scope. Existing profiles and settings are preserved.
For the one-time Inno Setup transition, see [Installation](installation.md).
