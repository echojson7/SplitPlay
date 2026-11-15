# SPDX-License-Identifier: MIT
"""Builds splitplay.ico (16-256 px) from the in-app logo (web/src/components/BrandMark.vue).

The logo is two rounded panes on a 16x16 grid; each size is drawn 8x larger and scaled down, so edges stay smooth at
every size. Left pane: the light theme accent (#005fb8), right pane: the dark theme accent (#60cdff), so the icon reads
on light and dark taskbars alike. Run: python3 make-icon.py (needs Pillow).
"""
from pathlib import Path

from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
PANES = [((1.0, 2.5, 6.4, 11.0), (0x00, 0x5F, 0xB8, 255)), ((8.6, 2.5, 6.4, 11.0), (0x60, 0xCD, 0xFF, 255))]
RADIUS = 1.4
SUPERSAMPLE = 8


def render(size: int) -> Image.Image:
    big = size * SUPERSAMPLE
    scale = big / 16
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    for (x, y, w, h), colour in PANES:
        box = (round(x * scale), round(y * scale), round((x + w) * scale) - 1, round((y + h) * scale) - 1)
        draw.rounded_rectangle(box, radius=RADIUS * scale, fill=colour)
    return image.resize((size, size), Image.Resampling.LANCZOS)


def main() -> None:
    here = Path(__file__).resolve().parent
    images = [render(s) for s in SIZES]
    # PNG-compressed 256 px frame, plain bitmaps below (what Windows Explorer and the shell expect).
    images[-1].save(here / "splitplay.ico", format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[:-1])


if __name__ == "__main__":
    main()
