# MegaProxy network core

The Windows host loads one Go DLL per process:

- `MegaProxy.Core.Stable.dll`: sing-box 1.14.4.
- `MegaProxy.Core.Experimental.dll`: sing-box 1.15.0-alpha.13, including HTTP/2 and HTTP/3 proxy transports.

Each DLL contains its own Go runtime. Switching versions starts a new host process;
DLLs are never unloaded in a running process. The host isolates native failures
from the .NET service and is covered by its existing process-job lifetime control.

Build with `scripts\tools.cmd scripts\build_core.py --dev`. Production builds omit
`--dev` and must be signed before use. Go and the checksum-pinned Zig C compiler
are kept in `.tools`. Module versions and checksums are locked independently for
the two DLLs. `--tidy` explicitly updates the module locks after dependency changes.

The host receives a little-endian operation and UTF-8 configuration length (two
32-bit integers), followed by configuration bytes on inherited stdin. Operations
are check (0), start (1) and fetch (2); fetch also reads a length-prefixed UTF-8 URL.
Configuration is limited to 16 MiB and URLs to 8 KiB. EOF stops a running core.
Configuration is not accepted through command-line file paths. The existing local
authenticated Clash API remains responsible for connection snapshots and counters.

Production uses mutual Authenticode verification: the host checks the DLL before
loading it, and the DLL checks its host EXE before accepting commands. Verification
checks file integrity and pins the publisher public key from
`assets/authenticode-certificate.pem`; installing a trusted Windows certificate is
not required. The DLL file is held without write/delete sharing during use.
DEV omits publisher checks at compile time. Preview packages compile in their own
preview publisher key and remain marked as preview updates.

This is not a security boundary against an administrator or injected code. Signing
an EXE does not authenticate every command sent to it. Access controls on the
application, service and its IPC remain necessary. No runtime switch can disable
production signature checks.

Run `scripts\tools.cmd scripts\core_smoke.py --signed` for lifecycle, local TCP
forwarding and positive/negative signature checks. The signing tests do not modify
Windows certificate stores. All client integration tests use the DLL cores prepared
by `setup.cmd` in `.tools/dev-cores`; upstream executables are test servers only.

The DLLs are GPL-3.0-or-later. `Core-NOTICES.txt` includes licenses of the linked Go
dependencies. Wintun's existing signed driver is used without rebuilding it.
