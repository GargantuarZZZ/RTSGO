#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""校验渲染三视图的正视图朝向，并把 front/side/top 合成一张拼图。

用法:
    python MakeTriViewSheets.py triview_base refs_base
"""
import os
import sys

from PIL import Image, ImageDraw


def mask(img, border=None):
    img = img.convert("RGB")
    if border is None:
        border = img.getpixel((4, 4))
    w, h = img.size
    out = Image.new("L", (w, h), 0)
    px = out.load()
    src = img.load()
    for y in range(h):
        for x in range(w):
            r, g, b = src[x, y]
            if abs(r - border[0]) + abs(g - border[1]) + abs(b - border[2]) > 18:
                px[x, y] = 255
    return out


def iou(a, b, size=128):
    a = a.resize((size, size), Image.NEAREST)
    b = b.resize((size, size), Image.NEAREST)
    pa, pb = a.load(), b.load()
    inter = union = 0
    for y in range(size):
        for x in range(size):
            va = 1 if pa[x, y] > 32 else 0
            vb = 1 if pb[x, y] > 32 else 0
            inter += va & vb
            union += va | vb
    return inter / max(1, union)


def main():
    tri_base = sys.argv[1]
    refs_base = sys.argv[2]
    dirs = sorted(
        d for d in os.listdir(tri_base)
        if os.path.isdir(os.path.join(tri_base, d)) and d[:3].isdigit()
    )
    for d in dirs:
        tdir = os.path.join(tri_base, d)
        front = os.path.join(tdir, "ortho_front.png")
        side = os.path.join(tdir, "ortho_side.png")
        top = os.path.join(tdir, "ortho_top.png")
        if not all(os.path.exists(p) for p in (front, side, top)):
            print(f"{d}: missing views")
            continue
        imgs = [Image.open(p).convert("RGB") for p in (front, side, top)]
        canvas = Image.new("RGB", (1024 * 3, 1024), (255, 255, 255))
        for i, im in enumerate(imgs):
            canvas.paste(im.resize((1024, 1024), Image.LANCZOS), (i * 1024, 0))
        draw = ImageDraw.Draw(canvas)
        for i, label in enumerate(("FRONT", "SIDE", "TOP")):
            draw.rectangle([i * 1024 + 10, 10, i * 1024 + 170, 44], fill=(20, 20, 24))
            draw.text((i * 1024 + 22, 16), label, fill=(255, 255, 255))
        sheet = os.path.join(tdir, "triview.png")
        canvas.save(sheet)

        ref_front = os.path.join(refs_base, d, "front.png")
        if os.path.exists(ref_front):
            sim = iou(mask(Image.open(front)), mask(Image.open(ref_front)))
            print(f"{d}: front IoU={sim:.2f} sheet={sheet}")
        else:
            print(f"{d}: sheet={sheet} (no ref)")
    print("SHEETS_DONE")


if __name__ == "__main__":
    main()
