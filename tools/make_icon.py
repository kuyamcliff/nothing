"""Draws the ClipFx Manager icon (gauge on a graphite tile) and writes assets/clipfx.ico + PNGs."""
import math, os
from PIL import Image, ImageDraw

S = 1024
def draw(size=S):
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = 40
    # tile with a subtle vertical gradient
    tile = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        t = y / S
        c = tuple(int(a + (b - a) * t) for a, b in zip((62, 62, 69), (36, 36, 41)))
        gd.line([(0, y), (S, y)], fill=c + (255,))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([pad, pad, S - pad, S - pad], radius=230, fill=255)
    img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)
    cx, cy, r, w = S / 2, S * 0.56, S * 0.29, int(S * 0.075)
    box = [cx - r, cy - r, cx + r, cy + r]
    d.arc(box, start=150, end=390, fill=(236, 236, 239, 255), width=w)
    for ang in (150, 390):  # round caps
        a = math.radians(ang)
        x, y = cx + (r - w / 2) * math.cos(a), cy + (r - w / 2) * math.sin(a)
        d.ellipse([x - w / 2, y - w / 2, x + w / 2, y + w / 2], fill=(236, 236, 239, 255))
    a = math.radians(-45)
    nx, ny = cx + r * 0.62 * math.cos(a), cy + r * 0.62 * math.sin(a)
    d.line([(cx, cy), (nx, ny)], fill=(236, 236, 239, 255), width=int(w * 0.95))
    for (x, y), rr in (((nx, ny), w * 0.47), ((cx, cy), w * 0.85)):
        d.ellipse([x - rr, y - rr, x + rr, y + rr], fill=(236, 236, 239, 255))
    return img

img = draw()
os.makedirs("assets", exist_ok=True)
img.save("assets/clipfx.ico", sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
img.resize((256, 256), Image.LANCZOS).save("assets/clipfx-256.png")
img.resize((64, 64), Image.LANCZOS).save("src/ClipFx.Server/wwwroot/favicon.png")
print("ok")
