import re
import shutil
import argparse

from PIL import Image

from artwork import render
from common import ROOT, cli


def main(*, dev_only=False):
    work = ROOT / ".tools/artwork"
    output = ROOT / "src/WindowsMegaProxy/Assets"
    work.mkdir(parents=True, exist_ok=True)
    for source, name, icon in (("icon-toolbar.svg", "toolbar", "app"), ("icon-taskbar.svg", "tray", "tray"),
                               ("icon-taskbar.svg", "tray-light", "tray-light")):
        svg = (ROOT / "assets/branding" / source).read_text(encoding="utf-8")
        if name == "tray-light":
            svg = svg.replace('fill="#ffffff"', 'fill="#202020"')
        if dev_only:
            svg = svg.replace('</svg>', '<rect x="76" y="76" width="52" height="52" rx="12" fill="#0078d4"/>'
                '<path d="M91 87h12a15 15 0 0 1 0 30H91zm7 6v18h5a9 9 0 0 0 0-18z" fill="white" fill-rule="evenodd"/></svg>')
            name, icon = 'dev-' + name, 'dev-' + icon
        page = work / f"{name}.html"
        page.write_text('<!doctype html><style>html,body{margin:0;background:transparent}svg{width:256px;height:256px}</style>'
                        + re.sub(r"<\?xml[^>]*\?>", "", svg), encoding="utf-8")
        png = work / f"{name}.png"
        render(page, png, 256, 256)
        with Image.open(png) as image:
            if image.size != (256, 256):
                raise RuntimeError("Unexpected icon size.")
            image.save(output / f"{icon}.ico", sizes=[(size, size) for size in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
    if not dev_only:
        shutil.copy2(work / "toolbar.png", output / "toolbar.png")
    print("Generated app, light/dark tray icons and toolbar PNG.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--dev-only", action="store_true")
    cli(lambda: main(dev_only=parser.parse_args().dev_only))
