# Country flags

Source: [lipis/flag-icons](https://github.com/lipis/flag-icons/tree/ac7e0eb3259eb62aa6100c435607e2fe03896a97), revision `ac7e0eb3259eb62aa6100c435607e2fe03896a97`, MIT license (preserved in `LICENSE`).

Source archive SHA-256: `478bf8407080f3e8a0ed8b688f29daf7fcaed585be829fe3f980229e35b2f3ae`.

`flags.cmd` renders the source 4:3 SVGs with Microsoft Edge and crops the sheet with Pillow into 257 committed 96×72 PNGs in `src/WindowsMegaProxy/Assets/Flags`. Python and Pillow are managed by the locked uv project in `scripts/`. `countries.json` contains country names/codes and the nearest Windows palette index based on the rendered flag's colored pixels. The raster files are derived from the SVGs without changing the flag designs. The license is copied into the application's `Notices` directory.

The matching algorithm follows AndroidMegaProxy's `ProfileColorMatcher`: skip pixels with alpha below 96, nearly white pixels and pixels with channel spread below 24, then vote for the closest palette color by squared RGB distance. Palette values are stored in `assets/profile-colors.json`, sourced from [Microsoft Fluent UI shared colors](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/colors.ts). Existing explicit profile color indices remain unchanged.
