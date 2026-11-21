"""Generates the interface artwork in src/assets.

Requires Python 3 with Pillow and NumPy:
    python tools/generate-art.py
The output is deterministic for a given seed, so the committed files can be rebuilt.
"""
import math
import os

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "assets")
AMBER = np.array([255, 146, 24], dtype=np.float32) / 255
AMBER_HOT = np.array([255, 222, 150], dtype=np.float32) / 255
CYAN = np.array([60, 200, 232], dtype=np.float32) / 255
CYAN_HOT = np.array([200, 248, 255], dtype=np.float32) / 255
HOT = {id(AMBER): AMBER_HOT, id(CYAN): CYAN_HOT}


def value_noise(rng, w, h, cells_x, cells_y):
    grid = rng.random((cells_y + 1, cells_x + 1)).astype(np.float32)
    img = Image.fromarray((grid * 255).astype(np.uint8), "L").resize((w, h), Image.BICUBIC)
    return np.asarray(img, dtype=np.float32) / 255


def fbm(rng, w, h, base, octaves=5, aspect=1.0):
    total = np.zeros((h, w), np.float32)
    amp, norm = 1.0, 0.0
    for o in range(octaves):
        cx = max(2, int(base * 2 ** o))
        cy = max(2, int(base * 2 ** o * h / w * aspect))
        total += amp * value_noise(rng, w, h, cx, cy)
        norm += amp
        amp *= 0.5
    return total / norm


def fbm1d(rng, n, base, octaves=6):
    x = np.linspace(0, 1, n)
    total = np.zeros(n)
    amp, norm = 1.0, 0.0
    for o in range(octaves):
        k = int(base * 2 ** o) + 2
        pts = rng.random(k)
        total += amp * np.interp(x * (k - 1), np.arange(k), pts)
        norm += amp
        amp *= 0.55
    return total / norm


def blur(arr, radius):
    if arr.ndim == 2:
        img = Image.fromarray(np.clip(arr * 255, 0, 255).astype(np.uint8), "L")
        return np.asarray(img.filter(ImageFilter.GaussianBlur(radius)), np.float32) / 255
    return np.stack([blur(arr[..., c], radius) for c in range(arr.shape[2])], -1)


def blur_float(arr, radius):
    """Blurs values above 1 by splitting them into scaled 8-bit layers."""
    peak = max(float(arr.max()), 1e-6)
    return blur(arr / peak, radius) * peak


def crystal_polygon(x, y, angle, length, width):
    dx, dy = math.sin(angle), -math.cos(angle)
    nx, ny = -dy, dx
    shoulder = length * 0.78
    pts = {
        "bl": (x - nx * width / 2, y - ny * width / 2),
        "br": (x + nx * width / 2, y + ny * width / 2),
        "sl": (x - nx * width / 2 + dx * shoulder, y - ny * width / 2 + dy * shoulder),
        "sr": (x + nx * width / 2 + dx * shoulder, y + ny * width / 2 + dy * shoulder),
        "tip": (x + dx * length, y + dy * length),
        "ml": (x + dx * shoulder * 0.98 - nx * width * 0.08, y + dy * shoulder * 0.98 - ny * width * 0.08),
        "mb": (x - nx * width * 0.08, y - ny * width * 0.08),
    }
    return pts


def draw_crystals(size, crystals, scale=4):
    """Returns (rgb, alpha) arrays of faceted crystals rendered with supersampling."""
    w, h = size
    layer = Image.new("RGBA", (w * scale, h * scale), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    for c in crystals:
        x, y, angle, length, width, color, light = c
        p = crystal_polygon(x * scale, y * scale, angle, length * scale, width * scale)
        base = np.array(color)
        hot = HOT.get(id(color), base)

        def rgba(c):
            return tuple(int(v) for v in np.clip(c * 255, 0, 255)) + (255,)

        lit = rgba((base * 0.9 + hot * 0.1) * (0.7 + 0.3 * light))
        mid = rgba(base * (0.32 + 0.14 * light))
        cap = rgba(base * 0.35 + hot * 0.65 * light)
        draw.polygon([p["bl"], p["sl"], p["tip"], p["ml"], p["mb"]], fill=lit)
        draw.polygon([p["mb"], p["ml"], p["tip"], p["sr"], p["br"]], fill=mid)
        draw.polygon([p["sl"], p["tip"], p["ml"]], fill=cap)
        draw.line([p["mb"], p["ml"], p["tip"]], fill=rgba(hot * (0.6 + 0.4 * light)), width=max(1, scale))
    layer = layer.resize((w, h), Image.LANCZOS)
    arr = np.asarray(layer, np.float32) / 255
    return arr[..., :3], arr[..., 3]


def make_cluster(rng, x, y, up, color, count, height, spread, light=1.0):
    crystals = []
    for i in range(count):
        t = (i - (count - 1) / 2) / max(1, (count - 1) / 2)
        angle = t * 0.55 + rng.normal(0, 0.12)
        if not up:
            angle = math.pi + angle
        length = height * (1.0 - 0.55 * abs(t)) * rng.uniform(0.75, 1.05)
        width = length * rng.uniform(0.2, 0.28)
        crystals.append((x + t * spread + rng.normal(0, spread * 0.1), y, angle, length, width, color, light))
    crystals.sort(key=lambda c: c[3])
    return crystals


def banner():
    rng = np.random.default_rng(7)
    w, h = 2400, 240
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    img = np.zeros((h, w, 3), np.float32)
    v = yy / h
    img += (np.array([0.030, 0.036, 0.050]) * (1 - v)[..., None] + np.array([0.040, 0.040, 0.044]) * v[..., None])

    def glow(cx, cy, rx, ry, color, strength):
        d = ((xx - cx) / rx) ** 2 + ((yy - cy) / ry) ** 2
        return np.exp(-d)[..., None] * color * strength

    light = glow(1640, 150, 520, 210, AMBER, 0.34) + glow(2130, 110, 380, 200, CYAN, 0.20) + glow(1180, 210, 380, 120, AMBER, 0.10)
    texture = fbm(rng, w, h, 18, 6, aspect=2.2)
    img += light * (0.45 + 0.9 * texture[..., None])
    img += (texture - 0.5)[..., None] * 0.035

    rim_light = glow(1640, 200, 260, 120, AMBER, 1.0) + glow(2110, 200, 200, 110, CYAN, 0.8) + glow(1910, 20, 160, 70, CYAN, 0.6)
    # Two cave layers: ceiling and floor silhouettes, rim-lit only near the crystals.
    for depth, (shade, top_base, bottom_base, roughness) in enumerate([
            (0.050, 30, 200, 30), (0.020, 12, 222, 30)]):
        top = top_base + fbm1d(rng, w, 10 + depth * 6) * roughness * 2 - roughness * 0.6
        bottom = bottom_base - fbm1d(rng, w, 12 + depth * 6) * roughness * 2 + roughness * 0.5
        spikes = np.maximum(0, fbm1d(rng, w, 90, 3) - 0.55) * 70 * (1 + depth * 0.4)
        top = top + spikes
        rock = (yy < top[None, :]) | (yy > bottom[None, :])
        edge = np.minimum(np.abs(yy - top[None, :]), np.abs(yy - bottom[None, :]))
        rim = np.exp(-edge / (3.0 + depth)) * rock
        grain = fbm(rng, w, h, 40, 4, aspect=1.6)
        rock_color = np.array([shade * 1.0, shade * 1.08, shade * 1.25]) * (0.65 + 0.7 * grain[..., None])
        img = np.where(rock[..., None], rock_color + light * 0.22 * (1 - depth * 0.5), img)
        img += rim[..., None] * rim_light * (0.9 - depth * 0.2)

    crystals = []
    crystals += make_cluster(rng, 1640, 232, True, AMBER, 6, 120, 70)
    crystals += make_cluster(rng, 1735, 236, True, AMBER, 3, 64, 26)
    crystals += make_cluster(rng, 2110, 236, True, CYAN, 5, 96, 52)
    crystals += make_cluster(rng, 1910, 4, False, CYAN, 4, 70, 40, 0.8)
    crystals += make_cluster(rng, 1460, 2, False, AMBER, 3, 52, 26, 0.7)
    crystals += make_cluster(rng, 2300, 238, True, AMBER, 3, 58, 30, 0.8)
    crystals += make_cluster(rng, 1220, 238, True, AMBER, 3, 40, 22, 0.55)
    crystals += make_cluster(rng, 980, 238, True, CYAN, 2, 30, 14, 0.45)
    rgb, alpha = draw_crystals((w, h), crystals)
    emission = rgb * alpha[..., None]
    bloom = blur_float(emission, 5) * 0.55 + blur_float(emission, 20) * 0.5 + blur_float(emission, 70) * 0.55
    img = img * (1 - alpha[..., None]) + rgb * alpha[..., None]
    img += bloom

    # Drifting sparks.
    sparks = np.zeros((h, w, 3), np.float32)
    for _ in range(420):
        x = int(np.clip(rng.normal(1700, 420), 600, w - 1))
        y = int(rng.uniform(0, h - 1))
        color = AMBER_HOT if rng.random() < 0.72 else CYAN_HOT
        sparks[y, x] += color * rng.uniform(0.4, 1.6)
    img += blur_float(sparks, 1.0) * 3 + blur_float(sparks, 4) * 2

    # Vignette: dark at the left for the title and at the top and bottom edges.
    left = np.clip((xx - 150) / 1050, 0, 1) ** 1.4
    img *= (0.28 + 0.72 * left)[..., None]
    img *= (1 - 0.45 * (np.abs(v - 0.5) * 2) ** 3)[..., None]
    img += (rng.random((h, w, 1)).astype(np.float32) - 0.5) * 0.02
    img = img / (1 + img * 0.35)
    out = Image.fromarray(np.clip(img * 255, 0, 255).astype(np.uint8), "RGB")
    out.save(os.path.join(OUT, "banner.jpg"), quality=90, optimize=True, progressive=True)


def hexagon(cx, cy, r, rotation=math.pi / 6):
    return [(cx + r * math.cos(rotation + k * math.pi / 3), cy + r * math.sin(rotation + k * math.pi / 3)) for k in range(6)]


def emblem_layer(size, detail=True):
    s = size * 4
    c = s / 2
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.polygon(hexagon(c, c, s * 0.49), fill=(14, 16, 19, 255))
    d.polygon(hexagon(c, c, s * 0.465), fill=(255, 168, 38, 255))
    d.polygon(hexagon(c, c, s * 0.41), fill=(46, 50, 56, 255))
    if detail:
        d.polygon(hexagon(c, c, s * 0.395), fill=(70, 76, 84, 255))
    d.polygon(hexagon(c, c, s * 0.355), fill=(18, 20, 24, 255))
    if detail:
        for k in range(6):
            a = math.pi / 6 + k * math.pi / 3
            rx, ry = c + s * 0.435 * math.cos(a), c + s * 0.435 * math.sin(a)
            rr = s * 0.022
            d.ellipse([rx - rr, ry - rr, rx + rr, ry + rr], fill=(30, 22, 10, 255))
    return img


def emblem(size, detail=True):
    base = emblem_layer(size, detail)
    s = size * 4
    # Inner glow and crystal cluster clipped to the inner hexagon.
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).polygon(hexagon(s / 2, s / 2, s * 0.355), fill=255)
    glow = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.ellipse([s * 0.22, s * 0.48, s * 0.78, s * 0.98], fill=(255, 150, 30, 150))
    glow = glow.filter(ImageFilter.GaussianBlur(s * 0.08))
    rng = np.random.default_rng(3)
    if detail:
        crystals = [(0.5, 0.80, -0.05, 0.50, 0.15, AMBER, 1.0),
                    (0.36, 0.82, -0.55, 0.30, 0.10, CYAN, 0.9),
                    (0.64, 0.82, 0.5, 0.34, 0.11, AMBER, 0.8),
                    (0.44, 0.84, -0.25, 0.22, 0.08, AMBER, 0.7)]
    else:
        crystals = [(0.5, 0.80, 0.0, 0.52, 0.2, AMBER, 1.1)]
    crystals = [(x * size, y * size, a, l * size, wd * size, col, li) for x, y, a, l, wd, col, li in crystals]
    crystals.sort(key=lambda c: c[3])
    rgb, alpha = draw_crystals((size, size), crystals, scale=8)
    layer = Image.fromarray(np.clip(np.dstack([rgb, alpha]) * 255, 0, 255).astype(np.uint8), "RGBA").resize((s, s), Image.LANCZOS)
    inner = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    inner = Image.alpha_composite(inner, glow)
    inner = Image.alpha_composite(inner, layer)
    clipped = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    clipped.paste(inner, (0, 0), Image.fromarray(np.minimum(np.asarray(inner)[..., 3], np.asarray(mask)).astype(np.uint8)))
    result = Image.alpha_composite(base, clipped)
    return result.resize((size, size), Image.LANCZOS)


def crystal_bed():
    rng = np.random.default_rng(11)
    w, h = 440, 360
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    ridge = 300 - fbm1d(rng, w, 6) * 70 - np.exp(-((np.arange(w) - 250) / 120) ** 2) * 40
    rock = (yy > ridge[None, :]).astype(np.float32)
    grain = fbm(rng, w, h, 14, 5)
    rock_rgb = np.array([0.085, 0.090, 0.105]) * (0.55 + 0.9 * grain[..., None])
    rock_rgb = rock_rgb + np.exp(-np.abs(yy - ridge[None, :]) / 40)[..., None] * AMBER * 0.12
    crystals = []
    crystals += make_cluster(rng, 250, 272, True, AMBER, 6, 210, 90)
    crystals += make_cluster(rng, 120, 296, True, CYAN, 4, 120, 46, 0.85)
    crystals += make_cluster(rng, 372, 290, True, AMBER, 3, 92, 34, 0.75)
    crystals += make_cluster(rng, 40, 318, True, AMBER, 2, 50, 18, 0.6)
    rgb, alpha = draw_crystals((w, h), crystals)
    emission = rgb * alpha[..., None]
    bloom = blur_float(emission, 7) * 0.45 + blur_float(emission, 28) * 0.4
    edge = np.abs(yy - ridge[None, :])
    rim = np.exp(-edge / 3) * rock
    light_pos = np.exp(-((xx - 250) / 130) ** 2) + 0.6 * np.exp(-((xx - 120) / 70) ** 2)
    # Premultiplied composite: rock, then crystals, then additive bloom.
    color = rock_rgb * rock[..., None] + rim[..., None] * AMBER * 0.9 * light_pos[..., None]
    a = rock.copy()
    color = color * (1 - alpha[..., None]) + rgb * alpha[..., None]
    a = a + alpha * (1 - a)
    glow_a = np.clip(bloom.max(-1), 0, 1)
    color = color + bloom
    a = np.clip(a + glow_a * (1 - a), 0, 1)
    fade = np.clip((yy - 20) / 160, 0, 1)
    color *= fade[..., None]
    a *= fade
    straight = np.where(a[..., None] > 1e-4, color / np.maximum(a[..., None], 1e-4), 0)
    out = np.dstack([np.clip(straight, 0, 1), a])
    Image.fromarray((out * 255).astype(np.uint8), "RGBA").save(os.path.join(OUT, "crystals.png"), optimize=True)


def write_ico(path, images):
    """Writes 32-bit DIB frames for small sizes and a PNG frame for 256 px, the layout Windows shell icons use."""
    import io
    import struct
    frames = []
    for img in images:
        n = img.width
        if n >= 256:
            buf = io.BytesIO()
            img.save(buf, "PNG", optimize=True)
            frames.append((n, buf.getvalue()))
            continue
        bgra = np.asarray(img.convert("RGBA"))[::-1, :, [2, 1, 0, 3]]
        mask_row = ((n + 31) // 32) * 4
        header = struct.pack("<IiiHHIIiiII", 40, n, n * 2, 1, 32, 0, n * n * 4 + mask_row * n, 0, 0, 0, 0)
        frames.append((n, header + bgra.tobytes() + bytes(mask_row * n)))
    out = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    for n, data in frames:
        out += struct.pack("<BBBBHHII", n % 256, n % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(path, "wb") as f:
        f.write(out + b"".join(data for _, data in frames))


def main():
    os.makedirs(OUT, exist_ok=True)
    banner()
    crystal_bed()
    big = emblem(256)
    big.save(os.path.join(OUT, "emblem.png"), optimize=True)
    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = [emblem(n, detail=n >= 48) for n in sizes]
    write_ico(os.path.join(OUT, "app.ico"), images)


if __name__ == "__main__":
    main()
