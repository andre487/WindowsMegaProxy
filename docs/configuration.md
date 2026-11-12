# Windows configuration contract

MegaProxy JSON v8 uses the common [MegaProxyConfig contract](https://github.com/andre487/MegaProxyConfig).
The local encrypted settings store is not the portable format; use Import/Export.
Windows imports v1–8, FoxyProxy JSON, ProxyList and the SuperProxy text subset.
It exports v8 JSON and ProxyList. Files are limited to 4 MiB and 1,000 profiles/rules.

## Shared fields

All six common transports are supported: HTTPS, HTTPS_JUMP, SSH, SSH_JUMP,
SOCKS5 and MASQUE. HTTP is a legacy local transport and cannot be exported as HTTPS.
Profile IDs are stable. Import updates matching IDs and appends new IDs, retaining
local order. Omitted options retain current values. Missing local profiles require
explicit selection for removal. Passwords/keys are retained only for unchanged
recipients; explicit empty strings clear them. Export includes passwords and keys
by default, with separate opt-out checkboxes. The file is not encrypted.

Current TLS fingerprint, local-network bypass and log size are exported as
`tls.fingerprint`, `routing.bypassLocalNetworks` and `diagnosticLogLimitMb`.
Older `windows` aliases are accepted when the common field is absent. Common fields
take precedence; exports never write stale duplicate aliases.

| Shared TLS identifier | Windows HTTPS/TCP preset |
| --- | --- |
| DEFAULT | CHROME_ANDROID currently; follows future default changes |
| CHROME_ANDROID | sing-box chrome |
| FIREFOX_ANDROID | sing-box firefox |
| EDGE_ANDROID | sing-box edge |
| RANDOMIZED | sing-box randomized |

These are preset mappings, not identical Android TLS handshakes. Legacy lowercase
Windows names for these presets are migrated. Other presets, including CUSTOM,
SAMSUNG_INTERNET, YANDEX_BROWSER and old Safari/iOS/native choices, reset to DEFAULT.
Import warns about unsupported fingerprints. HTTP/3 uses the native core's QUIC
fingerprint; TCP presets do not customize QUIC. Custom JA3 is not supported.
`preferHttp2`/`preferHttp3` remain per-proxy transport preferences.

Global `ssh.fingerprint` applies to both SSH hops when supplied; otherwise the
profile's `proxy.sshProfile` is used. Supported identifiers are DEFAULT,
OPENSSH_TERMUX, CONNECTBOT, JUICESSH and TERMIUS_ANDROID. `ssh.authMode` supports
AUTO, PASSWORD_ONLY and KEY_ONLY. Session rotation/channel/keepalive options are
unsupported. SSH host keys continue to be accepted without verification, including
when imported key pins or `acceptAnyHostKey=false` are present.
When a global SSH fingerprint is present, the profile editor displays and edits
that global selection and explains that it affects all SSH profiles.

Windows always allows IPv6. Imported `routing.allowIpv6` does not restrict it and
is discarded. Explicit destination block rules still work. Android package routing
and other platforms' routing sections do not become Windows rules.

DNS supports the named providers and custom HTTPS/TCP/UDP endpoints; see
[profiles and routing](en/profiles-and-routing.md). Unsupported DoH query strings
are reported and must be removed before using that DNS endpoint.

Unknown and unsupported common fields, foreign platform sections, and unknown
Windows options are discarded with an import notice. Unsupported Windows section
versions are ignored entirely. Foreign fields are not retained for re-export.
Rule fields are stricter: an unknown rule constraint rejects the import, preventing
an accidentally broader routing rule. Local permissions are never granted by import.

## Windows section, version 1

The Windows application owns validation of this section, not the shared schema.
All fields are optional; absence preserves an existing value. Defaults below apply
to fresh settings. MTU and traffic units are portable within this section.

| Field | Values / default |
| --- | --- |
| version | 1; absent means 1 |
| defaultAction | system (default), proxy, block; legacy direct maps to system |
| autoConnect | boolean; false |
| resetExistingTcpConnections | boolean; false |
| launchAtSignIn | boolean; false; startup changes still require local authorization |
| tunMtu | integer 1280–65535; 1500 |
| trafficUnits | IEC (default), SI |
| bootstrapDnsProvider | auto (default), system, CLOUDFLARE, GOOGLE, QUAD9, YANDEX, YANDEX_SAFE, YANDEX_FAMILY |
| reverseDnsCacheHours | 0 (DNS TTL), 1, 6, 12, 24 (default), 72; failed TTL-mode queries cache for one hour |
| resolveConnectionHosts | boolean; false |
| showAllConnections | boolean; false |
| language | en or ru; Russian Windows selects ru, otherwise en |
| theme | auto (default), light, dark |
| alwaysMinimizeToTray | boolean; false |
| automaticAppUpdates | boolean; true |
| appUpdatesThroughProxy | boolean; false |
| rules | ordered array; empty by default |

Legacy aliases `tlsFingerprint`, `bypassLocalNetworks`, and `logSizeMb` import into
the common settings above. Log size is an integer 1–100 MiB, default 5.
Profile-level `windows.colorMode` is `auto` or `manual`; it controls country-based
color assignment. Legacy `sshHostKey`/`sshJumpHostKey` strings may be transferred
but do not enable host-key enforcement.

Rules contain `name` (up to 80 characters), `enabled` (default true), `source`
(`all`, `exe`, `folder`), `path` (absolute EXE/folder; folders are recursive),
`destination` (empty, domain, `*.domain`, IP/CIDR or `podkop:id,id`),
`podkopThroughProxy` (default false) and `action` (`proxy`, `system`, `block`).
Rules target the active profile; legacy `profileId` is ignored. A version-1 rule
cannot target an independently selected profile. Rule order is significant.

## Configuration subscriptions

Windows implements the shared HTTPS subscription protocol, sending
`X-MegaProxy-Client: windows` and its application version. Requests reject redirects,
validate TLS and accept only HTTP 200 with strict UTF-8, at most 4 MiB decoded.
Connect/read timeouts are 15 seconds within a 45-second request budget.

Refresh tries the primary and then up to seven backups. Missing settings retain
current values; unknown fields are discarded and unsupported fingerprints reset
with warning status and an English log entry. Matching canonical IDs replace and
adopt profiles. Only absent subscription-owned profiles are removed. Unrelated
local profiles remain. Legacy feeds reconcile unique owned profiles by name or endpoint.
The surviving selection, including no selection, connection mode, auto-connect and
startup preferences remain local. Source replacement keeps local Basic Auth,
interval and pause state. Failed/stale downloads never replace a working snapshot.

Refreshing configuration does not restart an established tunnel. Reconnect applies
network changes. Ownership, status, timestamps, caches, downloaded updates and
permissions stay local and are not exported. Manual refresh works while paused.
