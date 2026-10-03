"""Rasterize the shared code-native cloud mark for Windows package assets."""

from pathlib import Path
from io import BytesIO

import cairosvg
from PIL import Image

here = Path(__file__).resolve().parent
svg = (here / "cloud.svg").read_bytes()
assets = here.parent / "yizuka-msix" / "staging" / "Assets"
assets.mkdir(parents=True, exist_ok=True)

for size, name in [(44, "Logo44.png"), (150, "Logo150.png"), (256, "Logo256.png")]:
    cairosvg.svg2png(bytestring=svg, write_to=str(assets / name), output_width=size, output_height=size)

layers = []
for size in [16, 24, 32, 48, 64, 128, 256]:
    png = cairosvg.svg2png(bytestring=svg, output_width=size, output_height=size)
    layers.append(Image.open(BytesIO(png)).convert("RGBA"))
layers[-1].save(here / "YizukaCloud.ico", format="ICO", sizes=[(v, v) for v in [16, 24, 32, 48, 64, 128, 256]])
