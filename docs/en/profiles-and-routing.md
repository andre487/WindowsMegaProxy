# Profiles and routing

[Русский](../ru/profiles-and-routing.md) · [Documentation](index.md)

## Proxy profiles

| Type | Transport and limits |
| --- | --- |
| HTTPS | CONNECT over HTTP/1.1; optional HTTP/2 or HTTP/2 + HTTP/3 |
| HTTPS with Jump | Two authenticated HTTPS proxies; H3 requires support on both hops |
| SOCKS5 | TCP and UDP ASSOCIATE; no transport encryption |
| SSH | TCP with password or private-key authentication |
| SSH with Jump | Two SSH hops, with shared or separate authentication |
| MASQUE α | Experimental HTTP/3 TCP and UDP |

Stable HTTP/1.1 uses the bundled stable core. HTTP/2 and HTTP/3 use the bundled alpha
core. The HTTP preference setting allows supported fallback; authentication and
certificate errors must not cause a direct bypass of the proxy.
SSH host keys are currently accepted without verification. Passphrase-protected SSH
keys are not supported.

Profiles have names, country flags and colors and can be reordered by dragging.
Editing while connected does not change the applied configuration until reconnecting.
The status identifies the applied profile, even if another profile is selected.

## Rules and IPv6

Choose a default route, then add named rules for EXE paths, recursive folders,
destination domains, wildcard domains, IP addresses/subnets or Podkop lists.
Rules can route through the active profile, use the system route or block traffic.
Disable a rule with its checkbox instead of deleting it.

Windows always allows IPv6 and ignores imported `routing.allowIpv6`.
Explicit system-route/block rules and local-network bypass still apply.
SSH and TCP-only HTTPS do not provide UDP forwarding.

Podkop lists can update through the connected proxy. When that option is enabled,
startup uses cached lists and refreshes them after connecting.

## DNS and connection settings

Profile DNS supports Cloudflare, Google, Quad9, Yandex or custom DoH and TCP/UDP
endpoints. While connected with an active profile, all ordinary application DNS
uses that profile's DNS through the proxy, including applications routed directly.
Traffic rules do not select a separate DNS provider. UDP DNS needs a UDP-capable transport and does not fall
back directly. Custom DNS URL query strings are unsupported.

Bootstrap DNS resolves proxy hostnames before connecting and can use Windows DNS
or a selected DoH provider directly. Automatic selects Cloudflare. Bootstrap does
not select application DNS. Disconnecting removes the TUN DNS configuration and
returns to the existing Windows DNS settings; physical adapter settings are unchanged.
The Windows DNS cache is cleared at TUN DNS startup and shutdown; applications
with their own DNS caches may need restarting. Without an active profile, system DNS is retained.
Application-managed DoH is unaffected. Connection settings also include TCP TLS and
SSH fingerprint presets; H3 uses the core's native fingerprint.

Connection settings include the sing-box log level (default `warn`). Changes are
saved automatically and apply after reconnection. Use `debug` for troubleshooting;
`trace` is more verbose. These logs include destination addresses and application
paths and use the existing local log rotation limit.

## Configuration transfer and subscriptions

Import MegaProxy JSON v1–8, FoxyProxy JSON, ProxyList or SuperProxy. Export MegaProxy
JSON or ProxyList. The import review controls which settings to apply; absent
options retain existing values. Unsupported fields and other platforms’ sections
are discarded with an import notice. Unsupported fingerprints reset to Default.
Passwords and private keys are included by default; clear their export checkboxes
to omit them. Exported files are not encrypted. MTU and traffic units are exported.
See the [Windows configuration contract](../configuration.md) for fields and defaults.

HTTPS subscriptions support backup sources and optional Basic authentication.
Updates preserve unrelated local profiles; matching IDs are replaced and become
subscription-managed. Reconnect to apply network changes. Optional
failover uses selected or all-profile backups in profile order.

## Diagnostics and logs

Opening Diagnostics starts a connection check; speed testing becomes available
after success. H3 checks appear only for compatible transports. Exit-IP reputation
comes from ProxyCheck and is not a guarantee of anonymity.

The Connections tab shows grouped connections and traffic rates; enable Show all
to include default-route connections. Copy paths or log excerpts as needed.
Logs are local, rotate at a configurable size and use English messages.

Connection rows and traffic rates refresh only while the window is focused and
the Connections tab is open. Optional reverse DNS replaces unknown IP addresses
with hostnames; enable it in Connection settings. Successful and failed lookups
default to a 24-hour cache. Other fixed durations or DNS-record TTL are available;
failed lookups in TTL mode are cached for one hour. Lookups run only while the
connection table is active. Full paths remain available in tooltips and for copying.

The opt-in reset of existing TCP connections runs on connection startup. It applies
only to IPv4 TCP connections matched by EXE/folder rules through the active profile;
ambiguous destination-domain rules are skipped. Existing transfers can be interrupted.

For missing tray icons, service connection problems or failed updates, see
[Troubleshooting](troubleshooting.md).
