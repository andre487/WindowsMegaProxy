import os
import subprocess as sp
from pathlib import Path

from common import ROOT


def render(page, output, width, height):
    candidates = [Path(os.environ.get(key, "")) / "Microsoft/Edge/Application/msedge.exe"
                  for key in ("ProgramFiles(x86)", "ProgramFiles")]
    edge = next((path for path in candidates if path.is_file()), None)
    if edge is None:
        raise RuntimeError("Microsoft Edge is required only to regenerate artwork.")
    output.unlink(missing_ok=True)
    sp.run([str(edge), "--headless", "--disable-gpu", "--no-first-run", "--disable-extensions",
            "--hide-scrollbars", "--default-background-color=00000000", "--force-device-scale-factor=1",
            "--virtual-time-budget=3000", f"--window-size={width},{height}",
            f"--user-data-dir={page.parent / 'edge-profile'}", f"--screenshot={output}", page.as_uri()],
           check=True, timeout=40, creationflags=sp.CREATE_NO_WINDOW, stdout=sp.DEVNULL, stderr=sp.DEVNULL)
    if not output.exists():
        raise RuntimeError("Edge did not produce an image.")


def preferred_color(image, palette):
    votes = [0] * len(palette)
    for count, pixel in image.convert("RGBA").getcolors(image.width * image.height):
        red, green, blue, alpha = pixel
        if alpha < 96 or min(red, green, blue) > 242 or max(red, green, blue) - min(red, green, blue) < 24:
            continue
        nearest = min(range(len(palette)), key=lambda i: sum((a - b) ** 2 for a, b in zip(pixel[:3], palette[i])))
        votes[nearest] += count
    return max(range(len(votes)), key=votes.__getitem__) if any(votes) else -1
