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

Starting with 0.1.1, each release includes this filename as a byte-for-byte copy
of its verified, signed, versioned installer. GitHub's `latest` redirect selects
the latest published release. For reproducible deployments, use the versioned
asset from a specific [release](https://github.com/andre487/WindowsMegaProxy/releases).

Auto-update manifests continue to refer to versioned assets. The permanent-name
copy has the same SHA-256 as the versioned Setup entry in `SHA256SUMS`; it is not
a second build. Retrying publication does not replace published installer bytes.

If in-app updating is unavailable, download Setup and install over the existing
installation with the same scope. Existing profiles and settings are preserved.
