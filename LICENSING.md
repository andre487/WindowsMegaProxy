# Licensing

Copyright (c) 2026 Andrey Prokopyuk, except where another author is identified.

The root [LICENSE](LICENSE) is MIT and applies to original project files unless
a more specific license or notice applies. The repository contains components
with different licenses; the root license does not relicense third-party code
or make the complete application distribution MIT.

| Scope | License |
| --- | --- |
| Original scripts, documentation and installer definitions (`installer/wix`) | MIT |
| Application, service, launcher and libraries under `src`, except the two setup projects below | GPL-3.0-or-later; see `src/LICENSE` |
| Original code and resources in `src/WindowsMegaProxy.Setup` and `src/WindowsMegaProxy.SetupUI` | MIT; each project includes its license |
| `native/core`, including its C host, and `native/diagnostics` | GPL-3.0-or-later |
| Tests under `tests` | GPL-3.0-or-later; see `tests/LICENSE` |
| MegaProxy artwork | CC0-1.0; see `assets/branding/IDENTITY-LICENSE` |
| Country flags | MIT; see `assets/flags/LICENSE` |
| Third-party files, generated dependency notices and vendored source | Their original licenses and copyright notices |

For GPL components, you may redistribute and modify the program under the GNU
General Public License, version 3 or, at your option, any later version. They are
provided without warranty, including merchantability or fitness for a particular
purpose. The full GPL text is supplied with each component.

The setup projects do not reference GPL application assemblies. Administrative
operations run the separately licensed service executable as a separate process.
Do not copy third-party GPL code into the MIT setup projects. WiX Burn and its
BootstrapperApplicationApi remain under MS-RL; our MIT grant does not cover them.
WiX build-tool OSMF terms are separate from these source-code licenses.

Binary distributions keep the application's GPL in `LICENSE.txt`, the setup MIT
notice in `licenses/Setup-MIT.txt`, and third-party notices alongside the components.
See [THIRD-PARTY-NOTICES](installer/THIRD-PARTY-NOTICES.txt) in the source repository.
In binary packages the same inventory is named `THIRD-PARTY-NOTICES.txt`.

Earlier releases retain their licenses and the rights already granted to users.
The historical MIT notice is retained in `installer/licenses/WindowsMegaProxy-MIT.txt`.
