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

The IPv6 setting controls destinations routed through that profile. It does not
disable Windows IPv6 or prohibit an IPv6 address for the proxy server. Explicit
system-route rules and local-network bypass retain their own routing behavior.
SSH and TCP-only HTTPS do not provide UDP forwarding.

Podkop lists can update through the connected proxy. When that option is enabled,
startup uses cached lists and refreshes them after connecting.

## DNS and connection settings

Profile DNS supports Cloudflare, Google, Quad9, Yandex or custom DoH and TCP/UDP
endpoints. When the profile is the default route, its DNS uses the proxy; system
mode retains Windows DNS. UDP DNS needs a UDP-capable transport and does not fall
back directly. Custom DNS URL query strings are unsupported.

Bootstrap DNS resolves proxy hostnames before connecting and can use Windows DNS
or a selected DoH provider directly. Connection settings also include TCP TLS and
SSH fingerprint presets; H3 uses the core's native fingerprint.

## Configuration transfer and subscriptions

Import MegaProxy JSON v1–8, FoxyProxy JSON, ProxyList or SuperProxy. Export MegaProxy
JSON or ProxyList. The import review controls which settings to apply; absent
options retain existing values. Other platforms' settings are preserved.
Passwords and private keys are excluded from exports by default; exported files
are not encrypted.

HTTPS subscriptions support backup sources and optional Basic authentication.
Updates preserve local profiles; reconnect to apply network changes. Optional
failover uses selected or all-profile backups in profile order.

## Diagnostics and logs

Opening Diagnostics starts a connection check; speed testing becomes available
after success. H3 checks appear only for compatible transports. Exit-IP reputation
comes from ProxyCheck and is not a guarantee of anonymity.

The Connections tab shows grouped connections and traffic rates; enable Show all
to include default-route connections. Copy paths or log excerpts as needed.
Logs are local, rotate at a configurable size and use English messages.
