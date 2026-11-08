# Antivirus and code signing

MegaProxy downloads pinned official sing-box packages during installation and
updates. The installer and application ZIP do not embed the cores. Archive and
extracted executable/library hashes are checked before the runtime is activated.
The upstream cores include Wintun; Windows OpenSSH support is an optional
Windows capability, enabled by default in Setup.

Proxy and tunnelling programs can receive network-tool or riskware
classifications. Such a classification is distinct from a named malware family;
it still needs investigation for the particular file and vendor.

Packaged MegaProxy EXE/DLL files, Inno Setup, its temporary copies and uninstaller
are Authenticode-signed with the persistent self-signed certificate in
[`assets/authenticode-certificate.pem`](../assets/authenticode-certificate.pem).
This signature does not establish Windows publisher trust. Existing upstream
signatures are preserved. Release signatures use SHA-256 and an RFC 3161 timestamp.
Ed25519 update-manifest verification is separate and remains mandatory.

VirusTotal's engines can differ from installed antivirus products in their
settings, heuristics and trust checks. A VirusTotal result does not directly
predict whether an installed product will block Setup. See the explanations from
[VirusTotal](https://docs.virustotal.com/docs/antivirus-stats) and
[Malwarebytes](https://forums.malwarebytes.com/topic/288906-virustotal-only-detections-not-detected-in-desktop-product/).

Release scans block publication on detections or incomplete analyses. Suspected
false positives should be submitted to the detecting vendor, following
[VirusTotal's guidance](https://docs.virustotal.com/docs/false-positive).
Individual scan reports and experimental comparisons belong in local/Actions
artifacts, not this repository's documentation. Do not disable antivirus
protection to install the application.
