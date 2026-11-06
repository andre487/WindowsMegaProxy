import base64
import json
import math
import re
import shutil

from PIL import Image

from artwork import preferred_color, render
from common import ROOT, cli, download, extract


def main():
    commit = "ac7e0eb3259eb62aa6100c435607e2fe03896a97"
    work = ROOT / ".tools/flags"
    source = work / f"source/flag-icons-{commit}"
    if not source.exists():
        archive = download(f"https://github.com/lipis/flag-icons/archive/{commit}.zip", work / "source.zip",
                           "478bf8407080f3e8a0ed8b688f29daf7fcaed585be829fe3f980229e35b2f3ae")
        extract(archive, work / "source")
    countries = sorted((item for item in json.loads((source / "country.json").read_text(encoding="utf-8"))
                        if re.fullmatch("[a-z]{2}", item["code"])), key=lambda item: item["code"])
    page = work / "flags.html"
    images = []
    for country in countries:
        svg = base64.b64encode((source / country["flag_4x3"]).read_bytes()).decode("ascii")
        images.append(f'<img src="data:image/svg+xml;base64,{svg}">')
    page.write_text('<!doctype html><style>html,body{margin:0;background:transparent}body{display:grid;'
                    'grid-template-columns:repeat(16,96px)}img{display:block;width:96px;height:72px}</style>'
                    + "".join(images), encoding="utf-8")
    sheet_path = work / "sheet.png"
    size = (16 * 96, math.ceil(len(countries) / 16) * 72)
    render(page, sheet_path, *size)
    colors = json.loads((ROOT / "assets/profile-colors.json").read_text(encoding="utf-8"))
    palette = [tuple(bytes.fromhex(color["hex"].lstrip("#"))) for color in colors]
    output = ROOT / "src/WindowsMegaProxy/Assets/Flags"
    output.mkdir(parents=True, exist_ok=True)
    metadata = []
    with Image.open(sheet_path) as sheet:
        if sheet.size != size:
            raise RuntimeError("Unexpected flag sheet size.")
        for i, country in enumerate(countries):
            x, y = i % 16 * 96, i // 16 * 72
            flag = sheet.crop((x, y, x + 96, y + 72))
            code = country["code"].upper()
            flag.save(output / f"{code}.png")
            metadata.append(dict(code=code, name=country["name"], preferredColor=preferred_color(flag, palette)))
    (ROOT / "assets/flags/countries.json").write_text(json.dumps(metadata, ensure_ascii=False, indent=4) + "\n", encoding="utf-8")
    shutil.copy2(source / "LICENSE", ROOT / "assets/flags/LICENSE")
    print(f"Generated {len(countries)} flags and palette matches.")


if __name__ == "__main__":
    cli(main)
