# MegaProxy artwork

Sources: [MegaProxyIdentity](https://github.com/andre487/MegaProxyIdentity/tree/65898348e43b94a8831180f8aa2b00cabd520fe4), revision `65898348e43b94a8831180f8aa2b00cabd520fe4`.
The upstream `LICENSE` is preserved as `IDENTITY-LICENSE` (CC0).

- `icon-taskbar.svg`: disconnected tray icon. A dark foreground variant is generated for light Windows taskbars.
- `icon-toolbar.svg`: application/window icon and connected tray icon. The header has no duplicate logo.

Run `icons.cmd` to regenerate the committed PNG/ICO files with Microsoft Edge and Pillow through the locked uv project in `scripts/`. Normal builds use committed images without Edge. ICO files contain 16, 20, 24, 32, 40, 48, 64, 128 and 256 px PNG frames.
