from PIL import Image, ImageDraw
import os

S = 512
MARGIN = 26
RADIUS = 108
INK = (52, 120, 246, 255)     # accent blue, same as launchpad
PLATE = (255, 255, 255, 238)

def base():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([MARGIN, MARGIN, S - MARGIN, S - MARGIN], radius=RADIUS, fill=PLATE)
    return img, d

outdir = os.path.dirname(__file__)

# ---------- launchpad: 3x3 grid ----------
img, d = base()
gx0, gy0, gx1, gy1 = MARGIN + 96, MARGIN + 96, S - MARGIN - 96, S - MARGIN - 96
cw = (gx1 - gx0) / 3; ch = (gy1 - gy0) / 3
dot = 54
for r in range(3):
    for c in range(3):
        cx = gx0 + cw * (c + 0.5); cy = gy0 + ch * (r + 0.5)
        d.rounded_rectangle([cx - dot/2, cy - dot/2, cx + dot/2, cy + dot/2], radius=16, fill=INK)
img.save(os.path.join(outdir, "launchpad.png")); print("launchpad", os.path.getsize(os.path.join(outdir,"launchpad.png")))

# ---------- control center: two slider tracks with knobs ----------
img, d = base()
left = MARGIN + 108
right = S - MARGIN - 108
track_w = 18
# two tracks
for i, ky in enumerate([196, 316]):
    ty = ky
    d.rounded_rectangle([left, ty - track_w//2, right, ty + track_w//2], radius=track_w//2, fill=(52,120,246,90))
    # knob position: top track knob right, bottom track knob left
    kx = right - 30 if i == 0 else left + 30
    kr = 34
    d.ellipse([kx - kr, ty - kr, kx + kr, ty + kr], fill=INK)
img.save(os.path.join(outdir, "control.png")); print("control", os.path.getsize(os.path.join(outdir,"control.png")))

# ---------- tray: inbox / items list ----------
img, d = base()
left = MARGIN + 104
right = S - MARGIN - 104
# three rows: small dot + line (system tray items)
rows = [188, 256, 324]
for i, ry in enumerate(rows):
    # dot
    dr = 22
    d.ellipse([left, ry - dr, left + dr*2, ry + dr], fill=INK)
    # line
    lx = left + dr*2 + 22
    d.rounded_rectangle([lx, ry - 13, right, ry + 13], radius=13, fill=(52,120,246,255))
img.save(os.path.join(outdir, "tray.png")); print("tray", os.path.getsize(os.path.join(outdir,"tray.png")))
