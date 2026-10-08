"""Rebuild original geometric branding assets. Dev-only: Python + Pillow + NumPy."""
from pathlib import Path
import math
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src/MusicFormatConverter.App/Assets"
INSTALLER = ROOT / "installer/branding"
ASSETS.mkdir(parents=True, exist_ok=True)
INSTALLER.mkdir(parents=True, exist_ok=True)
S = 1024
yy, xx = np.mgrid[0:S, 0:S].astype(float)
t = np.clip((xx + yy * .3) / (S * 1.3), 0, 1)[..., None]
rgb = np.uint8(np.array([50, 228, 250]) * (1 - t) + np.array([152, 103, 255]) * t)
gradient = Image.fromarray(rgb).convert("RGBA")
base = np.zeros((S, S, 3), dtype=float) + [9, 15, 31]
glow = np.exp(-(((xx - 300) / 720) ** 2 + ((yy - 150) / 610) ** 2))[:, :, None]
base += glow * [10, 20, 33]
image = Image.fromarray(np.uint8(np.clip(base, 0, 255))).convert("RGBA")
shape = Image.new("L", (S, S))
d = ImageDraw.Draw(shape)
d.rounded_rectangle((48, 48, 976, 976), radius=220, fill=255)
image.putalpha(shape)
rim = Image.new("RGBA", (S, S))
d = ImageDraw.Draw(rim)
d.rounded_rectangle((50, 50, 974, 974), radius=220, outline=(122, 186, 224, 95), width=3)
d.rounded_rectangle((64, 64, 960, 960), radius=205, outline=(108, 152, 226, 28), width=2)
image = Image.alpha_composite(image, rim)

mask = Image.new("L", (S, S))
d = ImageDraw.Draw(mask)
box = (178, 178, 846, 846)
d.arc(box, start=208, end=346, fill=255, width=33)
d.arc(box, start=28, end=166, fill=255, width=33)
for end in (166, 346):
    a = math.radians(end)
    x, y = 512 + 319 * math.cos(a), 512 + 319 * math.sin(a)
    tangent = (-math.sin(a), math.cos(a))
    normal = (math.cos(a), math.sin(a))
    points = [(x + tangent[0] * 27, y + tangent[1] * 27),
              (x - tangent[0] * 31 + normal[0] * 28, y - tangent[1] * 31 + normal[1] * 28),
              (x - tangent[0] * 31 - normal[0] * 28, y - tangent[1] * 31 - normal[1] * 28)]
    d.polygon(points, fill=255)
for x, height in zip((336, 424, 512, 600, 688), (122, 228, 352, 228, 122)):
    d.rounded_rectangle((x - 23, 512 - height // 2, x + 23, 512 + height // 2), radius=23, fill=255)
halo = gradient.copy()
halo.putalpha(mask.filter(ImageFilter.GaussianBlur(23)).point(lambda p: round(p * .32)))
image = Image.alpha_composite(image, halo)
foreground = gradient.copy()
foreground.putalpha(mask)
image = Image.alpha_composite(image, foreground)
image.save(ASSETS / "app-icon.png")
image.save(ASSETS / "app-icon.ico", sizes=[(n, n) for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
image.save(ASSETS / "app-icon.icns")

# Static wizard brand panel; all actual controls remain native accessible Inno widgets.
W, H = 360, 700
panel = Image.new("RGB", (W, H), "#0B1020")
draw = ImageDraw.Draw(panel)
for x in range(-500, 900, 48):
    draw.line((W // 2, 300, x, H), fill="#142339", width=1)
for y in (440, 480, 530, 590, 665):
    draw.line((0, y, W, y), fill="#142339", width=1)
panel.paste(image.resize((224, 224), Image.Resampling.LANCZOS), (68, 76),
            image.resize((224, 224), Image.Resampling.LANCZOS))
font_root = Path("C:/Windows/Fonts")
def font(size, bold=False):
    path = font_root / ("segoeuib.ttf" if bold else "segoeui.ttf")
    return ImageFont.truetype(str(path), size) if path.exists() else ImageFont.load_default(size=size)
draw.text((40, 324), "MUSIC", fill="#EFF7FF", font=font(35, True))
draw.text((40, 365), "FORMAT CONVERTER", fill="#99ACCC", font=font(19))
draw.line((40, 411, 110, 411), fill="#44DCEC", width=3)
draw.text((40, 444), "LOCAL. PRIVATE.", fill="#D7E5F8", font=font(18, True))
draw.text((40, 473), "BUILT FOR YOUR MUSIC.", fill="#869CBA", font=font(15))
for x, h in enumerate((12, 24, 14, 32, 46, 21, 68, 36, 20, 51, 29, 17, 40, 23, 12)):
    left = 40 + x * 19
    c = tuple(rgb[512, min(1023, x * 70)])
    draw.rounded_rectangle((left, 585 - h // 2, left + 7, 585 + h // 2), radius=3, fill=c)
draw.text((40, 653), "OFFLINE  /  HIGH FIDELITY", fill="#6884A7", font=font(13))
panel.save(INSTALLER / "wizard-panel.bmp")
panel.save(INSTALLER / "wizard-panel.png")
small = Image.new("RGB", (64, 64), "#0B1020")
small.paste(image.resize((64, 64), Image.Resampling.LANCZOS), (0, 0),
            image.resize((64, 64), Image.Resampling.LANCZOS))
small.save(INSTALLER / "wizard-small.bmp")
preview = Image.new("RGB", (800, 420), "#080D18")
preview.paste(image.resize((340, 340), Image.Resampling.LANCZOS), (40, 40),
              image.resize((340, 340), Image.Resampling.LANCZOS))
p = ImageDraw.Draw(preview)
p.text((438, 67), "MUSIC CONVERTER", font=font(24, True), fill="#E5F0FF")
p.text((438, 108), "ONE IDENTITY. EVERYWHERE.", font=font(14), fill="#8199BD")
for x, size in zip((438, 534, 608, 665), (64, 48, 32, 24)):
    icon = image.resize((size, size), Image.Resampling.LANCZOS)
    preview.paste(icon, (x, 190), icon)
    p.text((x, 270), str(size), font=font(12), fill="#8199BD")
preview.save(INSTALLER / "brand-preview.png")
print("Generated app PNG/ICO/ICNS and wizard artwork; no runtime generation dependency.")
