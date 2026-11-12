"""Crop the app tile out of the logo, make the outside transparent, and write .ico + .png."""
import sys
from PIL import Image, ImageDraw

src, out_dir = sys.argv[1], sys.argv[2]
img = Image.open(src).convert("RGBA")
w, h = img.size

# Locate the dark tile: rows/columns whose pixels are clearly darker than the light background.
px = img.load()
def dark(x, y):
    r, g, b, _ = px[x, y]
    return r + g + b < 300 and b - r > 25  # navy tile, not the grey drop shadow

cx, cy = w // 2, h // 2
left = next(x for x in range(w) if dark(x, cy))
right = next(x for x in range(w - 1, -1, -1) if dark(x, cy))
top = next(y for y in range(h) if dark(cx, y))
bottom = next(y for y in range(h - 1, -1, -1) if dark(cx, y))
print("tile bounds", left, top, right, bottom)

inset = 10  # stay clear of the anti-aliased tile edge
tile = img.crop((left + inset, top + inset, right + 1 - inset, bottom + 1 - inset))
tw, th = tile.size
side = max(tw, th)
radius = int(side * 0.17)

# Supersampled rounded-rect mask for smooth edges.
ss = 4
mask = Image.new("L", (tw * ss, th * ss), 0)
ImageDraw.Draw(mask).rounded_rectangle((0, 0, tw * ss - 1, th * ss - 1), radius=radius * ss, fill=255)
mask = mask.resize((tw, th), Image.LANCZOS)
tile.putalpha(mask)

square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
square.paste(tile, ((side - tw) // 2, (side - th) // 2), tile)

square.resize((512, 512), Image.LANCZOS).save(f"{out_dir}/chicken.png")
square.resize((256, 256), Image.LANCZOS).save(
    f"{out_dir}/chicken.ico",
    sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
)
print("ok")
