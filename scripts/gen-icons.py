# /// script
# requires-python = ">=3.10"
# dependencies = ["pillow>=10"]
# ///
"""
Draws the DropHalo logo and writes every size the app ships:

  docs/design/exports/drophalo-logo.svg       master (256 x 256)
  src/DropHalo.App/Assets/DropHalo.ico        windows, exe, MSI (16 ... 256 px)
  src/DropHalo.App/Assets/DropHalo.Tray.ico   transparent blue tray mark (16 ... 64 px)
  src/DropHalo.App/Assets/*.png               MSIX visual assets, unqualified plus scale-* / targetsize-* variants

The mark is an open halo above a drop. The app uses a blue plate and white mark; the tray uses the blue mark alone.
Small sizes have their own geometry so both gaps stay clear. Run from anywhere with `uv run scripts/gen-icons.py`;
the outputs are committed, so builds need no Python. Every run also checks the generated ICO entries.
"""
import io
import math
import struct
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src/DropHalo.App/Assets"
EXPORTS = ROOT / "docs/design/exports"

BLUE = (0x00, 0x67, 0xF4)
PLATE_TOP = (0x16, 0x84, 0xFF)
PLATE_BOTTOM = (0x00, 0x67, 0xE8)
HALO_ARCS = [(-78, 32), (148, 258)]

# Master geometry on a 256 grid; offset centres the open mark rather than its missing lower half.
MASTER = dict(inset=8, radius=56, outer=90, inner=62, drop=27, tip=56, offset=18)
SMALL = {
    16: dict(inset=0, radius=3.2, outer=5.7, inner=3.7, drop=1.7, tip=3.5, offset=1.2),
    20: dict(inset=0, radius=4.0, outer=7.1, inner=4.7, drop=2.1, tip=4.4, offset=1.4),
    24: dict(inset=1, radius=4.6, outer=8.5, inner=5.7, drop=2.5, tip=5.3, offset=1.7),
    32: dict(inset=1, radius=6.4, outer=11.3, inner=7.7, drop=3.4, tip=7.0, offset=2.3),
}


def geometry(n: int) -> dict:
    if n in SMALL:
        return SMALL[n]
    s = n / 256
    g = {key: value * s for key, value in MASTER.items()}
    if n < 64:
        g["inset"] = round(g["inset"])  # whole pixels keep the plate edge crisp
    return g


def arc(cx, cy, r, a0, a1, steps=96):
    return [(cx + r * math.cos(math.radians(a0 + (a1 - a0) * i / steps)), cy + r * math.sin(math.radians(a0 + (a1 - a0) * i / steps))) for i in range(steps + 1)]


def drop_shape(g: dict, scale: float, c: float):
    """Tip, circle centre and the two tangent angles of a drop pointing up, its bounding box centred on c."""
    r, d = g["drop"] * scale, g["tip"] * scale
    cy = c + (d - r) / 2
    phi = math.degrees(math.acos(r / d))
    return (c, cy - d), (c, cy), r, -90 + phi, 270 - phi


def icon(n: int, tray: bool = False) -> Image.Image:
    g = geometry(n)
    ss = max(4, min(32, 4096 // n))
    size, k = n * ss, ss
    c = size / 2

    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    if not tray:
        plate = Image.new("RGBA", (size, size))
        draw = ImageDraw.Draw(plate)
        for y in range(size):
            t = y / (size - 1)
            draw.line([(0, y), (size, y)], fill=tuple(round(PLATE_TOP[i] + (PLATE_BOTTOM[i] - PLATE_TOP[i]) * t) for i in range(3)) + (255,))
        mask = Image.new("L", (size, size), 0)
        a = g["inset"] * k
        ImageDraw.Draw(mask).rounded_rectangle([a, a, size - a, size - a], radius=g["radius"] * k, fill=255)
        img.paste(plate, (0, 0), mask)
    else:
        # Without a plate the mark can use more of the notification area's 16 px cell.
        g = {key: value * 1.25 for key, value in g.items()}

    cy = c + g["offset"] * k
    mark = Image.new("L", (size, size), 0)
    draw = ImageDraw.Draw(mark)
    for a0, a1 in HALO_ARCS:
        draw.polygon(arc(c, cy, g["outer"] * k, a0, a1) + arc(c, cy, g["inner"] * k, a1, a0), fill=255)
    tip, (cx, dy), r, t0, t1 = drop_shape(g, k, c)
    draw.polygon([(tip[0], tip[1] + cy - c)] + arc(cx, dy + cy - c, r, t0, t1), fill=255)
    img.paste(Image.new("RGBA", (size, size), (*BLUE, 255) if tray else (255, 255, 255, 255)), (0, 0), mark)
    return img.resize((n, n), Image.Resampling.BOX)


def svg(tray: bool = False) -> str:
    g, c = dict(MASTER), 128.0
    if tray:
        g = {key: value * 1.25 for key, value in g.items()}
    cy = c + g["offset"]
    fill = "#%02X%02X%02X" % BLUE if tray else "#FFFFFF"

    def pt(r, angle):
        return f"{c + r * math.cos(math.radians(angle)):.2f} {cy + r * math.sin(math.radians(angle)):.2f}"

    paths = []
    for a0, a1 in HALO_ARCS:
        o, i = g["outer"], g["inner"]
        d = f"M{pt(o, a0)} A{o} {o} 0 0 1 {pt(o, a1)} L{pt(i, a1)} A{i} {i} 0 0 0 {pt(i, a0)} Z"
        paths.append(f'  <path d="{d}" fill="{fill}"/>')
    tip, (cx, dy), r, t0, t1 = drop_shape(g, 1, c)
    dy += cy - c
    start = f"{cx + r * math.cos(math.radians(t0)):.2f} {dy + r * math.sin(math.radians(t0)):.2f}"
    end = f"{cx + r * math.cos(math.radians(t1)):.2f} {dy + r * math.sin(math.radians(t1)):.2f}"
    paths.append(f'  <path d="M{tip[0]:.2f} {tip[1] + cy - c:.2f} L{start} A{r} {r} 0 1 1 {end} Z" fill="{fill}"/>')
    inset, side = g["inset"], 256 - 2 * g["inset"]
    top, bottom = ("#%02X%02X%02X" % PLATE_TOP), ("#%02X%02X%02X" % PLATE_BOTTOM)
    return "\n".join([
        '<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">',
        "  <title>DropHalo</title>",
        *([] if tray else [
            f'  <defs><linearGradient id="plate" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{top}"/><stop offset="1" stop-color="{bottom}"/></linearGradient></defs>',
            f'  <rect x="{inset}" y="{inset}" width="{side}" height="{side}" rx="{g["radius"]}" fill="url(#plate)"/>',
        ]),
        *paths,
        "</svg>",
        "",
    ])


def write_ico(path: Path, sizes: list[int], tray: bool = False):
    """32-bit DIB entries for the small sizes (what every loader reads), PNG from 64 px; one drawing per size, not a resample."""
    entries = []
    for n in sizes:
        im = icon(n, tray)
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
    with Image.open(path) as decoded:
        assert decoded.ico.sizes() == {(n, n) for n in sizes}
        for n in sizes:
            im = decoded.ico.getimage((n, n)).convert("RGBA")
            low, high = im.getchannel("A").getextrema()
            assert low < 16 and high == 255, (path, n, "empty or opaque icon")
            if tray:
                assert im.getpixel((n // 2, n // 5))[3] == 0, (path, n, "closed halo gap")
                pixels = im.tobytes()
                assert all(tuple(pixels[i:i + 3]) == BLUE for i in range(0, len(pixels), 4) if pixels[i + 3] == 255), (path, n, "tray colour")


def on_canvas(width: int, height: int, mark: int) -> Image.Image:
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    canvas.alpha_composite(icon(mark), ((width - mark) // 2, (height - mark) // 2))
    return canvas


def save(img: Image.Image, name: str):
    img.save(ASSETS / name, "PNG", optimize=True)


def main():
    EXPORTS.mkdir(parents=True, exist_ok=True)
    (EXPORTS / "drophalo-logo.svg").write_text(svg(), encoding="utf-8")
    icon(1024).save(EXPORTS / "drophalo-logo-1024.png", "PNG", optimize=True)
    (EXPORTS / "drophalo-tray.svg").write_text(svg(tray=True), encoding="utf-8")
    icon(256, tray=True).save(EXPORTS / "drophalo-tray-256.png", "PNG", optimize=True)

    write_ico(ASSETS / "DropHalo.ico", [16, 20, 24, 28, 32, 36, 40, 48, 64, 96, 128, 256])
    write_ico(ASSETS / "DropHalo.Tray.ico", [16, 20, 24, 28, 32, 36, 40, 48, 64], tray=True)

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
