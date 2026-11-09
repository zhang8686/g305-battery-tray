"""Generate the application icon (source/app.ico) and a 256px preview.

The mark is an original, stylised "logi" wordmark with a battery bar. It is NOT
the official Logitech logo; Logitech / logi / G305 are trademarks of Logitech.

Pure Pillow, no network. The .ico is committed, so build.ps1 does not need
Python. PNG-compressed ICO entries (Vista+) keep full alpha at every size.
"""
import io
import os
import struct

from PIL import Image, ImageChops, ImageDraw, ImageFont, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT_ICO = os.path.join(HERE, "app.ico")
OUT_PNG = os.path.join(ROOT, "preview", "logo.png")

SIZES = [16, 24, 32, 48, 64, 128, 256]
SS = 8  # supersampling factor

FONT_CANDIDATES = [
    r"C:\Windows\Fonts\segoeuib.ttf",
    r"C:\Windows\Fonts\arialbd.ttf",
    r"C:\Windows\Fonts\calibrib.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
]


def font(px):
    for path in FONT_CANDIDATES:
        if os.path.isfile(path):
            try:
                return ImageFont.truetype(path, px)
            except OSError:
                pass
    return ImageFont.load_default()


def rounded(draw, box, radius, fill):
    draw.rounded_rectangle(box, radius=radius, fill=fill)


def render(size):
    W = size * SS
    im = Image.new("RGBA", (W, W), (0, 0, 0, 0))

    # 1. Background: deep blue vertical gradient inside a rounded square.
    grad = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grad)
    top, bottom = (28, 78, 112), (9, 22, 36)
    for y in range(W):
        t = y / float(max(1, W - 1))
        c = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
        gd.line((0, y, W, y), fill=c)
    mask = Image.new("L", (W, W), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, W - 1, W - 1), radius=int(W * 0.22), fill=255)
    im.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(im, "RGBA")

    # 2. Soft diagonal glass highlight in the upper-left corner.
    gloss = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    gdr = ImageDraw.Draw(gloss, "RGBA")
    gdr.ellipse((-W * 0.55, -W * 0.85, W * 0.95, W * 0.35), fill=(255, 255, 255, 46))
    gloss = gloss.filter(ImageFilter.GaussianBlur(W * 0.06))
    # Clip the highlight to the rounded square so the corners stay fully transparent.
    gloss.putalpha(ImageChops.multiply(gloss.getchannel("A"), mask))
    im = Image.alpha_composite(im, gloss)
    d = ImageDraw.Draw(im, "RGBA")

    # 3. Thin inner rim keeps the mark separated on dark taskbars.
    d.rounded_rectangle((W * 0.012, W * 0.012, W * 0.988, W * 0.988),
                        radius=int(W * 0.21), outline=(255, 255, 255, 52), width=max(1, int(W * 0.008)))

    simple = size <= 24   # 16/24px keep just the wordmark; 32px+ get the teal "i" dot
    teal = (0, 208, 178, 255)
    white = (250, 252, 253, 255)

    # 4. The wordmark: "log" in white + a hand-drawn "i" with a teal dot.
    if simple:
        fs = int(W * 0.47)
        f = font(fs)
        text = "logi"
        bbox = d.textbbox((0, 0), text, font=f)
        tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
        d.text(((W - tw) / 2 - bbox[0], (W - th) / 2 - bbox[1] - W * 0.025),
               text, font=f, fill=white)
    else:
        with_bar = size >= 48
        fs = int(W * (0.40 if with_bar else 0.45))
        f = font(fs)
        word = "log"
        bbox = d.textbbox((0, 0), word, font=f)
        tw = bbox[2] - bbox[0]
        iw = W * 0.075           # i stem width
        gap = W * 0.035
        total = tw + gap + iw
        x0 = (W - total) / 2
        top_y = W * (0.225 if with_bar else 0.255)
        d.text((x0 - bbox[0], top_y - bbox[1]), word, font=f, fill=white)

        # "i": rounded stem + teal dot, vertically aligned with the x-height.
        stem_top = top_y + fs * 0.36
        stem_bottom = top_y + fs * 0.94
        ix = x0 + tw + gap
        rounded(d, (ix, stem_top, ix + iw, stem_bottom), iw * 0.42, white)
        dot_r = iw * 0.72
        dot_cx = ix + iw / 2
        dot_cy = top_y + fs * 0.13
        d.ellipse((dot_cx - dot_r, dot_cy - dot_r, dot_cx + dot_r, dot_cy + dot_r), fill=teal)

        # 5. Small charge bar underneath (48px+ only; it turns to mush below that).
        if with_bar:
            bx0, bx1 = W * 0.245, W * 0.715
            by0, by1 = W * 0.735, W * 0.815
            rounded(d, (bx0, by0, bx1, by1), (by1 - by0) * 0.48, (255, 255, 255, 54))
            pad = (by1 - by0) * 0.18
            fill_right = bx0 + (bx1 - bx0) * 0.72
            rounded(d, (bx0 + pad, by0 + pad, fill_right, by1 - pad), (by1 - by0) * 0.32, teal)
            # terminal nub
            rounded(d, (bx1 + W * 0.012, by0 + (by1 - by0) * 0.28,
                        bx1 + W * 0.038, by1 - (by1 - by0) * 0.28),
                    W * 0.01, (255, 255, 255, 110))

    return im.resize((size, size), Image.LANCZOS)


def make_ico(images, path):
    header = b"\x00\x00\x01\x00" + struct.pack("<H", len(images))
    offset = 6 + 16 * len(images)
    entries, body = b"", b""
    for size, png in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset)
        body += png
        offset += len(png)
    with open(path, "wb") as fh:
        fh.write(header + entries + body)


def main():
    os.makedirs(os.path.join(ROOT, "preview"), exist_ok=True)
    icons = []
    for size in SIZES:
        img = render(size)
        buf = io.BytesIO()
        img.save(buf, format="PNG", optimize=True)
        icons.append((size, buf.getvalue()))
        if size == 256:
            img.save(OUT_PNG)
    make_ico(icons, OUT_ICO)
    print("wrote", OUT_ICO, os.path.getsize(OUT_ICO), "bytes")
    print("wrote", OUT_PNG)


if __name__ == "__main__":
    main()
