# /// script
# requires-python = ">=3.10"
# dependencies = ["pillow>=10"]
# ///
"""
Draws the DropWheel logo and writes every size the app ships:

  docs/design/exports/dropwheel-logo.svg       master (256 x 256)
  src/DropWheel.App/Assets/DropWheel.ico       windows, tray, exe, MSI (16 ... 256 px)
  src/DropWheel.App/Assets/*.png               MSIX visual assets, unqualified plus scale-* / targetsize-* variants

The mark is the conversion wheel itself: an orange plate, a ring cut into the wheel's six 60-degree sectors (the
first starting at -120 degrees, so one sits straight up), that top sector lit as the one under the pointer, and a
drop in the hub pointing at it. Sizes up to 32 px have their own geometry, since scaling the 256 px one down blurs
the gaps away. Run from anywhere with `uv run scripts/gen-icons.py`; the outputs are committed, so builds need no Python.
"""
import io
import math
import struct
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src/DropWheel.App/Assets"
EXPORTS = ROOT / "docs/design/exports"

PLATE_TOP = (0xEA, 0x58, 0x0C)  # orange-600
PLATE_BOTTOM = (0xC2, 0x41, 0x0C)  # accent #C2410C
SECTOR_STARTS = [-120, -60, 0, 60, 120, 180]  # degrees, clockwise from +x as on screen; -120 is the lit one
DIM = 150 / 255  # opacity of the five sectors that are not lit

# Master geometry on a 256 grid: plate inset and corner radius, ring radii, gap width at mid-ring, drop radius and
# distance from the drop's centre to its tip.
MASTER = dict(inset=8, radius=56, outer=90, inner=60, gap=13, drop=23, tip=45)
SMALL = {
    16: dict(inset=0, radius=3.2, outer=6.3, inner=3.8, gap=1.05, drop=1.5, tip=3.0, dim=170 / 255),
    20: dict(inset=0, radius=4.0, outer=7.8, inner=4.8, gap=1.25, drop=1.9, tip=3.8, dim=165 / 255),
    24: dict(inset=1, radius=4.6, outer=8.9, inner=5.5, gap=1.4, drop=2.3, tip=4.6, dim=160 / 255),
    32: dict(inset=1, radius=6.4, outer=11.6, inner=7.4, gap=1.8, drop=3.0, tip=5.9, dim=155 / 255),
}


def geometry(n: int) -> dict:
    if n in SMALL:
        return SMALL[n]
    s = n / 256
    g = {key: value * s for key, value in MASTER.items()}
    g["gap"] = max(g["gap"], 1.4)
    if n < 64:
        g["inset"] = round(g["inset"])  # whole pixels keep the plate edge crisp
    g["dim"] = DIM
    return g


def arc(cx, cy, r, a0, a1, steps=96):
    return [(cx + r * math.cos(math.radians(a0 + (a1 - a0) * i / steps)), cy + r * math.sin(math.radians(a0 + (a1 - a0) * i / steps))) for i in range(steps + 1)]


def sectors(g: dict, scale: float, c: float):
    """(a0, a1, lit) of each ring sector, the gap a constant width at mid-ring."""
    mid = (g["outer"] + g["inner"]) / 2 * scale
    half_gap = math.degrees(g["gap"] * scale / 2 / mid)
    return [(start + half_gap, start + 60 - half_gap, start == SECTOR_STARTS[0]) for start in SECTOR_STARTS]


def drop_shape(g: dict, scale: float, c: float):
    """Tip, circle centre and the two tangent angles of a drop pointing up, its bounding box centred on c."""
    r, d = g["drop"] * scale, g["tip"] * scale
    cy = c + (d - r) / 2
    phi = math.degrees(math.acos(r / d))
    return (c, cy - d), (c, cy), r, -90 + phi, 270 - phi


def icon(n: int) -> Image.Image:
    g = geometry(n)
    ss = max(4, min(32, 4096 // n))
    size, k = n * ss, ss
    c = size / 2

    plate = Image.new("RGBA", (size, size))
    draw = ImageDraw.Draw(plate)
    for y in range(size):
        t = y / (size - 1)
        draw.line([(0, y), (size, y)], fill=tuple(round(PLATE_TOP[i] + (PLATE_BOTTOM[i] - PLATE_TOP[i]) * t) for i in range(3)) + (255,))
    mask = Image.new("L", (size, size), 0)
    a = g["inset"] * k
    ImageDraw.Draw(mask).rounded_rectangle([a, a, size - a, size - a], radius=g["radius"] * k, fill=255)
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    img.paste(plate, (0, 0), mask)

    # The white parts go into one coverage layer, so a dim sector blends over the plate instead of replacing it.
    white = Image.new("L", (size, size), 0)
    draw = ImageDraw.Draw(white)
    for a0, a1, lit in sectors(g, k, c):
        draw.polygon(arc(c, c, g["outer"] * k, a0, a1) + arc(c, c, g["inner"] * k, a1, a0), fill=255 if lit else round(255 * g["dim"]))
    tip, centre, r, t0, t1 = drop_shape(g, k, c)
    draw.polygon([tip] + arc(*centre, r, t0, t1), fill=255)
    img.paste(Image.new("RGBA", (size, size), (255, 255, 255, 255)), (0, 0), white)
    return img.resize((n, n), Image.Resampling.BOX)


def svg() -> str:
    g, c = dict(MASTER, dim=DIM), 128.0

    def pt(r, angle):
        return f"{c + r * math.cos(math.radians(angle)):.2f} {c + r * math.sin(math.radians(angle)):.2f}"

    paths = []
    for a0, a1, lit in sectors(g, 1, c):
        o, i = g["outer"], g["inner"]
        d = f"M{pt(o, a0)} A{o} {o} 0 0 1 {pt(o, a1)} L{pt(i, a1)} A{i} {i} 0 0 0 {pt(i, a0)} Z"
        opacity = "" if lit else f' fill-opacity="{DIM:.2f}"'
        paths.append(f'  <path d="{d}" fill="#fff"{opacity}/>')
    tip, (cx, cy), r, t0, t1 = drop_shape(g, 1, c)
    start = f"{cx + r * math.cos(math.radians(t0)):.2f} {cy + r * math.sin(math.radians(t0)):.2f}"
    end = f"{cx + r * math.cos(math.radians(t1)):.2f} {cy + r * math.sin(math.radians(t1)):.2f}"
    paths.append(f'  <path d="M{tip[0]:.2f} {tip[1]:.2f} L{start} A{r} {r} 0 1 1 {end} Z" fill="#fff"/>')
    inset, side = g["inset"], 256 - 2 * g["inset"]
    top, bottom = ("#%02X%02X%02X" % PLATE_TOP), ("#%02X%02X%02X" % PLATE_BOTTOM)
    return "\n".join([
        '<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">',
        "  <title>DropWheel</title>",
        f'  <defs><linearGradient id="plate" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{top}"/><stop offset="1" stop-color="{bottom}"/></linearGradient></defs>',
        f'  <rect x="{inset}" y="{inset}" width="{side}" height="{side}" rx="{g["radius"]}" fill="url(#plate)"/>',
        *paths,
        "</svg>",
        "",
    ])


def write_ico(path: Path, sizes: list[int]):
    """32-bit DIB entries for the small sizes (what every loader reads), PNG from 64 px; one drawing per size, not a resample."""
    entries = []
    for n in sizes:
        im = icon(n)
        if n >= 64:
            buf = io.BytesIO()
            im.save(buf, "PNG", optimize=True)
            data = buf.getvalue()
        else:
            px = im.tobytes("raw", "BGRA")
            rows = [px[y * n * 4:(y + 1) * n * 4] for y in range(n)][::-1]  # DIBs are bottom-up
            mask_stride = ((n + 31) // 32) * 4
            alpha = im.getchannel("A").tobytes()
            mask = bytearray()
            for y in reversed(range(n)):
                row = bytearray(mask_stride)
                for x in range(n):
                    if alpha[y * n + x] == 0:
                        row[x // 8] |= 0x80 >> (x % 8)
                mask += row
            header = struct.pack("<IiiHHIIiiII", 40, n, 2 * n, 1, 32, 0, n * n * 4 + len(mask), 0, 0, 0, 0)
            data = header + b"".join(rows) + bytes(mask)
        entries.append((n, data))
    offset = 6 + 16 * len(entries)
    out = struct.pack("<HHH", 0, 1, len(entries))
    for n, data in entries:
        out += struct.pack("<BBBBHHII", n % 256, n % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    path.write_bytes(out + b"".join(data for _, data in entries))


def on_canvas(width: int, height: int, mark: int) -> Image.Image:
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    canvas.alpha_composite(icon(mark), ((width - mark) // 2, (height - mark) // 2))
    return canvas


def save(img: Image.Image, name: str):
    img.save(ASSETS / name, "PNG", optimize=True)


def main():
    EXPORTS.mkdir(parents=True, exist_ok=True)
    (EXPORTS / "dropwheel-logo.svg").write_text(svg(), encoding="utf-8")
    icon(1024).save(EXPORTS / "dropwheel-logo-1024.png", "PNG", optimize=True)

    write_ico(ASSETS / "DropWheel.ico", [16, 20, 24, 28, 32, 36, 40, 48, 64, 96, 128, 256])

    scales = [100, 125, 150, 200, 400]
    # Square44x44Logo: app list, taskbar, Alt+Tab. The plate is the icon, so plated and unplated are the same picture.
    save(icon(44), "Square44x44Logo.png")
    for s in scales:
        save(icon(round(44 * s / 100)), f"Square44x44Logo.scale-{s}.png")
    for n in [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256]:
        save(icon(n), f"Square44x44Logo.targetsize-{n}.png")
        save(icon(n), f"Square44x44Logo.targetsize-{n}_altform-unplated.png")
        save(icon(n), f"Square44x44Logo.targetsize-{n}_altform-lightunplated.png")
    # Tiles and splash: the mark on a transparent field, at the share of the height Windows' own assets use.
    for name, (w, h), share in [("Square150x150Logo", (150, 150), 0.48), ("Wide310x150Logo", (310, 150), 0.48), ("SplashScreen", (620, 300), 0.48)]:
        save(on_canvas(w, h, round(h * share)), f"{name}.png")
        for s in scales:
            sw, sh = round(w * s / 100), round(h * s / 100)
            save(on_canvas(sw, sh, round(sh * share)), f"{name}.scale-{s}.png")
    save(icon(50), "StoreLogo.png")
    for s in scales:
        save(icon(round(50 * s / 100)), f"StoreLogo.scale-{s}.png")


if __name__ == "__main__":
    main()
