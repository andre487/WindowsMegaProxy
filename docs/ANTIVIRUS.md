# Antivirus classifications

Observed November 7, 2026 (UTC). VirusTotal results can change over time.

The original WindowsMegaProxy 0.0.1 installer bundled upstream sing-box cores.
Two engines flagged that installer. Checking the exact component hashes from
its signed release manifest gave the following results:

| File | SHA-256 / report | Observed result |
| --- | --- | --- |
| Original installer | [8963569193b463cb6cba263038c0d9c8e7f950dd2b3fd48e2b7ed6ba2521a813](https://www.virustotal.com/gui/file/8963569193b463cb6cba263038c0d9c8e7f950dd2b3fd48e2b7ed6ba2521a813) | Kaspersky: `not-a-virus:UDS:NetTool.Win64.CloudTunnel.a`; DeepInstinct: `MALICIOUS`. |
| Official sing-box 1.14.4, Windows amd64 | [8f30682ee198855440d2a7fe070d5d3653793b2d01bb8c85eea88f93bbe7c8b9](https://www.virustotal.com/gui/file/8f30682ee198855440d2a7fe070d5d3653793b2d01bb8c85eea88f93bbe7c8b9) | The same Kaspersky `not-a-virus` network-tool classification; no DeepInstinct detection in the checked report. |
| Official sing-box 1.15.0-alpha.13, Windows amd64 | [0cebf40f8cea3ca4ad3673c8bdd8c9865242415be7afa190981ae655100b1b86](https://www.virustotal.com/gui/file/0cebf40f8cea3ca4ad3673c8bdd8c9865242415be7afa190981ae655100b1b86) | The initial report had no detections, but several engines timed out or failed. |
| Application ZIP without cores | [746a058042cd1cb5d355130826311a8d05f12c80c6677b7ee60aac6140cc6a7d](https://www.virustotal.com/gui/file/746a058042cd1cb5d355130826311a8d05f12c80c6677b7ee60aac6140cc6a7d) | No detections in the checked report. |

Official upstream downloads:
[stable v1.14.4](https://github.com/SagerNet/sing-box/releases/tag/v1.14.4),
[HTTP/3 v1.15.0-alpha.13](https://github.com/SagerNet/sing-box/releases/tag/v1.15.0-alpha.13).

The matching Kaspersky classification supports attribution to the stable
upstream core. The source of DeepInstinct's generic installer detection has not
been established; the checked standalone core reports do not reproduce it.
These observations are not a guarantee that a file is safe, nor a reason to
disable antivirus protection.

New installer builds download the pinned cores from upstream instead of
embedding them. This makes dependencies explicit and reduces the installer
payload; antivirus software can still inspect or flag a core after download.
Release scans continue to block publication on detections.
