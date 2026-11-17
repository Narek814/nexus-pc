"""Draws the Nexus PC app icon (cyberpunk: neon N, violet -> cyan, dark graphite).

Dev-time tool (Pillow). Output: ios/NexusPC/Assets.xcassets/AppIcon.appiconset/AppIcon.png
(1024x1024, opaque, as the App Store requires). Run: python3 -I tools/make_icon.py
"""
import json
import pathlib

from PIL import Image, ImageChops, ImageDraw, ImageFilter

SIZE = 1024
VIOLET = (155, 92, 255)
CYAN = (0, 220, 255)
root = pathlib.Path(__file__).resolve().parent.parent
out_dir = root / "ios/NexusPC/Assets.xcassets/AppIcon.appiconset"


def gradient(size, start, end):
    """Diagonal gradient from top-left (start) to bottom-right (end)."""
    w, h = size
    base = Image.new("RGB", size)
    px = base.load()
    for y in range(h):
        for x in range(w):
            t = (x + y) / (w + h - 2)
            px[x, y] = tuple(int(start[i] + (end[i] - start[i]) * t) for i in range(3))
    return base


def background():
    bg = Image.new("RGB", (SIZE, SIZE), (7, 7, 11))
    # Soft violet light from the upper middle.
    glow = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(glow).ellipse((SIZE * 0.05, -SIZE * 0.25, SIZE * 0.95, SIZE * 0.75), fill=110)
    glow = glow.filter(ImageFilter.GaussianBlur(170))
    bg = Image.composite(Image.new("RGB", bg.size, (44, 22, 86)), bg, glow)
    # Faint perspective grid at the bottom (synthwave floor).
    grid = Image.new("L", (SIZE, SIZE), 0)
    g = ImageDraw.Draw(grid)
    horizon = int(SIZE * 0.70)
    for i in range(1, 9):
        y = horizon + int((SIZE - horizon) * (i / 8) ** 1.8)
        g.line((0, y, SIZE, y), fill=70, width=3)
    for i in range(-8, 9):
        g.line((SIZE / 2 + i * 40, horizon, SIZE / 2 + i * 260, SIZE), fill=70, width=3)
    fade = Image.linear_gradient("L").resize((SIZE, SIZE))
    grid = ImageChops.multiply(grid, fade)
    bg = Image.composite(Image.new("RGB", bg.size, CYAN), bg, grid.point(lambda v: v // 2))
    return bg


def n_mask():
    """The N: two bars and a diagonal, with slanted ends."""
    m = Image.new("L", (SIZE, SIZE), 0)
    d = ImageDraw.Draw(m)
    left, right, top, bottom, bar = 270, 754, 230, 790, 132
    d.rounded_rectangle((left, top, left + bar, bottom), radius=26, fill=255)
    d.rounded_rectangle((right - bar, top, right, bottom), radius=26, fill=255)
    r = 26  # start the diagonal inside the rounded corners, so no square tips stick out
    d.polygon([(left + r, top), (left + bar + 24, top), (right - r, bottom), (right - bar - 24, bottom)], fill=255)
    return m


def main():
    icon = background()
    mask = n_mask()
    colored = gradient((SIZE, SIZE), VIOLET, CYAN)
    # Neon tube: a bright outline of the letter, a dark glassy inside, and a wide glow around it.
    inner = mask.filter(ImageFilter.MinFilter(57))
    tube = ImageChops.subtract(mask, inner)
    for radius, strength in ((90, 0.75), (40, 1.0), (14, 1.0)):
        halo = tube.filter(ImageFilter.GaussianBlur(radius)).point(lambda v, s=strength: min(255, int(v * s * 1.6)))
        icon = Image.composite(colored, icon, halo)
    dark = Image.blend(Image.new("RGB", icon.size, (10, 8, 20)), colored, 0.18)
    icon = Image.composite(dark, icon, inner)
    icon = Image.composite(colored, icon, tube)
    # White-hot centre line of the tube.
    core = ImageChops.subtract(mask.filter(ImageFilter.MinFilter(17)), mask.filter(ImageFilter.MinFilter(41)))
    icon = Image.composite(Image.new("RGB", icon.size, (245, 248, 255)), icon, core.filter(ImageFilter.GaussianBlur(3)).point(lambda v: int(v * 0.8)))

    out_dir.mkdir(parents=True, exist_ok=True)
    icon.save(out_dir / "AppIcon.png", optimize=True)
    (out_dir / "Contents.json").write_text(json.dumps({
        "images": [{"filename": "AppIcon.png", "idiom": "universal", "platform": "ios", "size": "1024x1024"}],
        "info": {"author": "xcode", "version": 1},
    }, indent=2) + "\n")
    (out_dir.parent / "Contents.json").write_text(json.dumps({"info": {"author": "xcode", "version": 1}}, indent=2) + "\n")
    print("wrote", out_dir / "AppIcon.png")


if __name__ == "__main__":
    main()
