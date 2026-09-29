"""Builds the game's texture atlases from the source sheets in godot/assets (placeholders; see
godot/assets/PLACEHOLDERS.md). Run from the repo root: python tools/make_textures.py (needs Pillow).

- godot/assets/textures/belt_atlas.png: horizontal bands, each one period of a tileable crop stretched
  to the full width, so a UV running past 1 along the belt repeats it. BeltView's Band* constants give
  each band's rows; keep them in step with BANDS below.
- godot/assets/textures/package_crates.png: the crate faces of supplies.png, 4 x 3, each cut to the
  square in the middle of its cell (packages are cubes).
"""
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "godot" / "assets"
OUT = ASSETS / "textures"

ATLAS = 1024
PAD = 4  # rows of each band's edge repeated above and below it, so filtering doesn't bleed across bands
# name: (crop box in belt.png, first row, rows) -- rows exclude the padding
BANDS = {
    "top":      ((0, 190, 386, 360), 8, 440),        # BELT TOP: one row of chevrons, a half band either side
    "rail":     ((1173, 168, 1526, 216), 464, 56),   # EDGE RAIL, the plain one
    "crossbar": ((1173, 279, 1526, 336), 536, 56),   # CROSSBAR
    "housing":  ((0, 585, 386, 980), 608, 408),      # BELT SIDE
}


def belt_atlas(sheet: Image.Image) -> Image.Image:
    atlas = Image.new("RGB", (ATLAS, ATLAS))
    for box, top, rows in BANDS.values():
        band = sheet.crop(box).resize((ATLAS, rows), Image.LANCZOS)
        atlas.paste(band, (0, top))
        for i in range(1, PAD + 1):
            atlas.paste(band.crop((0, 0, ATLAS, 1)), (0, top - i))
            atlas.paste(band.crop((0, rows - 1, ATLAS, rows)), (0, top + rows - 1 + i))
    return atlas


def package_crates(sheet: Image.Image, cell: int = 256) -> Image.Image:
    cols, rows = 4, 3
    w, h = sheet.width / cols, sheet.height / rows
    side = min(w, h)
    out = Image.new("RGB", (cell * cols, cell * rows))
    for r in range(rows):
        for c in range(cols):
            x, y = c * w + (w - side) / 2, r * h + (h - side) / 2
            face = sheet.crop((round(x), round(y), round(x + side), round(y + side))).resize((cell, cell), Image.LANCZOS)
            out.paste(face, (c * cell, r * cell))
    return out


if __name__ == "__main__":
    OUT.mkdir(exist_ok=True)
    belt_atlas(Image.open(ASSETS / "belt.png").convert("RGB")).save(OUT / "belt_atlas.png")
    package_crates(Image.open(ASSETS / "supplies.png").convert("RGB")).save(OUT / "package_crates.png")
    print("wrote", OUT / "belt_atlas.png", "and", OUT / "package_crates.png")
