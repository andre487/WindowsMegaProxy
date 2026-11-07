# WindowsMegaProxy Privacy Policy

Last updated: November 7, 2026

This policy describes the WindowsMegaProxy Windows application, its installed
network components and its release tooling. WindowsMegaProxy is an open-source
proxy client maintained at
[github.com/andre487/WindowsMegaProxy](https://github.com/andre487/WindowsMegaProxy).
The project does not operate the proxy servers you configure.

## Data processed on your device

To apply routing rules and display connections, the application processes
application names and executable paths, destination domains and IP addresses,
ports, protocols, routing decisions and traffic counters. The network core
handles traffic selected by your configuration.

The application saves proxy profiles, authentication details, rules,
subscription addresses and preferences locally. System-wide installations use
a shared configuration accessible to administrators and the Windows service.
Current-user installations keep a separate personal configuration. Windows
data protection and filesystem permissions protect stored settings. Runtime
files needed by network components may contain connection credentials; the
application needs access to them while operating. These protections do not
make data inaccessible to administrators or software controlling your device.

Logs are stored locally, rotate according to the configured size limit and can
be exported. They can contain timestamps, operational events, errors and network
metadata. The application attempts to redact recognized secrets, but logs and
configuration exports should still be reviewed before sharing. Configuration
export excludes passwords and private keys by default; you can choose to include
them. Other details, such as server addresses, usernames, rules and subscription
URLs, may also be sensitive.

## Network requests and third parties

WindowsMegaProxy has no built-in advertising, usage analytics or automatic
crash-report upload to the project maintainers. It does make network requests
to provide the features below:

| Feature | Recipients and information involved |
| --- | --- |
| Proxy connections | Your configured proxy, SSH, jump and knock-host servers receive the connections and authentication information required by their protocols. Proxy operators may observe connection metadata and any traffic their protocols expose. Application-layer encryption, when present, is separate from the proxy connection. |
| DNS resolution | Windows-configured or application-selected resolvers receive DNS queries. Depending on routing, the network core can use Cloudflare DNS, including DNS over HTTPS. |
| Configuration subscriptions | Configured subscription servers receive download requests and any authentication you configure. Enabled subscriptions may refresh automatically. |
| Podkop lists | GitHub-hosted community lists are downloaded and cached when used by rules. List downloads and refreshes contact GitHub infrastructure. |
| Application updates | Automatic checks and downloads contact the GitHub Releases API and GitHub download infrastructure. Automatic checks are enabled by default and can be disabled during installation or in settings; manual checks remain available. |
| Connection diagnostics | Entering the Diagnostics section starts a connection check. Checks can contact example.com, Cloudflare and, when needed, IP/country lookup services at ifconfig.me, api.ipify.org, icanhazip.com, ifconfig.co, ipapi.co and api.country.is. Supported HTTP/3 checks can also contact quic.browserleaks.com. Results can include the tested route's public IP address and country. |
| Speed tests | Starting a speed test transfers generated test data to and from Cloudflare speed-test endpoints. Tests can exercise both system and proxy routes. The separate TraceProbe development utility also performs network and speed checks when run. |
| Installation prerequisites | Installation and application updates download pinned sing-box packages from SagerNet's official GitHub releases, verifying their hashes. The installer may also download required components from official Microsoft sources, including the .NET runtime and Windows OpenSSH components. |

These recipients can see the source IP address of the route used, request timing
and ordinary protocol metadata. The address may be your direct connection's IP
or a proxy exit IP. Authentication is sent where required by the configured
service. Third parties operate under their own privacy policies; the project
does not control their logging, retention or processing locations.

Normal use does not upload your configuration or connection list to the project
maintainers. User-configured subscription URLs, exported files and logs can
contain secrets; sharing them is your responsibility.

## Your controls and retention

You can edit or remove profiles, rules and subscriptions, disable automatic
application-update checks, adjust local log retention and disconnect the proxy.
Disconnecting traffic interception does not disable independently enabled update
or subscription checks. Closing the interface in Windows service mode does not
stop the service. Exit or stop the applicable components when you want them to
stop operating.

Uninstalling preserves configuration data so that it can be reused. To remove
it, stop the application and service, then delete the relevant application data,
logs and caches. System-wide data is normally under
`%PROGRAMDATA%\WindowsMegaProxy`; current-user data and caches are normally under
`%LOCALAPPDATA%\WindowsMegaProxy`, with personal configuration in its `Personal`
subdirectory. DEV builds use separate locations. Files you export or back up
remain wherever you saved them, until you delete them.

## Public contributions and release tooling

Information you post in public GitHub issues or pull requests is public and is
also subject to GitHub's policies. Do not post credentials or private logs.

Release automation uses OpenAI to draft release notes from public repository
changes and submits release installers and application archives to VirusTotal.
These processes concern project source and distributable build files, not
configuration or traffic collected from installed clients. VirusTotal submissions
should be treated as shared with that service and its analysis ecosystem.

## Changes and contact

This policy may change as application behavior changes. Revisions are recorded
in the repository history, with the update date above.

For privacy questions, contact the maintainer through the
[project's GitHub issues](https://github.com/andre487/WindowsMegaProxy/issues).
Keep public questions free of personal information and secrets.
