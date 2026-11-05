"""晴天娃娃图标生成器（v2）：头身分明、多风格、高饱和，输出 PNG + ICO。"""
import math
import os
import random

import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFilter

S = 1024
OUT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "icons"))
os.makedirs(OUT, exist_ok=True)
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]


def hex2rgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def lin_grad(size, c1, c2, angle=90):
    w, h = size
    if angle in (0, 180):
        t = np.linspace(0, 1, w)
        row = t if angle == 0 else t[::-1]
        arr = np.tile(row, (h, 1))
    else:
        t = np.linspace(0, 1, h)
        col = t if angle == 90 else t[::-1]
        arr = np.tile(col[:, None], (1, w))
    a, b = np.array(hex2rgb(c1), float), np.array(hex2rgb(c2), float)
    rgb = a[None, None, :] + (b - a)[None, None, :] * arr[:, :, None]
    out = np.zeros((h, w, 4), np.uint8)
    out[:, :, :3] = np.clip(rgb, 0, 255).astype(np.uint8)
    out[:, :, 3] = 255
    return Image.fromarray(out, "RGBA")


def radial_grad(size, inner, outer, center=None, radius=None):
    w, h = size
    center = center or (w / 2, h / 2)
    radius = radius or max(w, h) * 0.72
    yy, xx = np.mgrid[0:h, 0:w]
    d = np.clip(np.sqrt((xx - center[0]) ** 2 + (yy - center[1]) ** 2) / radius, 0, 1)
    a, b = np.array(hex2rgb(inner), float), np.array(hex2rgb(outer), float)
    rgb = a[None, None, :] + (b - a)[None, None, :] * d[:, :, None]
    out = np.zeros((h, w, 4), np.uint8)
    out[:, :, :3] = np.clip(rgb, 0, 255).astype(np.uint8)
    out[:, :, 3] = 255
    return Image.fromarray(out, "RGBA")


def bg_rounded(base, radius_ratio=0.22, inset=18):
    w, h = base.size
    m = Image.new("L", (w, h), 0)
    ImageDraw.Draw(m).rounded_rectangle(
        [inset, inset, w - 1 - inset, h - 1 - inset], radius=int(min(w, h) * radius_ratio), fill=255)
    out = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    out.paste(base, (0, 0), m)
    return out


def drop_shadow(layer, offset=(0, 14), blur=24, opacity=105):
    a = layer.split()[3].point(lambda v: int(v * opacity / 255))
    sh = Image.new("RGBA", layer.size, (0, 0, 0, 0))
    sh.putalpha(a)
    sh = sh.filter(ImageFilter.GaussianBlur(blur))
    out = Image.new("RGBA", layer.size, (0, 0, 0, 0))
    out.paste(sh, offset, sh)
    return out


def body_points(cx, hy, hr, length, w_top, w_bot, bumps=3):
    """布身：上沿略窄于头，向下张开，底边波浪。"""
    top_y = hy + hr * 0.74
    bot_y = top_y + length
    pts = []
    n = 48
    for i in range(n + 1):
        t = i / n
        x = cx - (w_top / 2 + (w_bot - w_top) / 2 * (t ** 0.9))
        pts.append((x, top_y + (bot_y - top_y) * t))
    steps = 110
    for i in range(steps + 1):
        t = i / steps
        x = cx - w_bot / 2 + w_bot * t
        y = bot_y + hr * 0.16 * math.sin(t * math.pi * bumps * 2)
        pts.append((x, y))
    for i in range(n + 1):
        t = 1 - i / n
        x = cx + (w_top / 2 + (w_bot - w_top) / 2 * (t ** 0.9))
        pts.append((x, top_y + (bot_y - top_y) * t))
    return pts


def geometry(hr_ratio=0.185, len_ratio=0.375, top=1.34, bot=2.30, hy_ratio=0.285):
    hr = S * hr_ratio
    return (S / 2, S * hy_ratio, hr, S * len_ratio, hr * top, hr * bot)


def neck_shade(img, cx, hy, hr, shade=(0, 0, 0, 46)):
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    ImageDraw.Draw(layer).ellipse(
        [cx - hr * 1.02, hy + hr * 0.60, cx + hr * 1.02, hy + hr * 1.05], fill=shade)
    img.alpha_composite(layer.filter(ImageFilter.GaussianBlur(hr * 0.30)))


def collar(d, cx, hy, hr, color, width=9):
    """领口线：头与布身的分界。"""
    d.arc([cx - hr * 0.92, hy + hr * 0.28, cx + hr * 0.92, hy + hr * 1.05],
          start=18, end=162, fill=color, width=width)


def draw_string(d, cx, top_y, length=105, color=(70, 70, 82, 235), width=9, loop=True):
    d.line([(cx, top_y), (cx, top_y - length)], fill=color, width=width)
    if loop:
        r = 19
        d.arc([cx - r, top_y - length - r * 1.7, cx + r, top_y - length + r * 0.3],
              start=0, end=360, fill=color, width=width)


def face(d, cx, cy, hr, style="dots", color=(40, 40, 48, 255), blush=None, w=11):
    ex, er = hr * 0.40, hr * 0.095
    if style == "dots":
        for sx in (-1, 1):
            d.ellipse([cx + sx * ex - er, cy - er, cx + sx * ex + er, cy + er], fill=color)
    elif style == "happy":
        for sx in (-1, 1):
            bx = cx + sx * ex
            d.arc([bx - er * 2.3, cy - er * 1.7, bx + er * 2.3, cy + er * 1.9],
                  start=195, end=345, fill=color, width=w)
    elif style == "sleepy":
        for sx in (-1, 1):
            bx = cx + sx * ex
            d.line([(bx - er * 2.1, cy), (bx + er * 2.1, cy)], fill=color, width=w)
    d.arc([cx - hr * 0.17, cy + hr * 0.17, cx + hr * 0.17, cy + hr * 0.42],
          start=12, end=168, fill=color, width=w)
    if blush:
        for sx in (-1, 1):
            bx = cx + sx * hr * 0.60
            d.ellipse([bx - hr * 0.15, cy + hr * 0.12, bx + hr * 0.15, cy + hr * 0.32], fill=blush)


def base_doll(fill):
    """返回 (布身+头) 图层与几何。"""
    cx, hy, hr, ln, wt, wb = geometry()
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ld = ImageDraw.Draw(layer)
    ld.polygon(body_points(cx, hy, hr, ln, wt, wb), fill=fill)
    ld.ellipse([cx - hr, hy - hr, cx + hr, hy + hr], fill=fill)
    return layer, (cx, hy, hr, ln, wt, wb)


# ---------------- 风格 ----------------
def style_sunny():
    img = bg_rounded(lin_grad((S, S), "#FFD23F", "#FF7A2F", 90))
    d = ImageDraw.Draw(img)
    for k in range(20):
        a = k * 18 * math.pi / 180
        if math.sin(a) > 0.35:      # 只画上方射线，脚下不留“草”
            continue
        d.line([(S / 2 + S * 0.40 * math.cos(a), S * 0.42 + S * 0.40 * math.sin(a)),
                (S / 2 + S * 0.56 * math.cos(a), S * 0.42 + S * 0.56 * math.sin(a))],
               fill=(255, 255, 255, 52), width=18)
    layer, (cx, hy, hr, *_rest) = base_doll((255, 255, 255, 255))
    img.alpha_composite(drop_shadow(layer, (0, 16), 24, 95))
    img.alpha_composite(layer)
    d = ImageDraw.Draw(img)
    neck_shade(img, cx, hy, hr, (255, 190, 90, 70))
    d = ImageDraw.Draw(img)
    collar(d, cx, hy, hr, (255, 196, 104, 210))
    draw_string(d, cx, hy - hr, 74, (150, 74, 24, 235), 10)
    face(d, cx, hy + hr * 0.02, hr, "happy", (140, 60, 20, 255), (255, 128, 110, 120), 13)
    return img


def style_sky():
    img = bg_rounded(lin_grad((S, S), "#2D7DF6", "#5FD2F7", 90))
    cloud = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    cd = ImageDraw.Draw(cloud)
    for ox, oy, r in [(0.20, 0.76, 0.10), (0.34, 0.80, 0.13), (0.50, 0.77, 0.09), (0.80, 0.20, 0.07)]:
        cd.ellipse([S * ox - S * r, S * oy - S * r * 0.7, S * ox + S * r, S * oy + S * r * 0.7],
                   fill=(255, 255, 255, 95))
    img.alpha_composite(cloud.filter(ImageFilter.GaussianBlur(14)))
    layer, (cx, hy, hr, *_rest) = base_doll((252, 253, 255, 255))
    img.alpha_composite(drop_shadow(layer, (0, 14), 22, 105))
    img.alpha_composite(layer)
    neck_shade(img, cx, hy, hr, (120, 170, 235, 80))
    d = ImageDraw.Draw(img)
    collar(d, cx, hy, hr, (170, 205, 245, 200))
    draw_string(d, cx, hy - hr, 72, (32, 84, 152, 240), 9)
    face(d, cx, hy + hr * 0.02, hr, "dots", (44, 62, 92, 255), (120, 190, 255, 130), 11)
    return img


def style_candy():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    layer, (cx, hy, hr, *_rest) = base_doll((255, 255, 255, 255))
    img.alpha_composite(drop_shadow(layer, (0, 18), 28, 110))
    grad = lin_grad((S, S), "#FF3D9A", "#7C4DFF", 115)
    grad.putalpha(layer.split()[3])
    img.alpha_composite(grad)
    d = ImageDraw.Draw(img)
    d.arc([cx - hr * 0.78, hy - hr * 0.86, cx + hr * 0.42, hy + hr * 0.30],
          start=200, end=318, fill=(255, 255, 255, 180), width=18)
    d.line([(cx, hy - hr), (cx, hy - hr - 78)], fill=(124, 77, 255, 255), width=10)
    d.arc([cx - 18, hy - hr - 138, cx + 18, hy - hr - 96], start=0, end=360,
          fill=(255, 61, 154, 255), width=10)
    face(d, cx, hy + hr * 0.02, hr, "dots", (255, 255, 255, 255), (255, 150, 205, 150), 12)
    return img


def style_neon():
    img = bg_rounded(radial_grad((S, S), "#17263E", "#05080F"), 0.24)
    cx, hy, hr, ln, wt, wb = geometry()
    body = body_points(cx, hy, hr, ln, wt, wb)
    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    for w, col in [(48, (0, 255, 224, 60)), (28, (0, 255, 224, 120)), (12, (150, 255, 246, 235))]:
        gd.polygon(body, outline=col, width=w)
        gd.ellipse([cx - hr, hy - hr, cx + hr, hy + hr], outline=col, width=w)
    img.alpha_composite(glow.filter(ImageFilter.GaussianBlur(7)))
    d = ImageDraw.Draw(img)
    d.polygon(body, fill=(9, 20, 32, 240))
    d.ellipse([cx - hr, hy - hr, cx + hr, hy + hr], fill=(9, 20, 32, 240))
    d.polygon(body, outline=(0, 255, 224, 255), width=10)
    d.ellipse([cx - hr, hy - hr, cx + hr, hy + hr], outline=(0, 255, 224, 255), width=10)
    collar(d, cx, hy, hr, (0, 255, 224, 200), 8)
    d.line([(cx, hy - hr), (cx, hy - hr - 74)], fill=(255, 32, 176, 255), width=9)
    d.arc([cx - 18, hy - hr - 138, cx + 18, hy - hr - 96], start=0, end=360,
          fill=(255, 32, 176, 255), width=9)
    face(d, cx, hy + hr * 0.02, hr, "dots", (170, 255, 248, 255), None, 10)
    random.seed(7)
    for _ in range(28):
        x, y, r = random.uniform(0, S), random.uniform(0, S), random.uniform(2, 6)
        d.ellipse([x - r, y - r, x + r, y + r], fill=(120, 255, 245, random.randint(70, 180)))
    return img


def style_crayon():
    img = bg_rounded(lin_grad((S, S), "#FFF6E0", "#FFDDB0", 100), 0.20)
    cx, hy, hr, ln, wt, wb = geometry()
    body = body_points(cx, hy, hr, ln, wt, wb)
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ld = ImageDraw.Draw(layer)
    ld.polygon(body, fill=(255, 252, 242, 255))
    ld.ellipse([cx - hr, hy - hr, cx + hr, hy + hr], fill=(255, 252, 242, 255))
    img.alpha_composite(layer)
    neck_shade(img, cx, hy, hr, (200, 150, 100, 70))
    d = ImageDraw.Draw(img)
    random.seed(5)

    def wob(points, amt=4.5):
        return [(x + random.uniform(-amt, amt), y + random.uniform(-amt, amt)) for x, y in points]

    d.polygon(wob(body), outline=(62, 46, 32, 255), width=11)
    d.ellipse([cx - hr, hy - hr, cx + hr, hy + hr], outline=(62, 46, 32, 255), width=11)
    collar(d, cx, hy, hr, (214, 122, 74, 210), 11)
    d.line([(cx, hy - hr), (cx, hy - hr - 72)], fill=(62, 46, 32, 255), width=10)
    d.arc([cx - 19, hy - hr - 134, cx + 19, hy - hr - 94], start=0, end=360,
          fill=(214, 90, 60, 255), width=10)
    face(d, cx, hy + hr * 0.02, hr, "sleepy", (62, 46, 32, 255), (255, 178, 150, 150), 11)
    return img


def style_matcha():
    img = bg_rounded(lin_grad((S, S), "#0E9F6E", "#9BD46B", 90))
    d = ImageDraw.Draw(img)
    random.seed(11)
    for _ in range(10):
        x, y = random.uniform(S * 0.05, S * 0.95), random.uniform(S * 0.05, S * 0.95)
        r = random.uniform(15, 26)
        for k in range(5):
            a = k * 2 * math.pi / 5
            px, py = x + r * math.cos(a), y + r * math.sin(a)
            d.ellipse([px - r * 0.6, py - r * 0.6, px + r * 0.6, py + r * 0.6],
                      fill=(255, 216, 230, 140))
    layer, (cx, hy, hr, *_rest) = base_doll((255, 255, 255, 255))
    img.alpha_composite(drop_shadow(layer, (0, 16), 24, 95))
    img.alpha_composite(layer)
    neck_shade(img, cx, hy, hr, (20, 110, 80, 75))
    d = ImageDraw.Draw(img)
    collar(d, cx, hy, hr, (170, 225, 190, 200))
    draw_string(d, cx, hy - hr, 72, (18, 104, 76, 240), 9)
    face(d, cx, hy + hr * 0.02, hr, "happy", (24, 104, 76, 255), (255, 158, 190, 140), 12)
    return img


def style_sticker():
    """贴纸风：粗黑描边 + 白色外发光，亮黄/白撞色，小尺寸也清楚。"""
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    cx, hy, hr, ln, wt, wb = geometry(hr_ratio=0.175, len_ratio=0.355, top=1.34, bot=2.26, hy_ratio=0.300)
    body = body_points(cx, hy, hr, ln, wt, wb)

    shape = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    sd = ImageDraw.Draw(shape)
    sd.polygon(body, fill=hex2rgb("#FFC300") + (255,))
    sd.ellipse([cx - hr, hy - hr, cx + hr, hy + hr], fill=(255, 253, 243, 255))

    # 白色贴纸外圈
    halo = shape.split()[3].filter(ImageFilter.MaxFilter(25))
    white = Image.new("RGBA", (S, S), (255, 255, 255, 0))
    white.putalpha(halo)
    img.alpha_composite(drop_shadow(white, (0, 12), 16, 90))
    img.alpha_composite(white)

    img.alpha_composite(shape)
    d = ImageDraw.Draw(img)
    dark = (31, 41, 55, 255)
    d.polygon(body, outline=dark, width=16)
    d.ellipse([cx - hr, hy - hr, cx + hr, hy + hr], outline=dark, width=16)
    collar(d, cx, hy, hr, dark, 12)
    d.line([(cx, hy - hr), (cx, hy - hr - 70)], fill=dark, width=12)
    d.arc([cx - 20, hy - hr - 108, cx + 20, hy - hr - 66], start=0, end=360, fill=dark, width=12)
    face(d, cx, hy + hr * 0.02, hr, "dots", dark, (255, 122, 89, 170), 12)
    return img

def style_glass():
    img = bg_rounded(lin_grad((S, S), "#4B21C7", "#00B3F0", 125), 0.24)
    blob = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    bd = ImageDraw.Draw(blob)
    bd.ellipse([S * 0.04, S * 0.08, S * 0.56, S * 0.62], fill=(255, 255, 255, 105))
    bd.ellipse([S * 0.48, S * 0.44, S * 1.02, S * 1.0], fill=(255, 255, 255, 60))
    img.alpha_composite(blob.filter(ImageFilter.GaussianBlur(42)))
    cx, hy, hr, ln, wt, wb = geometry()
    body = body_points(cx, hy, hr, ln, wt, wb)
    glass = Image.new("RGBA", (S, S), (255, 255, 255, 175))
    gm = Image.new("L", (S, S), 0)
    gd = ImageDraw.Draw(gm)
    gd.polygon(body, fill=255)
    gd.ellipse([cx - hr, hy - hr, cx + hr, hy + hr], fill=255)
    glass.putalpha(gm)
    img.alpha_composite(glass)
    d = ImageDraw.Draw(img)
    d.polygon(body, outline=(255, 255, 255, 250), width=16)
    d.ellipse([cx - hr, hy - hr, cx + hr, hy + hr], outline=(255, 255, 255, 250), width=16)
    d.polygon(body, outline=(46, 108, 190, 120), width=5)
    d.ellipse([cx - hr, hy - hr, cx + hr, hy + hr], outline=(46, 108, 190, 120), width=5)
    collar(d, cx, hy, hr, (255, 255, 255, 210), 8)
    d.arc([cx - hr * 0.74, hy - hr * 0.8, cx + hr * 0.34, hy + hr * 0.2],
          start=205, end=320, fill=(255, 255, 255, 210), width=14)
    d.line([(cx, hy - hr), (cx, hy - hr - 72)], fill=(255, 255, 255, 230), width=8)
    face(d, cx, hy + hr * 0.02, hr, "dots", (255, 255, 255, 255), None, 10)
    return img


def style_mint():
    img = bg_rounded(lin_grad((S, S), "#00D9A5", "#00A6DB", 135), 0.26)
    layer, (cx, hy, hr, *_rest) = base_doll(hex2rgb("#FF5A36") + (255,))
    img.alpha_composite(drop_shadow(layer, (0, 16), 22, 90))
    img.alpha_composite(layer)
    d = ImageDraw.Draw(img)
    d.ellipse([cx - hr, hy - hr, cx + hr, hy + hr], fill=hex2rgb("#FFD24A") + (255,))
    collar(d, cx, hy, hr, (255, 245, 205, 225), 9)
    d.line([(cx, hy - hr), (cx, hy - hr - 72)], fill=(255, 255, 255, 245), width=10)
    face(d, cx, hy + hr * 0.02, hr, "dots", (58, 36, 24, 255), None, 12)
    return img


STYLES = {
    "01-sunny": style_sunny,
    "02-sky": style_sky,
    "03-candy": style_candy,
    "04-neon": style_neon,
    "05-crayon": style_crayon,
    "06-matcha": style_matcha,
    "07-sticker": style_sticker,
    "08-glass": style_glass,
    "09-mint": style_mint,
}


def save_all(name, img):
    big = img.resize((512, 512), Image.LANCZOS)
    big.save(os.path.join(OUT, f"{name}.png"))
    big.resize((256, 256), Image.LANCZOS).save(os.path.join(OUT, f"{name}-256.png"))
    big.save(os.path.join(OUT, f"{name}.ico"), sizes=[(s, s) for s in ICO_SIZES])
    return big


def main():
    thumbs = []
    for name, fn in STYLES.items():
        thumbs.append((name, save_all(name, fn())))
        print("ok", name)
    cols, pad, cell = 3, 22, 300
    rows = (len(thumbs) + cols - 1) // cols
    sheet = Image.new("RGBA", (cols * cell + pad * 2, rows * cell + pad * 2), (243, 245, 250, 255))
    for i, (_n, th) in enumerate(thumbs):
        x, y = pad + (i % cols) * cell, pad + (i // cols) * cell
        sheet.alpha_composite(th.resize((cell - 18, cell - 18), Image.LANCZOS), (x + 9, y + 9))
    sheet.save(os.path.join(OUT, "00-preview.png"))

    # 小尺寸对照（真实 16/24/32/48 像素）
    sizes = [48, 32, 24, 16]
    small = Image.new("RGBA", (len(thumbs) * 70, sum(s for s in sizes) + 40), (243, 245, 250, 255))
    y = 10
    for sp in sizes:
        for i, (_n, th) in enumerate(thumbs):
            small.alpha_composite(th.resize((sp, sp), Image.LANCZOS), (10 + i * 70, y))
        y += sp + 8
    small.save(os.path.join(OUT, "00-small-sizes.png"))
    print("done")


if __name__ == "__main__":
    main()
