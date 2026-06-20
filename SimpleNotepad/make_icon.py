from PIL import Image, ImageDraw

S = 1024  # supersample canvas; downscaled later for crisp edges
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)


def rounded(draw, box, r, fill):
    draw.rounded_rectangle(box, radius=r, fill=fill)


def vgrad(size, top, bottom):
    """Vertical gradient image."""
    w, h = size
    grad = Image.new("RGBA", (w, h))
    gd = ImageDraw.Draw(grad)
    for y in range(h):
        t = y / (h - 1)
        col = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
        gd.line([(0, y), (w, y)], fill=col)
    return grad


# --- App tile: rounded square with blue -> teal gradient ---
margin = int(S * 0.06)
tile_box = (margin, margin, S - margin, S - margin)
tile_r = int(S * 0.20)

grad = vgrad((S, S), (0x16, 0x9F, 0xD8), (0x10, 0x8A, 0x7C))  # azure -> teal
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle(tile_box, radius=tile_r, fill=255)
img.paste(grad, (0, 0), mask)

d = ImageDraw.Draw(img)

# --- White note/document, slightly inset ---
nm = int(S * 0.22)
note_box = (nm, int(S * 0.20), S - nm, S - int(S * 0.18))
note_r = int(S * 0.045)
# soft drop shadow
shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
sd = ImageDraw.Draw(shadow)
off = int(S * 0.015)
sd.rounded_rectangle(
    (note_box[0] + off, note_box[1] + off, note_box[2] + off, note_box[3] + off),
    radius=note_r, fill=(0, 0, 0, 70))
shadow = shadow.filter(__import__("PIL.ImageFilter", fromlist=["GaussianBlur"]).GaussianBlur(int(S * 0.012)))
img.alpha_composite(shadow)

d = ImageDraw.Draw(img)
rounded(d, note_box, note_r, (0xFB, 0xFB, 0xFD, 255))

# --- Text lines on the note ---
left = note_box[0] + int(S * 0.07)
right = note_box[2] - int(S * 0.07)
top = note_box[1] + int(S * 0.11)
gap = int(S * 0.085)
lh = int(S * 0.028)

# accent (title) line - teal
d.rounded_rectangle((left, top, left + int((right - left) * 0.55), top + int(lh * 1.4)),
                    radius=lh, fill=(0x16, 0x9F, 0xD8, 255))
# gray body lines
y = top + gap + int(S * 0.02)
widths = [1.0, 0.82, 0.92, 0.6]
for w in widths:
    d.rounded_rectangle((left, y, left + int((right - left) * w), y + lh),
                        radius=lh // 2, fill=(0xC9, 0xCF, 0xD6, 255))
    y += gap

# --- Downscale with antialiasing and export multi-size .ico ---
final = img.resize((256, 256), Image.LANCZOS)
final.save(r"C:\skillUp\AI app\simple notepad\SimpleNotepad\app.png")
final.save(
    r"C:\skillUp\AI app\simple notepad\SimpleNotepad\app.ico",
    sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
)
print("icon written")
